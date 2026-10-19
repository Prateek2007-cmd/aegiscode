// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

public sealed partial class JobRepository
{
    public async Task<bool> RetainRunnerModelEvidenceAsync(
        RunnerCallContext call, IReadOnlyList<RunnerFileOutcome> files,
        IReadOnlyList<ModelReviewManifest> manifests, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var job = db.Database.IsNpgsql()
            ? await db.ReviewJobs.FromSqlInterpolated($"SELECT * FROM review_jobs WHERE id={call.JobId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.ReviewJobs.SingleOrDefaultAsync(row => row.Id == call.JobId, ct);
        var now = db.Database.IsNpgsql()
            ? await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
            : DateTimeOffset.UtcNow;
        if (job is null || job.Status != JobStatus.Processing || job.LeaseGeneration != call.Generation
            || job.LeaseOwner != call.CallerIdentity || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
        {
            return false;
        }

        // Missing dispatch proof is an analytics limitation and does not invalidate ordinary review output.
        if (job.ModelEvidenceManifest is not { AllowedModels.Count: > 0 } dispatched || dispatched.Attempt != job.RetryCount + 1
                                                                                     || dispatched.LeaseGeneration != call.Generation)
        {
            return true;
        }

        foreach (var group in files.Where(file => file.ModelEvidence is not null || file.ModelEvidenceFragment is not null)
                     .GroupBy(file => file.FilePath, StringComparer.Ordinal))
        {
            if (!dispatched.Files.Contains(group.Key, StringComparer.Ordinal))
            {
                continue;
            }

            var row = await db.ReviewFileResults.SingleOrDefaultAsync(file => file.JobId == job.Id && file.FilePath == group.Key, ct);
            var isNew = row is null;
            row ??= new(job.Id, group.Key);
            var hasProof = false;
            foreach (var outcome in group)
            {
                var proof = outcome.ModelEvidence;
                if (outcome.ModelEvidenceFragment is { } fragment)
                {
                    proof = this.RetainFragment(db, row, fragment, job);
                }

                proof = proof is not null && proof.FilePath == group.Key
                    ? this.ValidateDispatchedProof(
                        proof, job, dispatched,
                        (row.ModelEvidence is { } prior ? new[] { prior } : []).Concat(dispatched.PullRequestPasses))
                    : null;
                if (proof is not null)
                {
                    var retainedState = row.ModelEvidence;
                    row.RecordModelEvidence(proof with { PendingFragments = [], RetainedTransportDigests = [] });
                    db.Entry(row).Property(file => file.ModelEvidence).CurrentValue = row.ModelEvidence! with
                    {
                        PendingFragments = retainedState?.PendingFragments ?? [],
                        RetainedTransportDigests = retainedState?.RetainedTransportDigests ?? [],
                    };
                    hasProof = true;
                }
            }

            if (row.ModelEvidence is null)
            {
                continue;
            }

            if (hasProof)
            {
                await this.FenceModelEvidenceAsync(db, row, ct);
            }

            if (isNew)
            {
                db.ReviewFileResults.Add(row);
            }
        }

        var manifestPaths = manifests.Where(manifest => manifest is not null && manifest.IsBounded)
            .SelectMany(manifest => manifest.PullRequestPasses)
            .Where(proof => dispatched.Files.Contains(proof.FilePath, StringComparer.Ordinal) && proof.Passes.Any(pass =>
                pass.JobId != job.Id || pass.Attempt != dispatched.Attempt || pass.LeaseGeneration != dispatched.LeaseGeneration))
            .Select(proof => proof.FilePath).Distinct(StringComparer.Ordinal).Take(ModelReviewEvidence.MaximumPasses + 1).ToArray();
        var storedFiles = await ReadRunnerHistoryAsync(db, job.Id, manifestPaths, ct);
        var historical = storedFiles.Concat(dispatched.PullRequestPasses).ToArray();
        foreach (var manifest in manifests.Where(manifest => manifest is not null && manifest.IsBounded))
        {
            if (manifest.JobId == job.Id && manifest.Attempt == dispatched.Attempt && manifest.LeaseGeneration == call.Generation
                && (manifest.Files.Count == 0 || manifest.Files.SequenceEqual(dispatched.Files, StringComparer.Ordinal))
                && manifest.SourceConnectionId == dispatched.SourceConnectionId && manifest.ProviderScope == dispatched.ProviderScope)
            {
                var validated = manifest.PullRequestPasses.Select(proof => this.ValidateDispatchedProof(proof, job, dispatched, historical))
                    .OfType<ModelReviewEvidence>().ToArray();
                job.RecordModelEvidenceManifest(this.AcceptManifest(job, manifest with { PullRequestPasses = validated }, now, historical));
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    private static async Task<IReadOnlyList<ModelReviewEvidence>> ReadRunnerHistoryAsync(
        MeisterProPRDbContext db, Guid jobId, string[] paths, CancellationToken ct)
    {
        if (paths.Length == 0 || paths.Length > ModelReviewEvidence.MaximumPasses)
        {
            return [];
        }

        if (db.Database.IsNpgsql())
        {
            var bytes = await db.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(octet_length((model_evidence_json-'PendingFragments'-'RetainedTransportDigests')::text)),0)::bigint AS "Value"
                 FROM review_file_results WHERE job_id={jobId} AND file_path=ANY({paths})
                 """).SingleAsync(ct);
            if (bytes > ModelReviewEvidenceReader.MaximumBytes)
            {
                return [];
            }

            var rows = await db.Database.SqlQuery<RunnerHistoryPayload>(
                $"""
                 SELECT (model_evidence_json-'PendingFragments'-'RetainedTransportDigests')::text AS "EvidenceJson"
                 FROM review_file_results WHERE job_id={jobId} AND file_path=ANY({paths}) AND model_evidence_json IS NOT NULL
                 LIMIT {ModelReviewEvidence.MaximumPasses + 1}
                 """).ToListAsync(ct);
            return rows.Select(row => ModelEvidenceJson.ReadEvidence(row.EvidenceJson)).OfType<ModelReviewEvidence>().ToArray();
        }

        long total = 0;
        var result = new List<ModelReviewEvidence>();
        await foreach (var proof in db.ReviewFileResults.AsNoTracking()
                           .Where(file => file.JobId == jobId && paths.Contains(file.FilePath) && file.ModelEvidence != null)
                           .Select(file => file.ModelEvidence!).AsAsyncEnumerable().WithCancellation(ct))
        {
            var bounded = proof with { PendingFragments = [], RetainedTransportDigests = [] };
            total += JsonSerializer.SerializeToUtf8Bytes(bounded).Length;
            if (total > ModelReviewEvidenceReader.MaximumBytes)
            {
                return [];
            }

            result.Add(bounded);
        }

        return result;
    }

    private sealed record RunnerHistoryPayload(string EvidenceJson);

    private ModelReviewEvidence? RetainFragment(
        MeisterProPRDbContext db, ReviewFileResult row,
        ModelReviewProofFragment fragment, ReviewJob job)
    {
        if (!fragment.IsBounded)
        {
            return null;
        }

        var retained = row.ModelEvidence ?? new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = job.RetryCount + 1, LeaseGeneration = job.LeaseGeneration, ClientId = job.ClientId,
            RepositoryId = job.RepositoryId, PullRequestId = job.PullRequestId, FilePath = row.FilePath,
            ProviderScope = ProviderSourceIdentity.FromReviewJob(job, this._sourcePolicies[job.Provider]).Value,
            RevisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
            HeadSha = job.RevisionHeadSha, BaseSha = job.RevisionBaseSha,
            SourceConnectionId = job.ModelEvidenceManifest?.SourceConnectionId, UnavailableReason = "model-proof-incomplete",
        };
        if (retained.RetainedTransportDigests.Contains(fragment.Digest, StringComparer.Ordinal))
        {
            return null;
        }

        var parts = retained.PendingFragments.Where(part => part.Digest == fragment.Digest && part.TotalBytes == fragment.TotalBytes).ToList();
        if (parts.Any(part => part.Offset == fragment.Offset && part.Data == fragment.Data))
        {
            return null;
        }

        byte[] incoming;
        try
        {
            incoming = Convert.FromBase64String(fragment.Data);
        }
        catch (FormatException)
        {
            return null;
        }

        if (parts.Count >= ModelReviewProofFragment.MaximumFragments || parts.Any(part =>
                fragment.Offset < part.Offset + part.Data.Length / 4 * 3 -
                (part.Data.EndsWith("==", StringComparison.Ordinal) ? 2 : part.Data.EndsWith('=') ? 1 : 0)
                && part.Offset < fragment.Offset + incoming.Length))
        {
            return null;
        }

        parts.Add(fragment);
        parts.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        var pending = retained with { PendingFragments = parts };
        if (!pending.IsBounded)
        {
            return null;
        }

        db.Entry(row).Property(file => file.ModelEvidence).CurrentValue = pending;
        var expected = 0;
        foreach (var part in parts)
        {
            if (part.Offset != expected)
            {
                return null;
            }

            expected += Convert.FromBase64String(part.Data).Length;
        }

        if (expected != fragment.TotalBytes)
        {
            return null;
        }

        var bytes = new byte[expected];
        foreach (var part in parts)
        {
            Convert.FromBase64String(part.Data).CopyTo(bytes, part.Offset);
        }

        db.Entry(row).Property(file => file.ModelEvidence).CurrentValue = retained with { PendingFragments = [] };
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), fragment.Digest, StringComparison.Ordinal))
        {
            return null;
        }

        db.Entry(row).Property(file => file.ModelEvidence).CurrentValue = retained with
        {
            PendingFragments = [], RetainedTransportDigests = retained.RetainedTransportDigests
                .Append(fragment.Digest).TakeLast(ModelReviewEvidence.MaximumPasses).ToArray(),
        };
        return ModelEvidenceJson.ReadEvidence(Encoding.UTF8.GetString(bytes), new(System.Text.Json.JsonSerializerDefaults.Web));
    }

    public async Task RecordModelManifestAsync(Guid id, int attempt, ModelReviewManifest manifest, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var job = db.Database.IsNpgsql()
            ? await db.ReviewJobs.FromSqlInterpolated($"SELECT * FROM review_jobs WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.ReviewJobs.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (job is null || job.RetryCount + 1 != attempt || manifest.Attempt != attempt || manifest.JobId != id || !manifest.IsBounded
            || manifest.LeaseGeneration is { } generation && generation != job.LeaseGeneration)
        {
            return;
        }

        job.RecordModelEvidenceManifest(this.AcceptManifest(job, manifest, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }

    private ModelReviewManifest AcceptManifest(
        ReviewJob job, ModelReviewManifest manifest, DateTimeOffset now,
        IReadOnlyList<ModelReviewEvidence>? historical = null)
    {
        var previous = job.ModelEvidenceManifest;
        var sameDispatch = previous is not null && previous.Attempt == manifest.Attempt && previous.LeaseGeneration == manifest.LeaseGeneration;
        var source = sameDispatch ? previous!.SourceConnectionId : manifest.SourceConnectionId;
        var scope = ProviderSourceIdentity.FromReviewJob(job, this._sourcePolicies[job.Provider]).Value;
        // Historical certificates retain their own pass identity and receipt. They do not describe a new dispatch.
        var proofs = (previous?.PullRequestPasses ?? []).Where(proof => proof.IsBounded && proof.ClientId == job.ClientId
                                                                                        && proof.RepositoryId == job.RepositoryId &&
                                                                                        proof.PullRequestId == job.PullRequestId && proof.ProviderScope == scope
                                                                                        && proof.SourceConnectionId == source &&
                                                                                        proof.RevisionKey == ReviewRevisionKeys.GetStoredKey(
                                                                                            job.ReviewRevisionReference, job.IterationId)
                                                                                        && proof.HeadSha == job.RevisionHeadSha &&
                                                                                        proof.BaseSha == job.RevisionBaseSha)
            .ToDictionary(proof => proof.FilePath, StringComparer.Ordinal);
        foreach (var incoming in manifest.PullRequestPasses)
        {
            if (!this.MatchesJob(incoming, job) || incoming.SourceConnectionId != source)
            {
                continue;
            }

            proofs.TryGetValue(incoming.FilePath, out var prior);
            var accepted = AcceptPasses(incoming, prior, now, historical);
            var merged = prior is null ? accepted : prior.Merge(accepted);
            proofs[incoming.FilePath] = merged;
        }

        return manifest with
        {
            Attempt = job.RetryCount + 1, AcceptedAt = sameDispatch ? previous!.AcceptedAt ?? now : now,
            Files = sameDispatch ? previous!.Files : manifest.Files,
            InventoryComplete = sameDispatch ? previous!.InventoryComplete : manifest.InventoryComplete,
            AllowedModels = sameDispatch ? previous!.AllowedModels : manifest.AllowedModels,
            ProviderScope = scope, SourceConnectionId = source,
            PullRequestPasses = proofs.Values.Take(ModelReviewEvidence.MaximumPasses).ToArray(),
        };
    }

    private ModelReviewEvidence? ValidateDispatchedProof(
        ModelReviewEvidence proof, ReviewJob job, ModelReviewManifest dispatched, IEnumerable<ModelReviewEvidence> retained)
    {
        if (!this.MatchesJob(proof, job) || proof.LeaseGeneration != dispatched.LeaseGeneration
                                         || proof.SourceConnectionId != dispatched.SourceConnectionId ||
                                         !dispatched.Files.Contains(proof.FilePath, StringComparer.Ordinal))
        {
            return null;
        }

        var history = retained.Where(old => CompatibleProof(old, proof)).ToArray();
        var passes = new List<ModelReviewPassEvidence>(proof.Passes.Count);
        var complete = proof.InventoryComplete;
        foreach (var pass in proof.Passes)
        {
            if (pass.JobId == job.Id && pass.Attempt == dispatched.Attempt && pass.LeaseGeneration == dispatched.LeaseGeneration
                && pass.Candidates.All(candidate => dispatched.Files.Contains(candidate.FilePath, StringComparer.Ordinal))
                && dispatched.AllowedModels.Any(binding => binding.ModelId == pass.ModelId && binding.LogicalModelName == pass.LogicalModelName
                                                                                           && binding.ConnectionId == pass.ConnectionId &&
                                                                                           binding.ConfiguredModelId == pass.ConfiguredModelId
                                                                                           && binding.Ordinal == pass.Ordinal && binding.Shadow == pass.Shadow
                                                                                           && (binding.Scope == ReviewPassScope.PerFile &&
                                                                                               pass.Kind == "generation" && pass.Lens == binding.Lens
                                                                                               || binding.Scope == ReviewPassScope.PrWide &&
                                                                                               pass.Kind == "pr-wide-generation" &&
                                                                                               IsPrWideGenerationStage(pass.Lens))))
            {
                passes.Add(pass);
                continue;
            }

            var original = history.SelectMany(file => file.Passes.Select(old => (File: file, Pass: old)))
                .FirstOrDefault(item => item.Pass.Candidates.SequenceEqual(pass.Candidates)
                                        && pass with { AcceptedAt = item.Pass.AcceptedAt, Candidates = item.Pass.Candidates } == item.Pass);
            if (original.Pass is null)
            {
                return null;
            }

            // Only the stored server certificate can supply an earlier receipt or historical candidate set.
            passes.Add(original.Pass);
            complete &= original.File.InventoryComplete;
        }

        return proof with { Passes = passes, InventoryComplete = complete };
    }

    private static bool CompatibleProof(ModelReviewEvidence stored, ModelReviewEvidence incoming) => stored.IsBounded && stored.AcceptedAt is not null
        && stored.JobId == incoming.JobId && stored.ClientId == incoming.ClientId && stored.RepositoryId == incoming.RepositoryId
        && stored.PullRequestId == incoming.PullRequestId && stored.ProviderScope == incoming.ProviderScope
        && stored.SourceConnectionId == incoming.SourceConnectionId && stored.FilePath == incoming.FilePath && stored.RevisionKey == incoming.RevisionKey
        && stored.HeadSha == incoming.HeadSha && stored.BaseSha == incoming.BaseSha && stored.InputFingerprint == incoming.InputFingerprint
        && stored.InputComplete == incoming.InputComplete && stored.AssignedRanges.SequenceEqual(incoming.AssignedRanges);

    private static bool IsPrWideGenerationStage(string? stage) => stage == "pr-wide-synthesis"
                                                                  || stage is { Length: 38 } && stage.StartsWith(
                                                                                                 "pr-wide-investigation-", StringComparison.Ordinal)
                                                                                             && stage[22..].All(character =>
                                                                                                 character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool MatchesJob(ModelReviewEvidence incoming, ReviewJob job) => incoming.IsBounded && incoming.JobId == job.Id
                                                                                               && incoming.ClientId == job.ClientId &&
                                                                                               incoming.RepositoryId == job.RepositoryId &&
                                                                                               incoming.PullRequestId == job.PullRequestId
                                                                                               && incoming.Attempt == job.RetryCount + 1
                                                                                               && (incoming.LeaseGeneration is null ||
                                                                                                   incoming.LeaseGeneration == job.LeaseGeneration &&
                                                                                                   job.Status == JobStatus.Processing
                                                                                                   && job.LeaseOwner is not null &&
                                                                                                   job.LeaseExpiresAt > DateTimeOffset.UtcNow)
                                                                                               && incoming.ProviderScope ==
                                                                                               ProviderSourceIdentity.FromReviewJob(
                                                                                                   job, this._sourcePolicies[job.Provider]).Value
                                                                                               && incoming.RevisionKey == ReviewRevisionKeys.GetStoredKey(
                                                                                                   job.ReviewRevisionReference, job.IterationId)
                                                                                               && incoming.HeadSha == job.RevisionHeadSha &&
                                                                                               incoming.BaseSha == job.RevisionBaseSha;

    private static ModelReviewEvidence AcceptPasses(
        ModelReviewEvidence incoming, ModelReviewEvidence? retained, DateTimeOffset now,
        IReadOnlyList<ModelReviewEvidence>? historical = null) => incoming with
    {
        AcceptedAt = retained?.AcceptedAt ?? now,
        Passes = incoming.Passes.Select(pass =>
        {
            var prior = (retained?.Passes ?? []).Concat(
                (historical ?? []).Where(proof => CompatibleProof(proof, incoming))
                .SelectMany(proof => proof.Passes)).FirstOrDefault(old =>
                old.PassKey == pass.PassKey && old.ModelId == pass.ModelId && old.LogicalModelName == pass.LogicalModelName
                && old.JobId == pass.JobId && old.Attempt == pass.Attempt && old.LeaseGeneration == pass.LeaseGeneration
                && old.ConnectionId == pass.ConnectionId && old.ConfiguredModelId == pass.ConfiguredModelId
                && old.Ordinal == pass.Ordinal && old.Kind == pass.Kind && old.Lens == pass.Lens && old.Shadow == pass.Shadow);
            return prior is { AcceptedAt: not null } && (prior.IsCertified || prior.Disposition == ModelGenerationDisposition.NotApplicable)
                ? prior
                : pass with { AcceptedAt = pass.IsCertified || pass.Disposition == ModelGenerationDisposition.NotApplicable ? now : null };
        }).ToArray(),
    };

    private async Task FenceModelEvidenceAsync(MeisterProPRDbContext db, ReviewFileResult result, CancellationToken ct)
    {
        if (result.ModelEvidence is not { } incoming)
        {
            return;
        }

        var retained = await db.ReviewFileResults.AsNoTracking().Where(row => row.Id == result.Id)
            .Select(row => row.ModelEvidence).SingleOrDefaultAsync(ct);
        var job = await db.ReviewJobs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == result.JobId, ct);
        if (job is not null && await this.AcceptReusedProofAsync(db, job, result, incoming, ct) is { } reused)
        {
            db.Entry(result).Property(row => row.ModelEvidence).CurrentValue = reused;
            return;
        }

        var historical = job?.ModelEvidenceManifest?.PullRequestPasses ?? [];
        var valid = job is not null && incoming.FilePath == result.FilePath && this.MatchesJob(incoming, job);
        if (valid && job!.ModelEvidenceManifest is { AllowedModels.Count: > 0 } dispatched)
        {
            var normalized = dispatched.Attempt == incoming.Attempt
                             && dispatched.LeaseGeneration == (job.LeaseGeneration > 0 ? job.LeaseGeneration : null)
                ? this.ValidateDispatchedProof(incoming, job, dispatched, (retained is { } prior ? new[] { prior } : []).Concat(historical))
                : null;
            valid = normalized is not null;
            incoming = normalized ?? incoming;
        }

        ModelReviewEvidence accepted;
        if (!valid)
        {
            accepted = retained ?? new ModelReviewEvidence
                { JobId = result.JobId, FilePath = result.FilePath, UnavailableReason = "model-proof-identity-unavailable" };
        }
        else
        {
            accepted = AcceptPasses(incoming, retained, DateTimeOffset.UtcNow, historical);
        }

        db.Entry(result).Property(row => row.ModelEvidence).CurrentValue = accepted;
    }

    private async Task<ModelReviewEvidence?> AcceptReusedProofAsync(
        MeisterProPRDbContext db, ReviewJob job,
        ReviewFileResult result, ModelReviewEvidence incoming, CancellationToken ct)
    {
        if ((!result.IsCarriedForward && result.ResumedFromFileResultId is null) || incoming.OriginJobId is not { } originJobId
                                                                                 || incoming.OriginFileResultId is not { } originFileId ||
                                                                                 !result.IsCarriedForward && !incoming.UnchangedInputProven)
        {
            return null;
        }

        var original = await db.ReviewFileResults.AsNoTracking().SingleOrDefaultAsync(row => row.Id == originFileId && row.JobId == originJobId, ct);
        var origin = original?.ModelEvidence;
        if (origin is null || !origin.IsBounded || original!.FilePath != result.FilePath || incoming.InputFingerprint != origin.InputFingerprint
            || origin.ClientId != job.ClientId || origin.RepositoryId != job.RepositoryId || origin.PullRequestId != job.PullRequestId
            || origin.ProviderScope != ProviderSourceIdentity.FromReviewJob(job, this._sourcePolicies[job.Provider]).Value
            || incoming.Passes.Count != origin.Passes.Count
            || !incoming.Passes.Select(pass => pass.PassKey).ToHashSet(StringComparer.Ordinal).SetEquals(origin.Passes.Select(pass => pass.PassKey)))
        {
            return null;
        }

        if (!result.IsCarriedForward && (job.RevisionHeadSha != origin.HeadSha || job.RevisionBaseSha != origin.BaseSha))
        {
            return null;
        }

        return origin with
        {
            JobId = job.Id, Attempt = job.RetryCount + 1, LeaseGeneration = job.LeaseGeneration > 0 ? job.LeaseGeneration : null,
            OriginJobId = originJobId, OriginFileResultId = originFileId, UnchangedInputProven = !result.IsCarriedForward,
            InputComplete = !result.IsCarriedForward && origin.InputComplete,
            UnavailableReason = result.IsCarriedForward ? "model-reuse-context-unavailable" : origin.UnavailableReason
        };
    }
}
