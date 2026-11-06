// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     Read-only source of the stored scan/finding history a trend needs.
///     Read-only on purpose: trends aggregate what scans already recorded and
///     never write anything. The EF implementation reads the existing
///     SecurityScans / SecurityFindings tables; no new table, no migration.
/// </summary>
public interface ISecurityScanHistoryReader
{
    /// <summary>
    ///     Completed scans (Succeeded or PartiallySucceeded) for the client,
    ///     oldest first, plus the findings whose first or last sighting falls
    ///     in that window. Failed, cancelled, queued and running scans carry
    ///     no findings and are excluded — they are not trend points.
    /// </summary>
    Task<SecurityScanHistory> ReadHistoryAsync(Guid clientId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class EfSecurityScanHistoryReader(MeisterProPRDbContext db) : ISecurityScanHistoryReader
{
    /// <summary>Most recent completed scans kept in one trend window.</summary>
    internal const int MaxScansInWindow = 500;

    /// <inheritdoc />
    public async Task<SecurityScanHistory> ReadHistoryAsync(Guid clientId, CancellationToken ct)
    {
        var scans = await db.SecurityScans
            .AsNoTracking()
            .Where(s => s.ClientId == clientId
                        && (s.Status == (int)SecurityScanStatus.Succeeded
                            || s.Status == (int)SecurityScanStatus.PartiallySucceeded))
            .OrderByDescending(s => s.FinishedAt ?? s.QueuedAt)
            .ThenByDescending(s => s.QueuedAt)
            .Take(MaxScansInWindow)
            .Select(s => new TrendScanSnapshot(
                s.Id,
                s.RepositoryUrl,
                s.FinishedAt ?? s.QueuedAt,
                s.CriticalCount,
                s.HighCount,
                s.MediumCount,
                s.LowCount,
                s.InfoCount))
            .ToListAsync(ct);
        scans.Reverse();

        var scanIds = scans.Select(s => s.ScanId).ToHashSet();
        var findings = await db.SecurityFindings
            .AsNoTracking()
            .Where(f => f.ClientId == clientId
                        && (scanIds.Contains(f.FirstSeenScanId) || scanIds.Contains(f.LastSeenScanId)))
            .Select(f => new TrendFindingSnapshot(
                f.Fingerprint,
                f.FirstSeenScanId,
                f.LastSeenScanId,
                f.FirstSeenAt,
                f.Status))
            .ToListAsync(ct);

        return new SecurityScanHistory(scans, findings);
    }
}

/// <summary>Security posture over time, aggregated from stored scans and findings.</summary>
public interface ISecurityTrendsService
{
    /// <summary>
    ///     Returns the client's posture trend. With fewer than two completed
    ///     scans the response says there is not enough history yet and carries
    ///     no points — a trend is never invented.
    /// </summary>
    Task<SecurityTrendsResponse> GetTrendsAsync(Guid clientId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SecurityTrendsService(ISecurityScanHistoryReader historyReader) : ISecurityTrendsService
{
    /// <summary>Minimum completed scans before a trend exists at all.</summary>
    internal const int MinScansForTrend = 2;

    /// <summary>Returned when the client has fewer than <see cref="MinScansForTrend"/> completed scans.</summary>
    public const string NotEnoughHistoryMessage =
        "Not enough history yet — run at least 2 completed scans to see trends.";

    /// <inheritdoc />
    public async Task<SecurityTrendsResponse> GetTrendsAsync(Guid clientId, CancellationToken ct)
    {
        var history = await historyReader.ReadHistoryAsync(clientId, ct);
        return ComputeTrends(history);
    }

    /// <summary>
    ///     Pure aggregation over stored history, so it is unit-testable with a
    ///     fake reader. Rules, all derived from the stored data model:
    ///     <list type="bullet">
    ///         <item>Severity counts per point are the scan's stored counts.</item>
    ///         <item>
    ///             New in a scan: findings whose first-seen scan is this scan.
    ///         </item>
    ///         <item>
    ///             Fixed in a scan: findings whose last-seen scan is the previous
    ///             completed scan of the <i>same repository</i>, that are now
    ///             Resolved, and that belong to that repository (first seen in
    ///             one of its scans). The scan worker only resolves a finding
    ///             when a later scan of its own repository misses it, so the
    ///             "previous scan" is always per-repository, never the previous
    ///             scan overall — a scan of repo B never fixes repo A's findings.
    ///         </item>
    ///         <item>
    ///             Mean time to resolve: for each finding currently Resolved,
    ///             from its first-seen time to the finish time of the first
    ///             completed scan of its repository after its last-seen scan —
    ///             the scan where it demonstrably disappeared. A resolved
    ///             finding with no such successor scan is left out rather than
    ///             guessed at. Findings still open are never counted as resolved.
    ///             One stored limitation: a fingerprint is shared client-wide,
    ///             so the same package vulnerability in two repositories shares
    ///             one finding row; when its last-seen scan belongs to the other
    ///             repository the fixing scan cannot be proven, and the finding
    ///             is left out of the fixed and MTTR counts.
    ///         </item>
    ///     </list>
    /// </summary>
    internal static SecurityTrendsResponse ComputeTrends(SecurityScanHistory history)
    {
        var scans = history.Scans;
        if (scans.Count < MinScansForTrend)
        {
            return new SecurityTrendsResponse(
                HasEnoughHistory: false,
                ScanCount: scans.Count,
                Points: [],
                MeanTimeToResolveDays: null,
                ResolvedFindingsCount: 0,
                Message: NotEnoughHistoryMessage);
        }

        var scanById = scans.ToDictionary(s => s.ScanId);
        var scansByRepo = scans
            .GroupBy(s => s.RepositoryUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Previous completed scan of the same repository, per scan.
        var previousByScan = new Dictionary<Guid, Guid>();
        foreach (var repoScans in scansByRepo.Values)
        {
            for (var i = 1; i < repoScans.Count; i++)
            {
                previousByScan[repoScans[i].ScanId] = repoScans[i - 1].ScanId;
            }
        }

        var newByScan = history.Findings
            .Where(f => scanById.ContainsKey(f.FirstSeenScanId))
            .GroupBy(f => f.FirstSeenScanId)
            .ToDictionary(g => g.Key, g => g.Count());

        var fixedByScan = new Dictionary<Guid, int>();
        foreach (var finding in history.Findings)
        {
            if (finding.Status != (int)SecurityFindingStatus.Resolved)
            {
                continue;
            }

            // The scan that fixed it is the first completed scan of its
            // repository after its last-seen scan.
            if (!scanById.TryGetValue(finding.FirstSeenScanId, out var firstSeenScan))
            {
                continue;
            }

            var repoScans = scansByRepo[firstSeenScan.RepositoryUrl];
            var lastSeenIndex = repoScans.FindIndex(s => s.ScanId == finding.LastSeenScanId);
            if (lastSeenIndex < 0 || lastSeenIndex + 1 >= repoScans.Count)
            {
                // No demonstrable fixing scan: either the last-seen scan is
                // the newest scan of this repository, or the fingerprint was
                // also seen in another repository's scan (one fingerprint row
                // is shared client-wide, so the last-seen scan can belong to
                // a different repo). Left out rather than guessed at.
                continue;
            }

            var fixingScanId = repoScans[lastSeenIndex + 1].ScanId;
            fixedByScan[fixingScanId] = fixedByScan.TryGetValue(fixingScanId, out var count) ? count + 1 : 1;
        }

        var points = scans.Select(s => new SecurityTrendPointResponse(
            s.ScanId,
            s.RepositoryUrl,
            s.At,
            s.Critical,
            s.High,
            s.Medium,
            s.Low,
            s.Info,
            s.Critical + s.High + s.Medium + s.Low + s.Info,
            newByScan.TryGetValue(s.ScanId, out var fresh) ? fresh : 0,
            fixedByScan.TryGetValue(s.ScanId, out var fixedCount) ? fixedCount : 0)).ToList();

        // Mean time to resolve, over findings that demonstrably disappeared.
        var resolveDays = new List<double>();
        foreach (var finding in history.Findings)
        {
            if (finding.Status != (int)SecurityFindingStatus.Resolved)
            {
                continue;
            }

            if (!scanById.TryGetValue(finding.FirstSeenScanId, out var firstSeenScan))
            {
                continue;
            }

            var repoScans = scansByRepo[firstSeenScan.RepositoryUrl];
            var lastSeenIndex = repoScans.FindIndex(s => s.ScanId == finding.LastSeenScanId);
            if (lastSeenIndex < 0 || lastSeenIndex + 1 >= repoScans.Count)
            {
                continue;
            }

            var disappearedAt = repoScans[lastSeenIndex + 1].At;
            var days = (disappearedAt - finding.FirstSeenAt).TotalDays;
            if (days < 0)
            {
                // Inconsistent stored timestamps — leave this finding out
                // rather than letting a negative duration corrupt the mean.
                continue;
            }

            resolveDays.Add(days);
        }

        double? meanDays = resolveDays.Count == 0
            ? null
            : Math.Round(resolveDays.Average(), 1);

        return new SecurityTrendsResponse(
            HasEnoughHistory: true,
            ScanCount: scans.Count,
            Points: points,
            MeanTimeToResolveDays: meanDays,
            ResolvedFindingsCount: resolveDays.Count,
            Message: null);
    }
}
