// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Corrects missing historical namespaces from retained jobs without replacing source evidence.</summary>
internal static class CodeInsightHistoricalSourceQualifier
{
    internal static string? CapturedScope(ReviewJob job, IScmProviderRegistry? registry)
    {
        if (registry is null || !Enum.IsDefined(job.Provider) || string.IsNullOrWhiteSpace(job.ProjectId)
            || !Uri.TryCreate(job.OrganizationUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var policy = registry.GetSourceIdentityPolicy(job.Provider);
        var capturedPath = policy.SelectCapturedSource(job.OrganizationUrl, job.HostBaseUrl);
        if (!Uri.TryCreate(capturedPath, UriKind.Absolute, out var captured) || captured.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var scope = ProviderSourceIdentity.FromReviewJob(job, policy).Value;
        return string.IsNullOrWhiteSpace(scope) || scope.Length > 1024 ? null : scope;
    }

    internal static async Task<bool> QualifyAsync(
        MeisterProPRDbContext db, CodeInsightPullRequest pr, IScmProviderRegistry? registry,
        List<CodeInsightFinding> findings, List<CodeInsightMiss> misses, List<CodeInsightReviewExposure> exposures,
        List<CodeInsightHarvestCoverage> coverage, List<CodeInsightThreadEligibility> eligibility, List<ReviewJob> jobs, CancellationToken ct)
    {
        var scopes = findings.Select(f => f.ProviderScope).Concat(misses.Select(m => m.ProviderScope))
            .Concat(exposures.Select(e => e.ProviderScope)).Concat(coverage.Select(c => c.ProviderScope)).Concat(eligibility.Select(e => e.ProviderScope));
        if (!db.Database.IsNpgsql() || !scopes.Any(string.IsNullOrWhiteSpace))
        {
            return true;
        }

        // Claims hold the aggregate row. Nonblocking locks avoid reversing the normal writer lock order.
        if (!await CodeInsightAggregateWriteLock.TryAcquireAsync(db, new(pr.ClientId, pr.RepositoryId, pr.PullRequestId), ct)
            || !await LockEvidenceAsync(db, pr, findings.Count, misses.Count, exposures.Count, coverage.Count, eligibility.Count, jobs.Count, ct))
        {
            return false;
        }

        findings.Clear();
        findings.AddRange(await db.CodeInsightFindings.AsNoTracking().Where(f => f.CodeInsightPullRequestId == pr.Id).ToListAsync(ct));
        misses.Clear();
        misses.AddRange(await db.CodeInsightMisses.AsNoTracking().Where(m => m.CodeInsightPullRequestId == pr.Id).ToListAsync(ct));
        exposures.Clear();
        exposures.AddRange(await db.CodeInsightReviewExposures.AsNoTracking().Where(e => e.CodeInsightPullRequestId == pr.Id).ToListAsync(ct));
        coverage.Clear();
        coverage.AddRange(await db.CodeInsightHarvestCoverage.AsNoTracking().Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync(ct));
        eligibility.Clear();
        eligibility.AddRange(await db.CodeInsightThreadEligibility.AsNoTracking().Where(e => e.CodeInsightPullRequestId == pr.Id).ToListAsync(ct));
        jobs.Clear();
        jobs.AddRange(
            await db.ReviewJobs.AsNoTracking().Where(j => j.ClientId == pr.ClientId && j.RepositoryId == pr.RepositoryId && j.PullRequestId == pr.PullRequestId)
                .OrderByDescending(j => j.SubmittedAt).ThenBy(j => j.Id).ToListAsync(ct));

        var captured = jobs.Select(j => (j.Id, Scope: CapturedScope(j, registry))).Where(j => j.Scope is not null).ToDictionary(j => j.Id, j => j.Scope!);
        var correctedScopes = await QualifyDirectEvidenceAsync(db, findings, exposures, captured, ct);
        var knownScopes = captured.Values.Concat(findings.Select(f => f.ProviderScope)).Concat(exposures.Select(e => e.ProviderScope))
            .Concat(misses.Select(m => m.ProviderScope)).Concat(coverage.Select(c => c.ProviderScope)).Concat(eligibility.Select(e => e.ProviderScope))
            .Append(pr.MissHarvestProviderScope).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        var unattributedDirectEvidence = findings.Any(f => string.IsNullOrWhiteSpace(f.ProviderScope)) ||
                                         exposures.Any(e => string.IsNullOrWhiteSpace(e.ProviderScope));
        if (!unattributedDirectEvidence && captured.Count == jobs.Count && knownScopes.Count == 1 &&
            captured.Values.Contains(knownScopes[0], StringComparer.Ordinal))
        {
            var scope = knownScopes[0];
            foreach (var miss in misses.Where(m => string.IsNullOrWhiteSpace(m.ProviderScope)))
            {
                // Conflicting successful judgments retain separate identities and prevent completion.
                if (misses.Any(m => m.ProviderScope == scope && m.ProviderThreadId == miss.ProviderThreadId))
                {
                    continue;
                }

                await db.CodeInsightMisses.Where(m => m.Id == miss.Id).ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ProviderScope, scope), ct);
                miss.ProviderScope = scope;
                correctedScopes.Add(scope);
            }

            if (!misses.Any(m => string.IsNullOrWhiteSpace(m.ProviderScope)))
            {
                if (await QualifyEligibilityAsync(db, eligibility, scope, ct))
                {
                    // Reassociated eligibility requires a fresh acknowledged source observation.
                    correctedScopes.Add(scope);
                }

                await QualifyCoverageAsync(db, pr, coverage, scope, ct);
            }
        }

        foreach (var source in coverage.Where(c => correctedScopes.Contains(c.ProviderScope) && c.EnumerationComplete))
        {
            await db.CodeInsightHarvestCoverage.Where(c => c.Id == source.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.EnumerationComplete, false), ct);
            source.EnumerationComplete = false;
        }

        return true;
    }

