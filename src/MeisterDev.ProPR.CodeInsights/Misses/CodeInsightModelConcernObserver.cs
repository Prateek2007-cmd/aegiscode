// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Misses;

/// <summary>Retains independent human concerns and model matching without changing legacy miss collection.</summary>
public sealed partial class CodeInsightModelConcernObserver(
    MeisterProPRDbContext db,
    IHumanMissClassifier truth,
    IHumanConcernModelMatcher matcher,
    ISecretProtectionCodec secrets,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightModelConcernObserver> logger,
    IDbContextFactory<MeisterProPRDbContext>? factory = null,
    IModelReviewEvidenceReader? evidenceReader = null,
    IFindingTypeClassifier? typeClassifier = null,
    ICodeInsightTaxonomyService? taxonomy = null) : ICodeInsightModelConcernObserver
{
    internal const string DiscussionPurpose = "code-insight-model-concern-discussion";
    private CodeInsightDiscussionBatch? _batch;
    private ModelReviewEvidenceSnapshot _executions = new([], false, "model-proof-unavailable");
    public string DependencyVersion => "model-concern-v1:" + truth.ClassifierVersion + ":" + matcher.ClassifierVersion;

    public async Task<string> PrepareAsync(CodeInsightDiscussionBatch batch, CancellationToken ct = default)
    {
        this._batch = batch;
        await using var lease = await CodeInsightDbContextLease.CreateAsync(db, factory, ct);
        this._executions = await (evidenceReader ?? new ModelReviewEvidenceReader(lease.Context)).ReadAsync(
            [new(batch.Key.ClientId, batch.Key.RepositoryId, batch.Key.PullRequestId, batch.ProviderScope, batch.ConnectionId)], DateTimeOffset.UtcNow, ct);
        return ModelReviewCapture.Digest(
            JsonSerializer.Serialize(
                new
                {
                    version = this.DependencyVersion, batch.DependencyVersion, batch.ConnectionId, this._executions.Complete,
                    this._executions.UnavailableReason,
                    jobs = this._executions.Executions.OrderBy(job => job.JobId).Select(job => new
                    {
                        job.JobId, job.Source, job.RevisionKey, job.Manifest,
                        files = job.Files.Select(file => new
                        {
                            file.FilePath, file.InputFingerprint, file.InputComplete, file.InventoryComplete, file.AssignedRanges,
                            passes = file.Passes.Select(pass => new
                            {
                                pass.PassKey, pass.ModelId, pass.LogicalModelName, pass.Disposition, pass.AcceptedAt,
                                candidates = pass.Candidates.Select(candidate => candidate.Id)
                            }),
                        }),
                    }),
                }));
    }

    public async Task<ModelConcernAcknowledgment> ObserveAsync(ThreadUpdatedEvent observation, CancellationToken ct = default)
    {
        if (observation.ObservedAt is not { } observedAt || !await gate.IsCollectionEnabledAsync(observation.ClientId, ct)
                                                         || ModelConcernIdentity.Of(observation) is not { } native)
        {
            return new(false, false);
        }

        try
        {
            var key = new CodeInsightPullRequestKey(observation.ClientId, observation.RepositoryId, observation.PullRequestId);
            var scope = string.IsNullOrWhiteSpace(observation.ProviderScope) ? "" : observation.ProviderScope;
            if (this._batch is not { } batch || batch.Key != key || batch.ProviderScope != scope || batch.ConnectionId != observation.ConnectionId)
            {
                await this.PrepareAsync(new(key, scope, [], "direct", observation.ConnectionId), ct);
            }

            var version = "human-concern-v1:" + truth.ClassifierVersion + ":" + this._batch!.DependencyVersion;
            var fingerprint = SourceFingerprint(observation, version);
            await using var readLease = await CodeInsightDbContextLease.CreateAsync(db, factory, ct);
            var current = await readLease.Context.CodeInsightHumanConcerns.AsNoTracking().SingleOrDefaultAsync(row => row.NativeIdentity == native, ct);
            var humanEvidence =
                observation.Comments.Any(comment => !comment.IsAiAuthored && !comment.IsSystemGenerated && !string.IsNullOrWhiteSpace(comment.Text));
            var discussion = BuildDiscussion(observation);
            var resolution = CodeInsightNativeResolution.InterpretKnown(observation.Status);
            var resolved = resolution is not null && resolution != ThreadResolutionIntent.Active;
            var same = current?.SourceFingerprint == fingerprint;
            HumanMissJudgement? judgement = null;
            var asked = false;
            string? unavailable = null;
            if (!observation.IsDeleted && humanEvidence)
            {
                if (same && current is { JudgementFailed: false, ClassifierConfidence: not null, IsHumanConfirmed: not null, IsSingleConcern: not null })
                {
                    judgement = new(
                        current.IsSubstantive, current.WasActedOn, current.IsInScope, current.ClassifierConfidence.Value, "Retained judgement",
                        current.IsHumanConfirmed.Value, current.IsSingleConcern.Value);
                }
                else if (resolution is null)
                {
                    unavailable = "model-concern-resolution-unavailable";
                }
                else if (discussion.Length > 6000)
                {
                    unavailable = "model-concern-input-limit-exceeded";
                }
                else if (!same || current!.FailedAttempts < 3)
                {
                    var root = observation.Comments.FirstOrDefault(comment => !comment.IsSystemGenerated);
                    var request = new HumanMissJudgementRequest(
                        observation.ClientId, observation.ThreadId, observation.FilePath, discussion, resolved,
                        IndependentConcern: true, ReviewerOwnedRoot: root?.IsAiAuthored == true);
                    if (truth is IHumanMissClassifierAttemptReporter reporter)
                    {
                        var result = await reporter.JudgeWithAttemptAsync(request, ct);
                        judgement = result.Judgement;
                        asked = result.ModelWasAsked;
                    }
                    else
                    {
                        asked = true;
                        judgement = await truth.JudgeAsync(request, ct);
                    }
                }

                unavailable ??= judgement is null ? "model-concern-judgement-failed" : !judgement.IsSingleConcern ? "model-concern-ambiguous" : null;
            }

            if (!await gate.IsCollectionEnabledAsync(observation.ClientId, ct))
            {
                return new(false, false);
            }

            var idForMatching = current?.Id ?? Guid.CreateVersion7();
            var dimensions = await this.ClassifyDimensionsAsync(observation, current, same, judgement, discussion, ct);
            var decisions = !observation.IsDeleted && humanEvidence &&
                            judgement is { IsHumanConfirmed: true, IsSubstantive: true, IsInScope: true, IsSingleConcern: true }
                            && resolved
                ? await this.MatchModelsAsync(readLease.Context, observation, idForMatching, fingerprint, discussion, ct)
                : [];
            await using var writeLease = await CodeInsightDbContextLease.CreateAsync(db, factory, ct);
            var write = writeLease.Context;
            await using var transaction = write.Database.IsRelational() ? await write.Database.BeginTransactionAsync(ct) : null;
            await CodeInsightAggregateWriteLock.AcquireAsync(write, key, ct);
            var stored = await write.CodeInsightHumanConcerns.SingleOrDefaultAsync(row => row.NativeIdentity == native, ct);
            if (stored is not null && stored.ObservedAt > observedAt)
            {
                return new(true, !stored.JudgementFailed || stored.FailedAttempts >= 3);
            }

            var excluded = observation.IsDeleted || !humanEvidence;
            if (stored is null && excluded)
            {
                return new(true, true);
            }

            var aggregate = await CodeInsightPullRequestPersistence.GetOrCreatePullRequestAsync(write, key, null, observation.LastActivityAt, ct);
            if (stored is null)
            {
                var id = idForMatching;
                stored = new() { Id = id, CodeInsightPullRequestId = aggregate.Id, NativeIdentity = native, CanonicalConcernId = id, HarvestedAt = observedAt };
                write.CodeInsightHumanConcerns.Add(stored);
            }

            var equivalenceBounded = true;
            if (stored.SourceFingerprint != fingerprint || excluded != stored.IsDeleted)
            {
                equivalenceBounded = await InvalidateEquivalenceAsync(write, stored, ct);
            }

            var preserveTruth = stored.SourceFingerprint == fingerprint && !stored.JudgementFailed && judgement is null && !excluded;
            stored.ProviderScope = scope;
            stored.ConnectionId = observation.ConnectionId;
            stored.ProviderThreadId = observation.ThreadId ?? "";
            stored.ObservedAt = observedAt;
            stored.IsDeleted = excluded;
            stored.Anchor = observation.ConcernAnchor;
            if (!preserveTruth)
            {
                var sameStored = stored.SourceFingerprint == fingerprint;
                stored.FailedAttempts = judgement is null && asked ? (sameStored ? stored.FailedAttempts : 0) + 1 : sameStored ? stored.FailedAttempts : 0;
                stored.SourceFingerprint = fingerprint;
                stored.ClassifierVersion = version;
                stored.ClassifierConfidence = judgement?.Confidence;
                stored.IsHumanConfirmed = judgement?.IsHumanConfirmed;
                stored.IsSingleConcern = judgement?.IsSingleConcern;
                stored.IsSubstantive = judgement?.IsSubstantive == true;
                stored.IsInScope = judgement?.IsInScope == true;
                stored.WasActedOn = judgement?.WasActedOn == true;
                stored.ThreadResolved = resolved;
                stored.JudgementFailed = !excluded && (judgement is null || !judgement.IsSingleConcern);
                stored.UnavailableReason = unavailable;
                var relevant = observation.Comments.Where(comment => !comment.IsSystemGenerated && !string.IsNullOrWhiteSpace(comment.Text)).Take(101)
                    .ToArray();
                var sourceBounded = relevant.Length <= 100 && discussion.Length <= 6000 && observation.ThreadId is null or { Length: <= 256 }
                                    && observation.FilePath is null or { Length: <= 2048 }
                                    && relevant.All(comment =>
                                        comment.CommentId.Length <= 256 && comment.AuthorIdentity.Length <= 256 && comment.Text.Length <= 6000);
                var retainedSource = sourceBounded
                    ? JsonSerializer.Serialize(new RetainedConcernSource("human-concern-source-v1", observation with { Comments = relevant }))
                    : "";
                stored.RetainedSourceComplete = sourceBounded && retainedSource.Length <= 32_000;
                stored.EncryptedDiscussion = secrets.Protect(
                    stored.RetainedSourceComplete ? retainedSource
                    : discussion.Length <= 32_000 ? discussion : discussion[..32_000], DiscussionPurpose);
            }

            if (dimensions is { } classified)
            {
                stored.TypeMembership = string.Join('|', classified.CoreSlugs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                stored.Qualifier = classified.Qualifier;
                stored.DimensionClassifierVersion = typeClassifier?.ClassifierVersion;
                stored.DimensionJudgementFailed = false;
                stored.FailedDimensionAttempts = 0;
            }
            else if (judgement is { IsSubstantive: true } && typeClassifier is not null && taxonomy is not null
                     && (!same || current?.DimensionClassifierVersion != typeClassifier.ClassifierVersion || current.DimensionJudgementFailed))
            {
                stored.DimensionJudgementFailed = true;
                stored.FailedDimensionAttempts++;
            }

            foreach (var decision in decisions)
            {
                var retained = await write.CodeInsightModelConcernEvidence.SingleOrDefaultAsync(
                    row => row.HumanConcernId == stored.Id
                           && row.ModelId == decision.ModelId && row.LogicalModelName == decision.LogicalModelName, ct);
                if (retained is not null && retained.ObservedAt > decision.ObservedAt)
                {
                    continue;
                }

                if (retained is null)
                {
                    decision.HumanConcernId = stored.Id;
                    write.CodeInsightModelConcernEvidence.Add(decision);
                }
                else
                {
                    decision.Id = retained.Id;
                    decision.HumanConcernId = stored.Id;
                    write.Entry(retained).CurrentValues.SetValues(decision);
                }
            }

            equivalenceBounded &= await this.MergeEquivalentConcernsAsync(write, stored, decisions, ct);
            if (!equivalenceBounded)
            {
                stored.JudgementFailed = true;
                stored.UnavailableReason = "model-concern-equivalence-limit-exceeded";
            }

            if (!write.Database.IsNpgsql())
            {
                write.CodeInsightPerformanceDirty.Add(new() { Id = Guid.CreateVersion7(), CodeInsightPullRequestId = aggregate.Id });
            }

            await write.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            var permanent = stored.UnavailableReason is "model-concern-input-limit-exceeded" or "model-concern-resolution-unavailable"
                or "model-concern-ambiguous" or "model-concern-equivalence-limit-exceeded";
            return new(
                true, (!stored.JudgementFailed || stored.FailedAttempts >= 3 || permanent)
                      && decisions.All(decision => decision.Decision != "unknown" || decision.FailedAttempts >= 3 || !Retryable(decision.UnavailableReason)),
                true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Independent human concern retention unavailable: {FailureType}", exception.GetType().Name);
            return new(false, false);
        }
    }

    public async Task<bool> CompleteEnumerationAsync(
        CodeInsightPullRequestKey key, string providerScope, Guid connectionId,
        DateTimeOffset observedAt, bool complete, IReadOnlyList<string> observedThreadIds, CancellationToken ct = default)
    {
        if (connectionId == Guid.Empty || observedThreadIds.Count > 100_000
                                       || observedThreadIds.Any(id => id is not { Length: 64 }) || !await gate.IsCollectionEnabledAsync(key.ClientId, ct))
        {
            return false;
        }

        await using var lease = await CodeInsightDbContextLease.CreateAsync(db, factory, ct);
        var write = lease.Context;
        await using var transaction = write.Database.IsRelational() ? await write.Database.BeginTransactionAsync(ct) : null;
        await CodeInsightAggregateWriteLock.AcquireAsync(write, key, ct);
        var aggregate = await CodeInsightPullRequestPersistence.GetOrCreatePullRequestAsync(write, key, null, observedAt, ct);
        var coverage = await write.CodeInsightHarvestCoverage.SingleOrDefaultAsync(
            row => row.CodeInsightPullRequestId == aggregate.Id && row.ProviderScope == providerScope, ct);
        if (coverage?.ModelConcernObservedAt > observedAt)
        {
            return true;
        }

        coverage ??= new() { Id = Guid.CreateVersion7(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = providerScope, ObservedAt = observedAt };
        if (write.Entry(coverage).State == EntityState.Detached)
        {
            write.CodeInsightHarvestCoverage.Add(coverage);
        }

        var present = observedThreadIds.ToHashSet(StringComparer.Ordinal);
        var concerns = await write.CodeInsightHumanConcerns.Where(row => row.CodeInsightPullRequestId == aggregate.Id
                                                                         && row.ProviderScope == providerScope && row.ConnectionId == connectionId &&
                                                                         row.ObservedAt <= observedAt).ToListAsync(ct);
        foreach (var concern in concerns.Where(row => complete && !present.Contains(row.NativeIdentity)))
        {
            concern.IsDeleted = true;
            concern.ObservedAt = observedAt;
        }

        coverage.ModelConcernEnumerationComplete = complete;
        coverage.ModelConcernConnectionId = connectionId;
        coverage.ModelConcernObservedAt = observedAt;
        if (!write.Database.IsNpgsql())
        {
            write.CodeInsightPerformanceDirty.Add(new() { Id = Guid.CreateVersion7(), CodeInsightPullRequestId = aggregate.Id });
        }

        await write.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return complete;
    }

    public async Task<bool> ReplayRetainedAsync(CodeInsightPullRequestKey key, string providerScope, Guid connectionId, CancellationToken ct = default)
    {
        if (!await gate.IsCollectionEnabledAsync(key.ClientId, ct))
        {
            return false;
        }

        await using var lease = await CodeInsightDbContextLease.CreateAsync(db, factory, ct);
        var rows = await (from concern in lease.Context.CodeInsightHumanConcerns.AsNoTracking()
            join aggregate in lease.Context.CodeInsightPullRequests.AsNoTracking() on concern.CodeInsightPullRequestId equals aggregate.Id
            where aggregate.ClientId == key.ClientId && aggregate.RepositoryId == key.RepositoryId && aggregate.PullRequestId == key.PullRequestId
                  && concern.ProviderScope == providerScope && concern.ConnectionId == connectionId && !concern.IsDeleted
            orderby concern.HarvestedAt, concern.Id
            select concern).Take(101).ToListAsync(ct);
        if (rows.Count == 0 || rows.Count > 100 || rows.Any(row => !row.RetainedSourceComplete))
        {
            return false;
        }

        var allCurrent = true;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var source = this.ReadRetainedSource(row.EncryptedDiscussion);
            if (source is null || source.ClientId != key.ClientId || source.RepositoryId != key.RepositoryId || source.PullRequestId != key.PullRequestId
                || source.ProviderScope != providerScope || source.ConnectionId != connectionId || source.ObservedAt != row.ObservedAt)
            {
                return false;
            }

            var ack = await this.ObserveAsync(source, ct);
            allCurrent &= ack.ConcernRetained && ack.ModelsCurrent;
        }

        return allCurrent;
    }

    private ThreadUpdatedEvent? ReadRetainedSource(string encrypted)
    {
        var text = secrets.Unprotect(encrypted, DiscussionPurpose);
        if (text.Length > 32_000)
        {
            return null;
        }

        try
        {
            var source = JsonSerializer.Deserialize<RetainedConcernSource>(text);
            return source is { Schema: "human-concern-source-v1", Observation: { Comments.Count: <= 100 } observation } ? observation : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string ReadDiscussion(string encrypted) => this.ReadRetainedSource(encrypted) is { } observation
        ? BuildDiscussion(observation)
        : secrets.Unprotect(encrypted, DiscussionPurpose);

    private sealed record RetainedConcernSource(string Schema, ThreadUpdatedEvent Observation);

    private static string BuildDiscussion(ThreadUpdatedEvent observation)
    {
        var builder = new StringBuilder();
        foreach (var comment in observation.Comments.Where(comment => !comment.IsSystemGenerated && !string.IsNullOrWhiteSpace(comment.Text)))
        {
            builder.Append(comment.IsAiAuthored ? "[reviewer] " : "[other author] ").Append(comment.AuthorIdentity).Append(": ");
            var remaining = 32_001 - builder.Length;
            if (remaining <= 0)
            {
                return builder.ToString();
            }

            builder.Append(comment.Text.AsSpan(0, Math.Min(comment.Text.Length, remaining))).Append('\n');
            if (builder.Length > 32_000)
            {
                break;
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string SourceFingerprint(ThreadUpdatedEvent observation, string version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(version);
        Add(
            JsonSerializer.Serialize(
                new
                {
                    observation.ClientId, observation.ConnectionId, observation.ProviderScope, observation.RepositoryId,
                    observation.PullRequestId, observation.ThreadId, observation.FilePath, observation.Line, observation.Status, observation.IsDeleted,
                    observation.ConcernAnchor
                }));
        foreach (var comment in observation.Comments.Where(comment => !comment.IsSystemGenerated && !string.IsNullOrWhiteSpace(comment.Text)))
        {
            Add(comment.CommentId);
            Add(comment.AuthorIdentity);
            Add(comment.IsAiAuthored.ToString());
            Add(comment.IsSystemGenerated.ToString());
            Add(comment.Text);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(string value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            hash.AppendData(length);
            Span<byte> encoded = stackalloc byte[4096];
            for (var offset = 0; offset < value.Length; offset += 1024)
            {
                var count = Encoding.UTF8.GetBytes(value.AsSpan(offset, Math.Min(1024, value.Length - offset)), encoded);
                hash.AppendData(encoded[..count]);
            }
        }
    }
}
