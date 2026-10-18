// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightPullRequestPersistence;
using static MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightProviderScopeLookup;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Persists ordered human-thread eligibility and classifier evidence under one aggregate lock.</summary>
public sealed class CodeInsightMissStore(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightMissStore
{
    private const int MaximumFailedClassifierAttempts = 3;
    private const int MaximumWriteAttempts = 3;
    private const string FindingMessagePurpose = "code-insight-finding-message";
    private const string MissDiscussionPurpose = "code-insight-miss-discussion";

    public Task<CodeInsightEvidenceAcknowledgment> TryPreserveHistoricalMissAsync(
        CodeInsightPullRequestKey key, CodeInsightHistoricalMissObservation observation, CancellationToken ct = default)
    {
        return this.WithMissDbAsync(
            key, async db =>
            {
                var id = await FindPullRequestIdAsync(db, key, ct);
                var existing = await db.CodeInsightMisses.FirstOrDefaultAsync(
                    m => m.CodeInsightPullRequestId == id &&
                         m.ProviderScope == observation.ProviderScope && m.ProviderThreadId == observation.ThreadId, ct);
                if (existing is null)
                {
                    return new CodeInsightEvidenceAcknowledgment(false);
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(existing).ReloadAsync(ct);
                }

                var stamp = NormalizeMissStamp(observation.ObservedAt)!.Value;
                if (existing.SourceObservedAt > stamp)
                {
                    return !existing.JudgementFailed && !existing.DimensionJudgementFailed && existing.ClassifierConfidence is not null
                           && !string.IsNullOrWhiteSpace(existing.EncryptedDiscussion)
                        ? new CodeInsightEvidenceAcknowledgment(true)
                        : new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ClassificationUnavailable);
                }

                if (!observation.ResolutionKnown || existing.JudgementFailed || existing.ClassifierConfidence is null ||
                    existing.JudgedThreadResolved != observation.ThreadResolved || existing.FilePath != observation.FilePath ||
                    existing.LineNumber != observation.LineNumber || string.IsNullOrWhiteSpace(observation.Discussion) ||
                    secretProtectionCodec.Unprotect(existing.EncryptedDiscussion, MissDiscussionPurpose) != observation.Discussion)
                {
                    return new CodeInsightEvidenceAcknowledgment(false);
                }

                var eligibility = await FindThreadEligibilityAsync(db, existing.CodeInsightPullRequestId, observation.ProviderScope, observation.ThreadId, ct);
                if (eligibility?.SourceObservedAt > stamp)
                {
                    return new CodeInsightEvidenceAcknowledgment(false);
                }

                var miss = new CodeInsightMissRecord(
                    observation.ThreadId, observation.FilePath, observation.LineNumber,
                    observation.Discussion, existing.IsSubstantive, existing.WasActedOn, existing.IsInScope,
                    existing.ClassifierConfidence, existing.ClassifierVersion, observation.ThreadResolved,
                    ProviderScope: observation.ProviderScope);
                var excluded = eligibility is { ExcludedFromHumanMisses: true } || await this.HasCurrentOwnFindingAsync(db, existing, miss, ct);
                var changed = excluded != existing.ExcludedAsOwnFinding;
                existing.RecordOwnFindingExclusion(excluded, DateTimeOffset.UtcNow);
                existing.SourceFingerprint = observation.SourceFingerprint;
                existing.SourceObservedAt = stamp;
                await db.SaveChangesAsync(ct);
                return existing.DimensionJudgementFailed
                    ? new CodeInsightEvidenceAcknowledgment(false, changed, CodeInsightObservationUnavailableReason.ClassificationUnavailable)
                    : new CodeInsightEvidenceAcknowledgment(true, changed);
            }, ct);
    }

    public Task<CodeInsightThreadEligibilityObservation> ObserveThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, bool excludedFromHumanMisses,
        DateTimeOffset observedAt, CancellationToken ct = default, CodeInsightObservationMode mode = CodeInsightObservationMode.Current)
    {
        return this.WithMissDbAsync(
            key, async db =>
            {
                var pr = mode == CodeInsightObservationMode.HistoricalBackfill
                    ? await db.CodeInsightPullRequests.FirstOrDefaultAsync(
                        p => p.ClientId == key.ClientId && p.RepositoryId == key.RepositoryId && p.PullRequestId == key.PullRequestId, ct)
                    : await GetOrCreatePullRequestAsync(db, key, null, observedAt, ct);
                if (pr is null)
                {
                    return new CodeInsightThreadEligibilityObservation(observedAt, excludedFromHumanMisses, false);
                }

                var fingerprint = ThreadEligibilityFingerprint(pr.Id, providerScope, threadId);
                var existing = await db.CodeInsightThreadEligibility.SingleOrDefaultAsync(row => row.IdentityFingerprint == fingerprint, ct);
                var stamp = NormalizeMissStamp(observedAt)!.Value;
                if (existing is null)
                {
                    existing = new CodeInsightThreadEligibility
                    {
                        Id = Guid.CreateVersion7(),
                        CodeInsightPullRequestId = pr.Id,
                        ProviderScope = providerScope,
                        ProviderThreadId = threadId,
                        IdentityFingerprint = fingerprint,
                        SourceObservedAt = stamp,
                        ExcludedFromHumanMisses = excludedFromHumanMisses,
                    };
                    db.CodeInsightThreadEligibility.Add(existing);
                }
                else
                {
                    if (db.Database.IsRelational())
                    {
                        await db.Entry(existing).ReloadAsync(ct);
                    }

                    if (stamp > existing.SourceObservedAt)
                    {
                        existing.SourceObservedAt = stamp;
                        existing.ExcludedFromHumanMisses = excludedFromHumanMisses;
                    }
                    else if (stamp == existing.SourceObservedAt && excludedFromHumanMisses)
                    {
                        existing.ExcludedFromHumanMisses = true;
                    }
                }

                await db.SaveChangesAsync(ct);
                return new CodeInsightThreadEligibilityObservation(existing.SourceObservedAt, existing.ExcludedFromHumanMisses);
            }, ct);
    }

    public Task<CodeInsightThreadEligibilityObservation?> GetThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, CancellationToken ct = default)
    {
        return this.WithDbAsync<CodeInsightThreadEligibilityObservation?>(
            async db =>
            {
                var id = await FindPullRequestIdAsync(db, key, ct);
                if (id is null)
                {
                    return null;
                }

                var fingerprint = ThreadEligibilityFingerprint(id.Value, providerScope, threadId);
                return await db.CodeInsightThreadEligibility.AsNoTracking().Where(row => row.IdentityFingerprint == fingerprint)
                    .Select(row => new CodeInsightThreadEligibilityObservation(row.SourceObservedAt, row.ExcludedFromHumanMisses)).SingleOrDefaultAsync(ct);
            }, ct);
    }

    public Task<bool> RecordMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(miss);
        miss = miss with { SourceObservedAt = NormalizeMissStamp(miss.SourceObservedAt) };

        return this.WithMissDbAsync(
            key,
            async db =>
            {
                // Create an aggregate for a human miss even when the review produced no findings.
                var pullRequest = miss.ObservationMode == CodeInsightObservationMode.HistoricalBackfill
                    ? await db.CodeInsightPullRequests.FirstOrDefaultAsync(
                        p => p.ClientId == key.ClientId && p.RepositoryId == key.RepositoryId && p.PullRequestId == key.PullRequestId, ct)
                    : await GetOrCreatePullRequestAsync(db, key, null, DateTimeOffset.UtcNow, ct);
                if (pullRequest is null)
                {
                    return false;
                }

                var providerScope = await ResolveMissProviderScopeAsync(db, key.ClientId, miss.ConnectionId, ct, miss.ProviderScope);
                if (SupersedesJudgment(await FindThreadEligibilityAsync(db, pullRequest.Id, providerScope, miss.ProviderThreadId, ct), miss.SourceObservedAt))
                {
                    return false;
                }

                var existing = await db.CodeInsightMisses
                    .FirstOrDefaultAsync(
                        candidate => candidate.CodeInsightPullRequestId == pullRequest.Id
                                     && candidate.ProviderThreadId == miss.ProviderThreadId && candidate.ProviderScope == providerScope,
                        ct);
                if (existing is not null)
                {
                    if (miss.SourceObservedAt is null)
                    {
                        return false;
                    }

                    if (db.Database.IsRelational())
                    {
                        await db.Entry(existing).ReloadAsync(ct);
                    }

                    return await this.ApplyMissAsync(db, existing, miss, ct);
                }

                var harvestedAt = DateTimeOffset.UtcNow;

                var harvested = new CodeInsightMiss
                {
                    Id = Guid.CreateVersion7(),
                    CodeInsightPullRequestId = pullRequest.Id,
                    ProviderThreadId = miss.ProviderThreadId,
                    TypeMembership = miss.TypeMembership,
                    ProviderScope = providerScope,
                    Qualifier = miss.Qualifier,
                    DimensionClassifierVersion = miss.DimensionClassifierVersion,
                    DimensionConfidence = miss.DimensionConfidence,
                    DimensionJudgementFailed = miss.DimensionJudgementFailed,
                    FailedDimensionAttempts = miss.DimensionJudgementFailed && miss.DimensionModelWasAsked ? 1 : 0,
                    JudgementFailed = miss.JudgementFailed,
                    FailedJudgementAttempts = miss.JudgementFailed && miss.JudgementModelWasAsked ? 1 : 0,
                    SourceFingerprint = miss.SourceFingerprint,
                    SourceObservedAt = miss.SourceObservedAt,
                    FilePath = miss.FilePath,
                    LineNumber = miss.LineNumber,
                    HarvestedAt = harvestedAt,
                };

                harvested.RecordJudgement(
                    miss.IsSubstantive,
                    miss.WasActedOn,
                    miss.IsInScope,
                    miss.Confidence,
                    miss.ClassifierVersion,
                    miss.JudgedThreadResolved,
                    secretProtectionCodec.Protect(miss.Discussion, MissDiscussionPurpose),
                    harvestedAt);

                harvested.RecordOwnFindingExclusion(await this.HasCurrentOwnFindingAsync(db, harvested, miss, ct), harvestedAt);
                db.CodeInsightMisses.Add(harvested);

                await db.SaveChangesAsync(ct);
                return true;
            },
            ct);
    }

    public Task<CodeInsightMissAcknowledgment> ObserveUnchangedMissAsync(
        CodeInsightPullRequestKey key, string threadId, string fingerprint,
        DateTimeOffset observedAt, CancellationToken ct = default, Guid? connectionId = null,
        string? dimensionClassifierVersion = null, bool excludedAsOwnFinding = false, string? providerScope = null)
    {
        return this.WithMissDbAsync(
            key, async db =>
            {
                observedAt = NormalizeMissStamp(observedAt)!.Value;
                var id = await FindPullRequestIdAsync(db, key, ct);
                var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                var existing = await db.CodeInsightMisses.FirstOrDefaultAsync(
                    row => row.CodeInsightPullRequestId == id
                           && row.ProviderThreadId == threadId && row.ProviderScope == scope, ct);
                if (existing is null)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(existing).ReloadAsync(ct);
                }

                if (existing.SourceObservedAt > observedAt)
                {
                    return new CodeInsightMissAcknowledgment(true, false);
                }

                var eligibility = await FindThreadEligibilityAsync(db, existing.CodeInsightPullRequestId, scope, threadId, ct);
                if (eligibility is { ExcludedFromHumanMisses: true } && eligibility.SourceObservedAt >= observedAt)
                {
                    excludedAsOwnFinding = true;
                    observedAt = eligibility.SourceObservedAt;
                }
                else if (!excludedAsOwnFinding && eligibility?.SourceObservedAt > observedAt)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                var completed = HasCompletedObservation(existing, fingerprint, dimensionClassifierVersion);
                if (!excludedAsOwnFinding && !completed)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                var changed = existing.ExcludedAsOwnFinding != excludedAsOwnFinding;
                existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
                existing.SourceObservedAt = observedAt;
                await db.SaveChangesAsync(ct);
                return new CodeInsightMissAcknowledgment(true, changed);
            }, ct);
    }

    public Task<IReadOnlyDictionary<string, CodeInsightMissObservation>> GetObservationsAsync(
        CodeInsightPullRequestKey key, string providerScope, CancellationToken ct = default)
    {
        return this.WithDbAsync<IReadOnlyDictionary<string, CodeInsightMissObservation>>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                return await db.CodeInsightMisses.AsNoTracking().Where(row => row.CodeInsightPullRequestId == aggregateId && row.ProviderScope == providerScope)
                    .ToDictionaryAsync(
                        row => row.ProviderThreadId, row => new CodeInsightMissObservation(
                            row.SourceFingerprint, row.JudgementFailed, row.FailedJudgementAttempts,
                            row.IsSubstantive, row.WasActedOn, row.IsInScope, row.ClassifierConfidence, row.JudgedThreadResolved, row.TypeMembership,
                            row.Qualifier, row.DimensionClassifierVersion, row.DimensionConfidence, row.DimensionJudgementFailed, row.FailedDimensionAttempts,
                            row.SourceObservedAt, row.ExcludedAsOwnFinding, row.ClassifierVersion), ct);
            }, ct);
    }

    public Task<IReadOnlyDictionary<string, CodeInsightThreadEligibilityObservation>> GetEligibilitiesAsync(
        CodeInsightPullRequestKey key, string providerScope, CancellationToken ct = default)
    {
        return this.WithDbAsync<IReadOnlyDictionary<string, CodeInsightThreadEligibilityObservation>>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                return await db.CodeInsightThreadEligibility.AsNoTracking()
                    .Where(row => row.CodeInsightPullRequestId == aggregateId && row.ProviderScope == providerScope)
                    .ToDictionaryAsync(
                        row => row.ProviderThreadId, row => new CodeInsightThreadEligibilityObservation(row.SourceObservedAt, row.ExcludedFromHumanMisses), ct);
            }, ct);
    }

    public Task<CodeInsightMissObservation?> GetObservationAsync(
        CodeInsightPullRequestKey key, string threadId, CancellationToken ct = default, Guid? connectionId = null, string? providerScope = null)
    {
        return this.WithDbAsync<CodeInsightMissObservation?>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                var candidates = db.CodeInsightMisses.Where(row => row.CodeInsightPullRequestId == aggregateId && row.ProviderThreadId == threadId);
                if (connectionId is not null || providerScope is not null)
                {
                    var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                    candidates = candidates.Where(row => row.ProviderScope == scope);
                }
                else if (await candidates.Select(row => row.ProviderScope).Distinct().Take(2).CountAsync(ct) > 1)
                {
                    return null;
                }

                return await candidates
                    .Select(row => new CodeInsightMissObservation(
                        row.SourceFingerprint, row.JudgementFailed, row.FailedJudgementAttempts,
                        row.IsSubstantive, row.WasActedOn, row.IsInScope, row.ClassifierConfidence, row.JudgedThreadResolved,
                        row.TypeMembership, row.Qualifier, row.DimensionClassifierVersion, row.DimensionConfidence,
                        row.DimensionJudgementFailed, row.FailedDimensionAttempts, row.SourceObservedAt, row.ExcludedAsOwnFinding, row.ClassifierVersion))
                    .FirstOrDefaultAsync(ct);
            }, ct);
    }

    public Task<bool?> GetJudgedThreadResolvedAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default,
        Guid? connectionId = null, string? providerScope = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerThreadId);

        return this.WithDbAsync(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return null;
                }

                // A missing row and a judgment of an open thread require different retry decisions.
                var candidates = db.CodeInsightMisses.Where(miss =>
                    miss.CodeInsightPullRequestId == aggregateId.Value && miss.ProviderThreadId == providerThreadId);
                if (connectionId is not null || providerScope is not null)
                {
                    var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                    candidates = candidates.Where(row => row.ProviderScope == scope);
                }
                else if (await candidates.Select(row => row.ProviderScope).Distinct().Take(2).CountAsync(ct) > 1)
                {
                    return null;
                }

                var judged = await candidates
                    .Select(miss => (bool?)(miss.JudgementFailed ? miss.FailedJudgementAttempts >= MaximumFailedClassifierAttempts : miss.JudgedThreadResolved))
                    .FirstOrDefaultAsync(ct);

                return judged;
            },
            ct);
    }

    public Task<bool> RejudgeMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(miss);
        miss = miss with { SourceObservedAt = NormalizeMissStamp(miss.SourceObservedAt) };

        return this.WithMissDbAsync(
            key,
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return false;
                }

                var providerScope = await ResolveMissProviderScopeAsync(db, key.ClientId, miss.ConnectionId, ct, miss.ProviderScope);
                var existing = await db.CodeInsightMisses
                    .FirstOrDefaultAsync(
                        candidate => candidate.CodeInsightPullRequestId == aggregateId.Value
                                     && candidate.ProviderThreadId == miss.ProviderThreadId && candidate.ProviderScope == providerScope,
                        ct);
                if (existing is null)
                {
                    return false;
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(existing).ReloadAsync(ct);
                }

                return await this.ApplyMissAsync(db, existing, miss, ct);
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightMissView>> GetMissesForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return this.WithDbAsync<IReadOnlyList<CodeInsightMissView>>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return [];
                }

                var records = await db.CodeInsightMisses
                    .Where(miss => miss.CodeInsightPullRequestId == aggregateId.Value)
                    .OrderBy(miss => miss.HarvestedAt)
                    .ToListAsync(ct);

                return records
                    .Select(miss => new CodeInsightMissView(
                        miss.Id,
                        miss.ProviderThreadId,
                        miss.FilePath,
                        miss.LineNumber,
                        secretProtectionCodec.Unprotect(miss.EncryptedDiscussion, MissDiscussionPurpose),
                        miss.IsSubstantive,
                        miss.WasActedOn,
                        miss.IsInScope,
                        !miss.JudgementFailed && miss.CountsAsMiss,
                        miss.ClassifierConfidence,
                        miss.ClassifierVersion,
                        miss.HarvestedAt,
                        miss.JudgedThreadResolved,
                        miss.LastJudgedAt, miss.JudgementFailed, miss.ExcludedAsOwnFinding))
                    .ToList();
            },
            ct);
    }

    private static string ThreadEligibilityFingerprint(Guid aggregateId, string providerScope, string threadId)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { aggregateId.ToString("D"), providerScope, threadId })));
    }

    private static async Task<CodeInsightThreadEligibility?> FindThreadEligibilityAsync(
        MeisterProPRDbContext db, Guid aggregateId, string providerScope, string threadId, CancellationToken ct)
    {
        var fingerprint = ThreadEligibilityFingerprint(aggregateId, providerScope, threadId);
        return await db.CodeInsightThreadEligibility.AsNoTracking().SingleOrDefaultAsync(row => row.IdentityFingerprint == fingerprint, ct);
    }

    private static bool SupersedesJudgment(CodeInsightThreadEligibility? eligibility, DateTimeOffset? observedAt)
    {
        return eligibility is not null && (observedAt is null || eligibility.SourceObservedAt > observedAt
                                                              || eligibility.ExcludedFromHumanMisses && eligibility.SourceObservedAt == observedAt);
    }

    private static DateTimeOffset? NormalizeMissStamp(DateTimeOffset? stamp)
    {
        return stamp is null ? null : new DateTimeOffset(stamp.Value.UtcTicks / 10 * 10, TimeSpan.Zero);
    }

    private async Task<bool> ApplyMissAsync(MeisterProPRDbContext db, CodeInsightMiss existing, CodeInsightMissRecord miss, CancellationToken ct)
    {
        if (existing.SourceObservedAt is not null && (miss.SourceObservedAt is null || existing.SourceObservedAt > miss.SourceObservedAt))
        {
            return false;
        }

        if (SupersedesJudgment(
                await FindThreadEligibilityAsync(db, existing.CodeInsightPullRequestId, existing.ProviderScope, miss.ProviderThreadId, ct),
                miss.SourceObservedAt))
        {
            return false;
        }

        var excludedAsOwnFinding = await this.HasCurrentOwnFindingAsync(db, existing, miss, ct);
        var sameSource = !string.IsNullOrEmpty(miss.SourceFingerprint) && existing.SourceFingerprint == miss.SourceFingerprint;
        if (sameSource && !existing.JudgementFailed && miss.JudgementFailed)
        {
            var exclusionChanged = existing.ExcludedAsOwnFinding != excludedAsOwnFinding;
            existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
            existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
            await db.SaveChangesAsync(ct);
            return exclusionChanged || miss.SourceObservedAt is null;
        }

        var sameDimensionsSource = sameSource && existing.DimensionClassifierVersion == miss.DimensionClassifierVersion;
        var keepDimensions = sameDimensionsSource && !existing.DimensionJudgementFailed && miss.DimensionJudgementFailed;
        var sameHumanJudgment = sameSource && IsHumanJudgmentUnchanged(existing, miss);
        var sameDimensions = sameDimensionsSource && AreDimensionsUnchanged(existing, miss);
        if (existing.ExcludedAsOwnFinding == excludedAsOwnFinding && sameHumanJudgment &&
            ((sameDimensions && (!existing.DimensionJudgementFailed || existing.FailedDimensionAttempts >= MaximumFailedClassifierAttempts ||
                                 !miss.DimensionModelWasAsked)) || keepDimensions))
        {
            existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
            await db.SaveChangesAsync(ct);
            return miss.SourceObservedAt is null;
        }

        if (!keepDimensions)
        {
            ApplyDimensions(existing, miss, sameDimensionsSource);
        }

        this.ApplyHumanJudgment(existing, miss, sameSource, excludedAsOwnFinding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HasCompletedObservation(CodeInsightMiss existing, string fingerprint, string? dimensionClassifierVersion)
    {
        if (existing.SourceFingerprint != fingerprint)
        {
            return false;
        }

        if (existing.JudgementFailed)
        {
            return existing.FailedJudgementAttempts >= MaximumFailedClassifierAttempts;
        }

        return !existing.IsSubstantive || dimensionClassifierVersion is null
                                       || (existing.DimensionClassifierVersion == dimensionClassifierVersion
                                           && (!existing.DimensionJudgementFailed || existing.FailedDimensionAttempts >= MaximumFailedClassifierAttempts));
    }

    private static bool IsHumanJudgmentUnchanged(CodeInsightMiss existing, CodeInsightMissRecord miss)
    {
        return !existing.JudgementFailed && !miss.JudgementFailed
                                         && existing.IsSubstantive == miss.IsSubstantive
                                         && existing.WasActedOn == miss.WasActedOn
                                         && existing.IsInScope == miss.IsInScope
                                         && existing.ClassifierConfidence == miss.Confidence
                                         && existing.ClassifierVersion == miss.ClassifierVersion
                                         && existing.JudgedThreadResolved == miss.JudgedThreadResolved;
    }

    private static bool AreDimensionsUnchanged(CodeInsightMiss existing, CodeInsightMissRecord miss)
    {
        return existing.TypeMembership == miss.TypeMembership
               && existing.Qualifier == miss.Qualifier
               && existing.DimensionConfidence == miss.DimensionConfidence
               && existing.DimensionJudgementFailed == miss.DimensionJudgementFailed;
    }

    private static void ApplyDimensions(CodeInsightMiss existing, CodeInsightMissRecord miss, bool sameSource)
    {
        existing.FailedDimensionAttempts = FailedAttempts(
            miss.DimensionJudgementFailed, sameSource, existing.FailedDimensionAttempts, miss.DimensionModelWasAsked);
        existing.DimensionJudgementFailed = miss.DimensionJudgementFailed;
        existing.TypeMembership = miss.TypeMembership;
        existing.Qualifier = miss.Qualifier;
        existing.DimensionClassifierVersion = miss.DimensionClassifierVersion;
        existing.DimensionConfidence = miss.DimensionConfidence;
    }

    private void ApplyHumanJudgment(CodeInsightMiss existing, CodeInsightMissRecord miss, bool sameSource, bool excludedAsOwnFinding)
    {
        var preserveJudgment = sameSource && !miss.JudgementModelWasAsked && IsHumanJudgmentUnchanged(existing, miss)
                               && existing.FilePath == miss.FilePath && existing.LineNumber == miss.LineNumber
                               && secretProtectionCodec.Unprotect(existing.EncryptedDiscussion, MissDiscussionPurpose) == miss.Discussion;
        existing.JudgementFailed = miss.JudgementFailed;
        existing.FailedJudgementAttempts = FailedAttempts(miss.JudgementFailed, sameSource, existing.FailedJudgementAttempts, miss.JudgementModelWasAsked);
        existing.SourceFingerprint = miss.SourceFingerprint;
        existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
        existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
        if (preserveJudgment)
        {
            return;
        }

        existing.RecordJudgement(
            miss.IsSubstantive, miss.WasActedOn, miss.IsInScope, miss.Confidence, miss.ClassifierVersion,
            miss.JudgedThreadResolved, secretProtectionCodec.Protect(miss.Discussion, MissDiscussionPurpose), DateTimeOffset.UtcNow);
        existing.FilePath = miss.FilePath;
        existing.LineNumber = miss.LineNumber;
    }

    private static int FailedAttempts(bool failed, bool sameSource, int retainedAttempts, bool modelWasAsked)
    {
        return failed ? Math.Min(MaximumFailedClassifierAttempts, (sameSource ? retainedAttempts : 0) + (modelWasAsked ? 1 : 0)) : 0;
    }

    private async Task<bool> HasCurrentOwnFindingAsync(MeisterProPRDbContext db, CodeInsightMiss existing, CodeInsightMissRecord miss, CancellationToken ct)
    {
        var findings = await db.CodeInsightFindings.Where(finding =>
                finding.CodeInsightPullRequestId == existing.CodeInsightPullRequestId && finding.ProviderScope == existing.ProviderScope)
            .Select(finding => new
            {
                finding.ProviderThreadId,
                finding.FilePath,
                finding.LineNumber,
                finding.EncryptedMessage,
            }).ToListAsync(ct);
        return findings.Any(finding => string.Equals(finding.ProviderThreadId, miss.ProviderThreadId, StringComparison.Ordinal))
               || HumanFindingOverlap.DuplicatesAnyFinding(
                   miss.FilePath, miss.LineNumber, miss.Discussion,
                   findings.Select(finding => new FindingOverlapCandidate(
                       finding.FilePath, finding.LineNumber, secretProtectionCodec.Unprotect(finding.EncryptedMessage, FindingMessagePurpose))).ToList());
    }

    private async Task<T> WithMissDbAsync<T>(CodeInsightPullRequestKey key, Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await this.WithDbAsync(
                    async db =>
                    {
                        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                        await CodeInsightAggregateWriteLock.AcquireAsync(db, key, ct);

                        var result = await operation(db);
                        if (transaction is not null)
                        {
                            await transaction.CommitAsync(ct);
                        }

                        return result;
                    }, ct);
            }
            catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                      exception.InnerException is PostgresException { SqlState: "23505" })
            {
                if (contextFactory is null)
                {
                    dbContext.ChangeTracker.Clear();
                }
            }
        }
    }

    private async Task WithDbAsync(Func<MeisterProPRDbContext, Task> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        await operation(lease.Context);
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        return await operation(lease.Context);
    }
}