    private static async Task<HashSet<string>> QualifyDirectEvidenceAsync(
        MeisterProPRDbContext db, List<CodeInsightFinding> findings, List<CodeInsightReviewExposure> exposures,
        Dictionary<Guid, string> captured, CancellationToken ct)
    {
        var corrected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings.Where(f => string.IsNullOrWhiteSpace(f.ProviderScope)))
        {
            if (captured.TryGetValue(finding.JobId, out var scope))
            {
                await db.CodeInsightFindings.Where(f => f.Id == finding.Id).ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ProviderScope, scope), ct);
                finding.ProviderScope = scope;
                corrected.Add(scope);
            }
        }

        foreach (var exposure in exposures.Where(e => string.IsNullOrWhiteSpace(e.ProviderScope)))
        {
            if (captured.TryGetValue(exposure.JobId, out var scope))
            {
                await db.CodeInsightReviewExposures.Where(e => e.Id == exposure.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ProviderScope, scope), ct);
                exposure.ProviderScope = scope;
                corrected.Add(scope);
            }
        }

        return corrected;
    }

    private static async Task<bool> QualifyEligibilityAsync(
        MeisterProPRDbContext db, List<CodeInsightThreadEligibility> rows, string scope, CancellationToken ct)
    {
        var legacyRows = rows.Where(e => string.IsNullOrWhiteSpace(e.ProviderScope)).ToList();
        foreach (var legacy in legacyRows)
        {
            var canonical = rows.SingleOrDefault(e => e.ProviderScope == scope && e.ProviderThreadId == legacy.ProviderThreadId);
            if (canonical is null)
            {
                var fingerprint = Convert.ToHexString(
                    SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(new[] { legacy.CodeInsightPullRequestId.ToString("D"), scope, legacy.ProviderThreadId })));
                await db.CodeInsightThreadEligibility.Where(e => e.Id == legacy.Id).ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(e => e.ProviderScope, scope).SetProperty(e => e.IdentityFingerprint, fingerprint), ct);
                rows.Remove(legacy);
                rows.Add(
                    new CodeInsightThreadEligibility
                    {
                        Id = legacy.Id, CodeInsightPullRequestId = legacy.CodeInsightPullRequestId, ProviderScope = scope,
                        ProviderThreadId = legacy.ProviderThreadId,
                        IdentityFingerprint = fingerprint, SourceObservedAt = legacy.SourceObservedAt, ExcludedFromHumanMisses = legacy.ExcludedFromHumanMisses
                    });
                continue;
            }

            var latest = legacy.SourceObservedAt > canonical.SourceObservedAt ? legacy : canonical;
            var excluded = latest.ExcludedFromHumanMisses || (legacy.SourceObservedAt == canonical.SourceObservedAt && legacy.ExcludedFromHumanMisses);
            await db.CodeInsightThreadEligibility.Where(e => e.Id == canonical.Id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.SourceObservedAt, latest.SourceObservedAt).SetProperty(e => e.ExcludedFromHumanMisses, excluded), ct);
            canonical.SourceObservedAt = latest.SourceObservedAt;
            canonical.ExcludedFromHumanMisses = excluded;
            await db.CodeInsightThreadEligibility.Where(e => e.Id == legacy.Id).ExecuteDeleteAsync(ct);
            rows.Remove(legacy);
        }

        return legacyRows.Count > 0;
    }

    private static async Task QualifyCoverageAsync(
        MeisterProPRDbContext db, CodeInsightPullRequest pr, List<CodeInsightHarvestCoverage> rows, string scope, CancellationToken ct)
    {
        foreach (var legacy in rows.Where(c => string.IsNullOrWhiteSpace(c.ProviderScope)).ToList())
        {
            var canonical = rows.SingleOrDefault(c => c.ProviderScope == scope);
            if (canonical is null)
            {
                canonical = new CodeInsightHarvestCoverage
                {
                    Id = legacy.Id, CodeInsightPullRequestId = pr.Id, ProviderScope = scope,
                    ObservedAt = legacy.ObservedAt, AllHumanThreadsResolved = legacy.AllHumanThreadsResolved, EnumerationComplete = false
                };
                await db.CodeInsightHarvestCoverage.Where(c => c.Id == legacy.Id).ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(c => c.ProviderScope, scope).SetProperty(c => c.EnumerationComplete, false), ct);
                rows.Add(canonical);
            }
            else
            {
                var latest = legacy.ObservedAt > canonical.ObservedAt ? legacy : canonical;
                await db.CodeInsightHarvestCoverage.Where(c => c.Id == canonical.Id).ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(c => c.ObservedAt, latest.ObservedAt).SetProperty(c => c.AllHumanThreadsResolved, latest.AllHumanThreadsResolved)
                        .SetProperty(c => c.EnumerationComplete, false), ct);
                canonical.ObservedAt = latest.ObservedAt;
                canonical.AllHumanThreadsResolved = latest.AllHumanThreadsResolved;
                canonical.EnumerationComplete = false;
                await db.CodeInsightHarvestCoverage.Where(c => c.Id == legacy.Id).ExecuteDeleteAsync(ct);
            }

            rows.Remove(legacy);
        }

        if (pr.MissHarvestObservedAt is not null && string.IsNullOrWhiteSpace(pr.MissHarvestProviderScope))
        {
            pr.MissHarvestProviderScope = scope;
            if (rows.Count == 0)
            {
                var pending = new CodeInsightHarvestCoverage
                {
                    Id = Guid.CreateVersion7(), CodeInsightPullRequestId = pr.Id, ProviderScope = scope, ObservedAt = pr.MissHarvestObservedAt.Value,
                    AllHumanThreadsResolved = pr.MissHarvestSettled, EnumerationComplete = false
                };
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO code_insight_harvest_coverage
                     ("Id", "CodeInsightPullRequestId", "ProviderScope", "ObservedAt", "AllHumanThreadsResolved", "EnumerationComplete")
                     VALUES ({pending.Id}, {pr.Id}, {scope}, {pending.ObservedAt}, {pending.AllHumanThreadsResolved}, false)
                     """, ct);
                rows.Add(pending);
            }
        }
    }

    private static async Task<bool> LockEvidenceAsync(
        MeisterProPRDbContext db, CodeInsightPullRequest pr, int findings, int misses, int exposures, int coverage, int eligibility, int jobs,
        CancellationToken ct)
    {
        // Captured job coordinates are immutable. This lock prevents deletion while allowing lease and result updates.
        return await LockRowsAsync(
                   db, $"SELECT id AS \"Value\" FROM code_insight_findings WHERE code_insight_pull_request_id = {pr.Id} ORDER BY id FOR UPDATE SKIP LOCKED",
                   findings, ct)
               && await LockRowsAsync(
                   db, $"SELECT id AS \"Value\" FROM code_insight_misses WHERE code_insight_pull_request_id = {pr.Id} ORDER BY id FOR UPDATE SKIP LOCKED",
                   misses, ct)
               && await LockRowsAsync(
                   db,
                   $"SELECT \"Id\" AS \"Value\" FROM code_insight_review_exposures WHERE \"CodeInsightPullRequestId\" = {pr.Id} ORDER BY \"Id\" FOR UPDATE SKIP LOCKED",
                   exposures, ct)
               && await LockRowsAsync(
                   db,
                   $"SELECT \"Id\" AS \"Value\" FROM code_insight_harvest_coverage WHERE \"CodeInsightPullRequestId\" = {pr.Id} ORDER BY \"Id\" FOR UPDATE SKIP LOCKED",
                   coverage, ct)
               && await LockRowsAsync(
                   db,
                   $"SELECT \"Id\" AS \"Value\" FROM code_insight_thread_eligibility WHERE \"CodeInsightPullRequestId\" = {pr.Id} ORDER BY \"Id\" FOR UPDATE SKIP LOCKED",
                   eligibility, ct)
               && await LockRowsAsync(
                   db,
                   $"SELECT id AS \"Value\" FROM review_jobs WHERE client_id = {pr.ClientId} AND repository_id = {pr.RepositoryId} AND pull_request_id = {pr.PullRequestId} ORDER BY id FOR KEY SHARE SKIP LOCKED",
                   jobs, ct);
    }

    private static async Task<bool> LockRowsAsync(MeisterProPRDbContext db, FormattableString query, int expected, CancellationToken ct)
    {
        return (await db.Database.SqlQuery<Guid>(query).ToListAsync(ct)).Count == expected;
    }
}
