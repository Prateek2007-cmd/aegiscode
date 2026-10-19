// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Persists bounded historical observation claims independently of retention and score metadata.</summary>
public sealed class CodeInsightHistoricalRefreshStore(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    IScmProviderRegistry? providerRegistry = null) : ICodeInsightHistoricalRefreshStore
{
    public const int MaximumPullRequests = 250;

    // Match managed whitespace checks when selecting persisted recovery gaps.
    private const string SqlWhitespaceCharacters =
        "\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";

    public async Task<IReadOnlyList<CodeInsightHistoricalRefreshCandidate>> ClaimAsync(
        int maximum, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var clients = await db.CodeInsightPullRequests.AsNoTracking().Select(p => p.ClientId).Distinct().ToListAsync(ct);
        var enabled = new List<Guid>();
        foreach (var client in clients)
        {
            if (await gate.IsCollectionEnabledAsync(client, ct))
            {
                enabled.Add(client);
            }
        }

        if (enabled.Count == 0)
        {
            return [];
        }

        var cap = Math.Clamp(maximum, 1, MaximumPullRequests);
        var duration = TimeSpan.FromMinutes(Math.Clamp(leaseDuration.TotalMinutes, 1, 60));
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var candidates = new List<CodeInsightHistoricalRefreshCandidate>();
        var scanned = new List<Guid>();
        // Bound qualification independently of the claim count so busy evidence does not end the search.
        while (candidates.Count < cap && scanned.Count < MaximumPullRequests)
        {
            var batchSize = Math.Min(cap - candidates.Count, MaximumPullRequests - scanned.Count);
            var rows = await ReadClaimablePullRequestsAsync(db, enabled, scanned, batchSize, now, ct);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                scanned.Add(row.Id);
                if (candidates.Count == cap)
                {
                    break;
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(row).ReloadAsync(ct);
                }

                if (!await gate.IsCollectionEnabledAsync(row.ClientId, ct))
                {
                    continue;
                }

                if (transaction is not null)
                {
                    await transaction.CreateSavepointAsync("source_qualification", ct);
                }

                var sources = await this.ReadSourcesAsync(db, row, ct);
                if (sources is null || !await gate.IsCollectionEnabledAsync(row.ClientId, ct))
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackToSavepointAsync("source_qualification", ct);
                        await db.Entry(row).ReloadAsync(ct);
                    }

                    continue;
                }

                var gaps = sources.Aggregate(CodeInsightHistoricalGap.None, (current, source) => current | source.Gaps);
                var unfinishedAttempt = row.LastHistoricalRefreshAttemptAt is { } started
                                        && !(row.LastHistoricalRefreshCompletedAt >= started);
                if (!IsExplicitlyQueued(row) && (sources.Count == 0 || (gaps == CodeInsightHistoricalGap.None && !unfinishedAttempt)))
                {
                    continue;
                }

