// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.CodeInsights.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Reconciles existing typed checkpoints and retries retained classification without rerunning reviews.</summary>
public sealed class ModelEvidenceMaintenance(
    MeisterProPRDbContext db,
    ICodeInsightsCollectionGate gate,
    ICodeInsightReviewExposureCollector exposures,
    ICodeInsightModelConcernObserver? concerns = null,
    IDiscussionSynchronizationStore? discussionStore = null,
    ICodeInsightDiscussionBatchFactory? batches = null,
    TimeProvider? timeProvider = null,
    IOptionsMonitor<CodeInsightsOptions>? options = null) : IModelEvidenceMaintenance
{
    internal const string ExposureSource = "model-opportunity-v1";
    private TimeSpan RetryCooldown => (options?.CurrentValue ?? new CodeInsightsOptions()).ModelEvidenceRetryCooldown;

    private readonly IReadOnlyDictionary<MeisterDev.ProPR.Domain.Enums.ScmProvider, IReviewSourcePolicy> _policies =
        ScmLocalPolicyFactory.CreateSourcePolicies().ToDictionary(policy => policy.Provider);

    public async Task<int> SweepAsync(int maximumJobs, CancellationToken ct = default)
    {
        var cap = Math.Clamp(maximumJobs, 1, 100);
        var jobIds = await this.PendingJobIdsAsync(cap, ct);
        var metadata = await db.ReviewJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id)).Select(job => new
            { job.Id, job.ClientId, job.RepositoryId, job.PullRequestId, job.Provider, job.OrganizationUrl, job.HostBaseUrl }).ToListAsync(ct);
        var indexed = 0;
        foreach (var job in metadata)
        {
            if (!await gate.IsCollectionEnabledAsync(job.ClientId, ct))
            {
                continue;
            }

            var policy = this._policies[job.Provider];
            var scope = ProviderSourceIdentity.FromReviewSource(job.Provider, policy.SelectCapturedSource(job.OrganizationUrl, job.HostBaseUrl), policy).Value;
            var snapshot = await new ModelReviewEvidenceReader(db).ReadAsync(
                [new(job.ClientId, job.RepositoryId, job.PullRequestId, scope)],
                (timeProvider ?? TimeProvider.System).GetUtcNow(), ct);
            if (!snapshot.Complete)
            {
                continue;
            }

            var changed = false;
            foreach (var execution in snapshot.Executions.Where(execution => execution.JobId == job.Id))
            {
                foreach (var file in execution.Files.Concat(execution.Manifest?.PullRequestPasses ?? []))
                {
                    foreach (var pass in file.Passes.Where(pass => pass.IsCertified && pass.AcceptedAt is not null))
                    {
                        if (!IndexablePath(file.FilePath) || await db.CodeInsightReviewExposures.AsNoTracking().AnyAsync(
                                row => row.JobId == job.Id
                                       && row.FilePath == file.FilePath && row.ModelId == pass.ModelId && row.LogicalModelName == pass.LogicalModelName &&
                                       row.Source == ExposureSource, ct))
                        {
                            continue;
                        }

                        await exposures.RecordAsync(
                            new(job.ClientId, job.RepositoryId, job.PullRequestId), job.Id, file.FilePath, execution.RevisionKey,
                            pass.ModelId, pass.LogicalModelName, scope, ExposureSource, pass.AcceptedAt!.Value, ct);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                indexed++;
            }
        }

        if (concerns is not null && discussionStore is not null)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var retryBefore = now - this.RetryCooldown;
            var due = await db.CodeInsightHumanConcerns.AsNoTracking().Where(row => !row.IsDeleted && row.RetainedSourceComplete
                                                                                                   && (row.JudgementFailed && row.FailedAttempts < 3
                                                                                                       && (row.UnavailableReason == null ||
                                                                                                           row.UnavailableReason ==
                                                                                                           "model-concern-judgement-failed") ||
                                                                                                       !row.JudgementFailed && row.DimensionJudgementFailed &&
                                                                                                       row.FailedDimensionAttempts < 3
                                                                                                       || !row.JudgementFailed &&
                                                                                                       db.CodeInsightModelConcernEvidence.Any(decision =>
                                                                                                           decision.HumanConcernId == row.Id &&
                                                                                                           decision.SourceFingerprint ==
                                                                                                           row.SourceFingerprint &&
                                                                                                           decision.Decision == "unknown"
                                                                                                           && decision.FailedAttempts < 3 &&
                                                                                                           decision.UnavailableReason ==
                                                                                                           "model-concern-match-unavailable")))
                .Where(row => row.LastModelEvidenceRetryAt == null || row.LastModelEvidenceRetryAt <= retryBefore)
                .Where(row => db.CodeInsightHumanConcerns.Count(other => !other.IsDeleted && other.CodeInsightPullRequestId == row.CodeInsightPullRequestId
                                                                                          && other.ProviderScope == row.ProviderScope &&
                                                                                          other.ConnectionId == row.ConnectionId) <= 100
                              && !db.CodeInsightHumanConcerns.Any(other => !other.IsDeleted && other.CodeInsightPullRequestId == row.CodeInsightPullRequestId
                                                                                            && other.ProviderScope == row.ProviderScope &&
                                                                                            other.ConnectionId == row.ConnectionId
                                                                                            && (!other.RetainedSourceComplete ||
                                                                                                other.LastModelEvidenceRetryAt > retryBefore)))
                .Join(
                    db.CodeInsightPullRequests.AsNoTracking(), row => row.CodeInsightPullRequestId, aggregate => aggregate.Id,
                    (row, aggregate) => new { aggregate.Id, aggregate.ClientId, row.ProviderScope, row.ConnectionId })
                .Distinct().OrderBy(row => row.Id).ThenBy(row => row.ProviderScope).ThenBy(row => row.ConnectionId).Take(Math.Min(cap, 10)).ToListAsync(ct);
            foreach (var aggregate in due)
            {
                await this.RetryCoreAsync(new(aggregate.Id, aggregate.ProviderScope, aggregate.ConnectionId), [aggregate.ClientId], false, ct);
            }
        }

        return indexed;
    }

    private async Task<Guid[]> PendingJobIdsAsync(int cap, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Database.SqlQuery<Guid>(
                $"""
                 WITH proof AS (
                     SELECT f.job_id, f.file_path, f.model_evidence_json AS evidence FROM review_file_results f
                     WHERE f.model_evidence_json IS NOT NULL
                     UNION ALL
                     SELECT j.id, evidence->>'FilePath', evidence FROM review_jobs j
                     CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(j.model_evidence_manifest_json->'PullRequestPasses')='array'
                         THEN j.model_evidence_manifest_json->'PullRequestPasses' ELSE '[]'::jsonb END) evidence
                 )
                 SELECT DISTINCT p.job_id AS "Value" FROM proof p
                 JOIN review_jobs j ON j.id=p.job_id JOIN clients c ON c.id=j.client_id AND c.code_insights_collection_enabled
                 CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(p.evidence->'Passes')='array'
                     THEN p.evidence->'Passes' ELSE '[]'::jsonb END) pass
                 WHERE pass->>'Disposition' IN ('1','Completed','completed') AND pass->>'CandidateSetComplete'='true'
                     AND char_length(p.file_path) <= 512
                     AND pass->>'AcceptedAt' IS NOT NULL AND pass->>'StartedAt' IS NOT NULL AND pass->>'CompletedAt' IS NOT NULL
                     AND pass->>'Kind' IN ('generation','pr-wide-generation') AND COALESCE(pass->>'ModelId','')<>''
                     AND NOT EXISTS (SELECT 1 FROM code_insight_review_exposures e WHERE e."JobId"=p.job_id AND e."FilePath"=p.file_path
                         AND e."ModelId"=pass->>'ModelId' AND e."LogicalModelName"=COALESCE(pass->>'LogicalModelName','') AND e."Source"={ExposureSource})
                 ORDER BY "Value" LIMIT {cap}
                 """).ToArrayAsync(ct);
        }

        var indexed = (await db.CodeInsightReviewExposures.AsNoTracking().Where(row => row.Source == ExposureSource)
                .Select(row => new { row.JobId, row.FilePath, row.ModelId, row.LogicalModelName }).Take(ModelReviewEvidenceReader.MaximumFiles + 1)
                .ToListAsync(ct))
            .Select(row => (row.JobId, row.FilePath, row.ModelId, row.LogicalModelName)).ToHashSet();
        var files = await db.ReviewFileResults.AsNoTracking().Where(row => row.ModelEvidence != null).Take(ModelReviewEvidenceReader.MaximumFiles + 1)
            .ToListAsync(ct);
        if (files.Count > ModelReviewEvidenceReader.MaximumFiles || indexed.Count > ModelReviewEvidenceReader.MaximumFiles)
        {
            return [];
        }

        var candidates = files.Where(file => IndexablePath(file.FilePath) && file.ModelEvidence!.Passes.Any(pass => pass.IsCertified &&
                pass.AcceptedAt is not null
                && !indexed.Contains(
                    (file.JobId, file.FilePath, pass.ModelId,
                        pass.LogicalModelName))))
            .Select(file => file.JobId);
        var manifests = await db.ReviewJobs.AsNoTracking().Where(job => job.ModelEvidenceManifest != null)
            .Select(job => new { job.Id, job.ModelEvidenceManifest }).Take(ModelReviewEvidenceReader.MaximumJobs + 1).ToListAsync(ct);
        return candidates.Concat(
            manifests.Where(job => job.ModelEvidenceManifest!.PullRequestPasses.Any(file => IndexablePath(file.FilePath) && file.Passes.Any(pass =>
                    pass.IsCertified
                    && pass.AcceptedAt is not null && !indexed.Contains((job.Id, file.FilePath, pass.ModelId, pass.LogicalModelName)))))
                .Select(job => job.Id)).Distinct().Order().Take(cap).ToArray();
    }

    public Task<ModelEvidenceRetryResult> RetryAsync(
        ModelEvidenceRetryTarget target, IReadOnlyCollection<Guid> authorizedClients, CancellationToken ct = default) =>
        this.RetryCoreAsync(target, authorizedClients, true, ct);

    private async Task<ModelEvidenceRetryResult> RetryCoreAsync(
        ModelEvidenceRetryTarget retryTarget, IReadOnlyCollection<Guid> authorizedClients, bool resetAttempts, CancellationToken ct)
    {
        var aggregateId = retryTarget.AggregateId;
        var aggregate = await db.CodeInsightPullRequests.SingleOrDefaultAsync(row => row.Id == aggregateId && authorizedClients.Contains(row.ClientId), ct);
        if (aggregate is null)
        {
            return ModelEvidenceRetryResult.NotFound;
        }

        if (!await gate.IsCollectionEnabledAsync(aggregate.ClientId, ct))
        {
            return ModelEvidenceRetryResult.Disabled;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var cooldown = this.RetryCooldown;
        if (string.IsNullOrWhiteSpace(retryTarget.ProviderScope) || retryTarget.ProviderScope.Length > 1024 || retryTarget.ConnectionId == Guid.Empty)
        {
            return ModelEvidenceRetryResult.Unreconstructable;
        }

        var sourceRows = db.CodeInsightHumanConcerns.Where(row => row.CodeInsightPullRequestId == aggregate.Id && !row.IsDeleted
            && row.ProviderScope == retryTarget.ProviderScope && row.ConnectionId == retryTarget.ConnectionId);
        var retained = await sourceRows.AsNoTracking().Take(101).ToListAsync(ct);
        if (retained.Any(row => row.LastModelEvidenceRetryAt > now - cooldown))
        {
            return ModelEvidenceRetryResult.Cooldown;
        }

        if (concerns is null || discussionStore is null)
        {
            return ModelEvidenceRetryResult.Unreconstructable;
        }

        if (retained.Count == 0 || retained.Count > 100 || retained.Any(row => !row.RetainedSourceComplete))
        {
            return ModelEvidenceRetryResult.Unreconstructable;
        }

        var retryableTruth = retained.Any(row => row.JudgementFailed
                                                 && (row.UnavailableReason is null or "model-concern-judgement-failed"));
        var retryableDimensions = retained.Any(row => !row.JudgementFailed && row.DimensionJudgementFailed);
        var classifiedIds = retained.Where(row => !row.JudgementFailed).Select(row => row.Id).ToArray();
        var retryableMatch = await db.CodeInsightModelConcernEvidence.AsNoTracking().AnyAsync(
            decision => classifiedIds.Contains(decision.HumanConcernId)
                        && decision.Decision == "unknown" && decision.UnavailableReason == "model-concern-match-unavailable"
                        && sourceRows.Any(row => row.Id == decision.HumanConcernId && row.SourceFingerprint == decision.SourceFingerprint), ct);
        if (!retryableTruth && !retryableDimensions && !retryableMatch)
        {
            return ModelEvidenceRetryResult.Unreconstructable;
        }

        var key = new CodeInsightPullRequestKey(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var attempted = false;
        foreach (var source in retained.GroupBy(row => (row.ProviderScope, row.ConnectionId)))
        {
            var snapshot = await new ModelReviewEvidenceReader(db).ReadAsync(
                [new(key.ClientId, key.RepositoryId, key.PullRequestId, source.Key.ProviderScope, source.Key.ConnectionId)], now, deadline.Token);
            var jobIds = snapshot.Executions.Select(execution => execution.JobId).ToArray();
            var job = await db.ReviewJobs.AsNoTracking().Where(row => jobIds.Contains(row.Id)).OrderBy(row => row.Id)
                .Select(row => new { row.Provider, row.OrganizationUrl, row.ProjectId, row.RepositoryId }).FirstOrDefaultAsync(deadline.Token);
            if (job is null || source.Key.ConnectionId == Guid.Empty)
            {
                return ModelEvidenceRetryResult.Unreconstructable;
            }

            var policy = this._policies[job.Provider];
            var coordinates = policy.GetCacheCoordinates(job.OrganizationUrl, job.ProjectId);
            var repository = policy.GetCacheCoordinates(job.OrganizationUrl, job.RepositoryId).ProjectKey;
            var target = new PullRequestThreadSnapshotTarget(
                key.ClientId, source.Key.ConnectionId, job.Provider,
                coordinates.Authority + coordinates.ScopePath, coordinates.ProjectKey, repository, checked((int)key.PullRequestId));
            await using var lease = await discussionStore.TryAcquireRetainedReplayAsync(
                target,
                (options?.CurrentValue ?? new CodeInsightsOptions()).ProviderEvidenceLease, deadline.Token);
            if (lease is null)
            {
                return ModelEvidenceRetryResult.Busy;
            }

            using var writes = lease.BeginWrites();
            await lease.EnsureCurrentAsync(deadline.Token);
            if (!attempted)
            {
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(deadline.Token) : null;
                await CodeInsightAggregateWriteLock.AcquireAsync(db, key, deadline.Token);
                if (await sourceRows.AsNoTracking().AnyAsync(row => row.LastModelEvidenceRetryAt > now - cooldown, deadline.Token))
                {
                    return ModelEvidenceRetryResult.Cooldown;
                }

                foreach (var concern in await sourceRows.ToListAsync(deadline.Token))
                {
                    concern.LastModelEvidenceRetryAt = now;
                }

                await db.SaveChangesAsync(deadline.Token);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(deadline.Token);
                }
            }

            if (resetAttempts)
            {
                var ids = source.Select(row => row.Id).ToArray();
                foreach (var concern in await db.CodeInsightHumanConcerns.Where(row => ids.Contains(row.Id)).ToListAsync(deadline.Token))
                {
                    concern.FailedAttempts = 0;
                    concern.FailedDimensionAttempts = 0;
                }

                foreach (var decision in await db.CodeInsightModelConcernEvidence.Where(row => ids.Contains(row.HumanConcernId) && row.Decision == "unknown")
                             .ToListAsync(deadline.Token))
                {
                    decision.FailedAttempts = 0;
                }

                await db.SaveChangesAsync(deadline.Token);
            }

            var batch = batches is not null
                ? await batches.CreateAsync(key, source.Key.ProviderScope, deadline.Token)
                : new(key, source.Key.ProviderScope, [], "retained-replay");
            if (batch is null)
            {
                return ModelEvidenceRetryResult.Disabled;
            }

            await concerns.PrepareAsync(batch with { ConnectionId = source.Key.ConnectionId }, deadline.Token);
            try
            {
                await concerns.ReplayRetainedAsync(key, source.Key.ProviderScope, source.Key.ConnectionId, deadline.Token);
                await lease.EnsureCurrentAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                return ModelEvidenceRetryResult.Retried;
            }

            attempted = true;
        }

        return attempted ? ModelEvidenceRetryResult.Retried : ModelEvidenceRetryResult.Unreconstructable;
    }

    private static bool IndexablePath(string path) => path.EnumerateRunes().Take(513).Count() <= 512;
}