                var attempt = Guid.CreateVersion7();
                row.LastHistoricalRefreshAttemptId = attempt;
                row.LastHistoricalRefreshAttemptAt = now;
                row.HistoricalRefreshNotBefore = now + duration;
                row.LastHistoricalRefreshReason = null;
                candidates.Add(
                    new CodeInsightHistoricalRefreshCandidate(
                        row.Id, attempt, now, now + duration,
                        new CodeInsightPullRequestKey(row.ClientId, row.RepositoryId, row.PullRequestId), gaps, sources));
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return candidates;
    }

    /// <summary>Locks the next bounded batch of retained gaps or unfinished attempts, excluding inspected candidates.</summary>
    private static async Task<List<CodeInsightPullRequest>> ReadClaimablePullRequestsAsync(
        MeisterProPRDbContext db, List<Guid> enabled, List<Guid> scanned, int cap, DateTimeOffset now, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            var repairReasons = CodeInsightHistoricalRecoveryPolicy.RepairRequiredReasons;
            return await db.CodeInsightPullRequests.Where(p => enabled.Contains(p.ClientId)
                                                               && !scanned.Contains(p.Id)
                                                               && (p.LastHistoricalRefreshReason == null ||
                                                                   !repairReasons.Contains(p.LastHistoricalRefreshReason))
                                                               && (p.HistoricalRefreshNotBefore == null || p.HistoricalRefreshNotBefore <= now))
                .OrderByDescending(p =>
                    p.LastHistoricalRefreshAttemptId == null && p.HistoricalRefreshNotBefore != null && p.LastHistoricalRefreshReason == null)
                .ThenBy(p => p.LastHistoricalRefreshAttemptAt).ThenBy(p => p.Id).Take(cap).ToListAsync(ct);
        }

        var ids = enabled.ToArray();
        var inspected = scanned.ToArray();
        var requiresRepair = CodeInsightHistoricalRecoveryPolicy.RepairRequiredReasons;
        return await db.CodeInsightPullRequests.FromSqlInterpolated(
            $"""
             SELECT pr.* FROM code_insight_pull_requests pr
             CROSS JOIN LATERAL (SELECT
               (EXISTS (SELECT 1 FROM code_insight_findings f WHERE f.code_insight_pull_request_id = pr.id) OR
                EXISTS (SELECT 1 FROM code_insight_misses m WHERE m.code_insight_pull_request_id = pr.id) OR
                EXISTS (SELECT 1 FROM code_insight_review_exposures e WHERE e."CodeInsightPullRequestId" = pr.id) OR
                EXISTS (SELECT 1 FROM code_insight_harvest_coverage c WHERE c."CodeInsightPullRequestId" = pr.id) OR
                EXISTS (SELECT 1 FROM code_insight_thread_eligibility e WHERE e."CodeInsightPullRequestId" = pr.id)) AS has_retained_evidence,
               EXISTS (SELECT 1 FROM code_insight_findings f
                 WHERE f.code_insight_pull_request_id = pr.id AND
                 (f."PublicationState" = 1 OR (f."PublicationState" = 0 AND
                   (NULLIF(btrim(f.provider_thread_id, {SqlWhitespaceCharacters}), '') IS NOT NULL OR
                    NULLIF(btrim(f.provider_comment_id, {SqlWhitespaceCharacters}), '') IS NOT NULL))) AND
                 (NULLIF(btrim(f."NativeStatus", {SqlWhitespaceCharacters}), '') IS NULL OR
                  (f."PublicationState" = 1 AND NULLIF(btrim(f.provider_thread_id, {SqlWhitespaceCharacters}), '') IS NULL) OR
                  lower(btrim(f."NativeStatus", {SqlWhitespaceCharacters})) NOT IN ('active', 'pending', 'fixed', 'closed', 'wontfix', 'bydesign') OR
                  (lower(btrim(f."NativeStatus", {SqlWhitespaceCharacters})) NOT IN ('active', 'pending') AND
                   (f."CurrentDisposition" IS NULL OR
                    (f."CurrentClassifierVersion" IS NOT NULL AND f."CurrentClassifierConfidence" IS NULL) OR
                    NULLIF(btrim(f."OutcomeSourceFingerprint", {SqlWhitespaceCharacters}), '') IS NULL OR f."OutcomeObservedAt" IS NULL)))) AS needs_native_outcome,
               EXISTS (SELECT 1 FROM code_insight_misses m
                 WHERE m.code_insight_pull_request_id = pr.id AND NOT m."ExcludedAsOwnFinding"
                   AND NULLIF(btrim(m."SourceFingerprint", {SqlWhitespaceCharacters}), '') IS NULL) AS needs_miss_fingerprint,
               EXISTS (SELECT 1 FROM (
                 SELECT f."ProviderScope" AS scope FROM code_insight_findings f WHERE f.code_insight_pull_request_id = pr.id
                 UNION SELECT e."ProviderScope" FROM code_insight_review_exposures e WHERE e."CodeInsightPullRequestId" = pr.id
                 UNION SELECT m."ProviderScope" FROM code_insight_misses m WHERE m.code_insight_pull_request_id = pr.id
                 UNION SELECT c."ProviderScope" FROM code_insight_harvest_coverage c WHERE c."CodeInsightPullRequestId" = pr.id
                 UNION SELECT e."ProviderScope" FROM code_insight_thread_eligibility e WHERE e."CodeInsightPullRequestId" = pr.id
               ) sources WHERE NULLIF(btrim(sources.scope, {SqlWhitespaceCharacters}), '') IS NULL OR NOT EXISTS
                 (SELECT 1 FROM code_insight_harvest_coverage c WHERE c."CodeInsightPullRequestId" = pr.id
                  AND c."ProviderScope" = sources.scope AND c."EnumerationComplete")) AS needs_harvest_coverage
             ) gaps
             WHERE pr.client_id = ANY({ids})
               AND NOT (pr.id = ANY({inspected}))
               AND (pr."LastHistoricalRefreshReason" IS NULL OR NOT (pr."LastHistoricalRefreshReason" = ANY({requiresRepair})))
               AND (pr."HistoricalRefreshNotBefore" IS NULL OR pr."HistoricalRefreshNotBefore" <= {now})
               AND (
                 (pr."LastHistoricalRefreshAttemptId" IS NULL AND pr."HistoricalRefreshNotBefore" IS NOT NULL AND pr."LastHistoricalRefreshReason" IS NULL)
                 OR
                 -- Partial writes do not complete an interrupted attempt.
                 (gaps.has_retained_evidence AND pr."LastHistoricalRefreshAttemptAt" IS NOT NULL AND
                  (pr."LastHistoricalRefreshCompletedAt" IS NULL OR pr."LastHistoricalRefreshCompletedAt" < pr."LastHistoricalRefreshAttemptAt"))
                 OR gaps.needs_native_outcome OR gaps.needs_miss_fingerprint OR gaps.needs_harvest_coverage)
             ORDER BY (pr."LastHistoricalRefreshAttemptId" IS NULL AND pr."HistoricalRefreshNotBefore" IS NOT NULL AND pr."LastHistoricalRefreshReason" IS NULL) DESC,
               pr."LastHistoricalRefreshAttemptAt" ASC NULLS FIRST, pr.id
             LIMIT {cap} FOR UPDATE OF pr SKIP LOCKED
             """).ToListAsync(ct);
    }

    public async Task<bool> RecordResultAsync(
        Guid aggregateId, Guid attemptId, CodeInsightObservationResult observation,
        DateTimeOffset completedAt, TimeSpan retryCooldown, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (observation.Status == CodeInsightObservationStatus.Observed && observation.ObservedAt is null)
        {
            return false;
        }

        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM code_insight_pull_requests WHERE id = {aggregateId} FOR UPDATE", ct);
        }

        var row = await db.CodeInsightPullRequests.FirstOrDefaultAsync(p => p.Id == aggregateId, ct);
        if (row is not null && db.Database.IsRelational())
        {
            await db.Entry(row).ReloadAsync(ct);
        }

        if (row is null || row.LastHistoricalRefreshAttemptId != attemptId ||
            row.LastHistoricalRefreshAttemptAt > completedAt || row.HistoricalRefreshNotBefore <= completedAt ||
            row.LastHistoricalRefreshReason is not null || row.LastHistoricalRefreshCompletedAt >= row.LastHistoricalRefreshAttemptAt ||
            !await gate.IsCollectionEnabledAsync(row.ClientId, ct))
        {
            return false;
        }

        if (observation.Status == CodeInsightObservationStatus.Observed)
        {
            row.LastHistoricalRefreshCompletedAt = completedAt;
            row.HistoricalRefreshNotBefore = completedAt;
        }
        else
        {
            row.LastHistoricalRefreshReason = observation.Status == CodeInsightObservationStatus.Disabled
                ? "CollectionDisabled"
                : (observation.UnavailableReason ?? CodeInsightObservationUnavailableReason.ProviderReadFailed).ToString();
            row.HistoricalRefreshNotBefore = completedAt + TimeSpan.FromMinutes(Math.Clamp(retryCooldown.TotalMinutes, 10, 1440));
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    public async Task<CodeInsightHistoricalRequeueResult> RequeueAsync(
        Guid aggregateId, IReadOnlyCollection<Guid> authorizedClients, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var clients = authorizedClients.ToArray();
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM code_insight_pull_requests WHERE id = {aggregateId} AND client_id = ANY({clients}) FOR UPDATE", ct);
        }

        var row = await db.CodeInsightPullRequests.FirstOrDefaultAsync(p => p.Id == aggregateId && clients.Contains(p.ClientId), ct);
        if (row is null)
        {
            return CodeInsightHistoricalRequeueResult.NotFound;
        }

        if (db.Database.IsRelational())
        {
            await db.Entry(row).ReloadAsync(ct);
        }

        if (!await gate.IsCollectionEnabledAsync(row.ClientId, ct))
        {
            return CodeInsightHistoricalRequeueResult.CollectionDisabled;
        }

        if (row.LastHistoricalRefreshReason is null && row.LastHistoricalRefreshAttemptId is not null
                                                    && row.LastHistoricalRefreshAttemptAt is { } attempted &&
                                                    !(row.LastHistoricalRefreshCompletedAt >= attempted)
                                                    && row.HistoricalRefreshNotBefore > now)
        {
            return CodeInsightHistoricalRequeueResult.InProgress;
        }

        row.LastHistoricalRefreshAttemptId = null;
        row.LastHistoricalRefreshReason = null;
        row.HistoricalRefreshNotBefore = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return CodeInsightHistoricalRequeueResult.Queued;
    }

    // Explicit requeue clears the attempt token while retaining a due cutoff; claims replace that token.
    private static bool IsExplicitlyQueued(CodeInsightPullRequest row) => row.LastHistoricalRefreshAttemptId is null
                                                                          && row.HistoricalRefreshNotBefore is not null &&
                                                                          row.LastHistoricalRefreshReason is null;

    private async Task<IReadOnlyList<CodeInsightHistoricalRefreshSource>?> ReadSourcesAsync(
        MeisterProPRDbContext db, CodeInsightPullRequest pr, CancellationToken ct)
    {
        var findings = await db.CodeInsightFindings.AsNoTracking().Where(f => f.CodeInsightPullRequestId == pr.Id).ToListAsync(ct);
        var misses = await db.CodeInsightMisses.AsNoTracking().Where(m => m.CodeInsightPullRequestId == pr.Id).ToListAsync(ct);
        var exposures = await db.CodeInsightReviewExposures.AsNoTracking().Where(e => e.CodeInsightPullRequestId == pr.Id).ToListAsync(ct);
        var coverage = await db.CodeInsightHarvestCoverage.AsNoTracking().Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync(ct);
        var eligibility = await db.CodeInsightThreadEligibility.AsNoTracking().Where(e => e.CodeInsightPullRequestId == pr.Id).ToListAsync(ct);
        var jobs = await db.ReviewJobs.AsNoTracking().Where(j => j.ClientId == pr.ClientId &&
                                                                 j.RepositoryId == pr.RepositoryId && j.PullRequestId == pr.PullRequestId)
            .OrderByDescending(j => j.SubmittedAt).ThenBy(j => j.Id).ToListAsync(ct);
        if (!await CodeInsightHistoricalSourceQualifier.QualifyAsync(db, pr, providerRegistry, findings, misses, exposures, coverage, eligibility, jobs, ct))
        {
            return null;
        }

        var scopes = findings.Select(f => f.ProviderScope).Concat(misses.Select(m => m.ProviderScope))
            .Concat(exposures.Select(e => e.ProviderScope)).Concat(coverage.Select(c => c.ProviderScope)).Concat(eligibility.Select(e => e.ProviderScope))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var result = new List<CodeInsightHistoricalRefreshSource>();
        foreach (var scope in scopes)
        {
            var scoped = findings.Where(f => f.ProviderScope == scope).ToList();
            var gaps = CodeInsightHistoricalGap.None;
            if (scoped.Any(NeedsNative))
            {
                gaps |= CodeInsightHistoricalGap.NativeOutcome;
            }

            if (string.IsNullOrWhiteSpace(scope) || !coverage.Any(c => c.ProviderScope == scope && c.EnumerationComplete))
            {
                gaps |= CodeInsightHistoricalGap.HarvestCoverage;
            }

            if (misses.Any(m => m.ProviderScope == scope && !m.ExcludedAsOwnFinding && string.IsNullOrWhiteSpace(m.SourceFingerprint)))
            {
                gaps |= CodeInsightHistoricalGap.MissFingerprint;
            }

            var sourceJobs = scoped.Select(f => f.JobId).Concat(exposures.Where(e => e.ProviderScope == scope).Select(e => e.JobId)).ToHashSet();
            var matchingJobs = string.IsNullOrWhiteSpace(scope)
                ? []
                : jobs.Where(j => CodeInsightHistoricalSourceQualifier.CapturedScope(j, providerRegistry) == scope).ToList();
            var representative = matchingJobs.OrderByDescending(j => sourceJobs.Contains(j.Id)).Select(j => (Guid?)j.Id).FirstOrDefault();
            result.Add(new CodeInsightHistoricalRefreshSource(scope, representative, gaps));
        }

        return result;
    }

    private static bool NeedsNative(CodeInsightFinding finding)
    {
        return (finding.PublicationState == CodeInsightPublicationState.Published || (finding.PublicationState == CodeInsightPublicationState.Unknown &&
                                                                                      (!string.IsNullOrWhiteSpace(finding.ProviderThreadId) ||
                                                                                       !string.IsNullOrWhiteSpace(finding.ProviderCommentId)))) &&
               (string.IsNullOrWhiteSpace(finding.NativeStatus) ||
                (finding.PublicationState == CodeInsightPublicationState.Published && string.IsNullOrWhiteSpace(finding.ProviderThreadId)) ||
                CodeInsightNativeResolution.InterpretKnown(finding.NativeStatus) is null ||
                (CodeInsightNativeResolution.InterpretKnown(finding.NativeStatus) != ThreadResolutionIntent.Active &&
                 (finding.CurrentDisposition is null ||
                  (finding.CurrentClassifierVersion is not null && finding.CurrentClassifierConfidence is null) ||
                  string.IsNullOrWhiteSpace(finding.OutcomeSourceFingerprint) || finding.OutcomeObservedAt is null)));
    }
}
