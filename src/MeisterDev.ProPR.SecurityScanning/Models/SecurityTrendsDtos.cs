// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     One point on the posture trend: a single completed scan and what it
///     reported. Severity counts are the scan's stored denormalized counts —
///     what that scan actually reported, never recomputed or smoothed.
///     <see cref="NewFindings"/> counts findings first seen in this scan;
///     <see cref="FixedFindings"/> counts findings present in the previous
///     completed scan of the same repository but absent from this one.
/// </summary>
public sealed record SecurityTrendPointResponse(
    Guid ScanId,
    string RepositoryUrl,
    DateTimeOffset At,
    int Critical,
    int High,
    int Medium,
    int Low,
    int Info,
    int Total,
    int NewFindings,
    int FixedFindings);

/// <summary>
///     Security posture over time for a client: findings by severity per
///     completed scan, mean time to resolve, and new-vs-fixed per scan.
///     Every number comes from stored scans and findings. With fewer than
///     two completed scans there is no trend to show:
///     <see cref="HasEnoughHistory"/> is false, <see cref="Points"/> is
///     empty, and <see cref="Message"/> says so — no trend line and no
///     fake "0 criticals" posture is ever invented.
/// </summary>
public sealed record SecurityTrendsResponse(
    bool HasEnoughHistory,
    int ScanCount,
    IReadOnlyList<SecurityTrendPointResponse> Points,
    double? MeanTimeToResolveDays,
    int ResolvedFindingsCount,
    string? Message);

/// <summary>
///     One completed scan as read for trend aggregation: its stored severity
///     counts and when it finished (queued time when no finish time was recorded).
/// </summary>
public sealed record TrendScanSnapshot(
    Guid ScanId,
    string RepositoryUrl,
    DateTimeOffset At,
    int Critical,
    int High,
    int Medium,
    int Low,
    int Info);

/// <summary>
///     One stored finding as read for trend aggregation. Findings are one row
///     per fingerprint per client: <see cref="FirstSeenScanId"/> is the scan
///     that first reported it, <see cref="LastSeenScanId"/> the most recent
///     scan that still reported it, and <see cref="Status"/> is the triage
///     state (0=Open … 3=Resolved) — see
///     <see cref="SecurityFindingStatus"/>.
/// </summary>
public sealed record TrendFindingSnapshot(
    string Fingerprint,
    Guid FirstSeenScanId,
    Guid LastSeenScanId,
    DateTimeOffset FirstSeenAt,
    int Status);

/// <summary>All stored history one trends computation needs: completed scans plus findings.</summary>
public sealed record SecurityScanHistory(
    IReadOnlyList<TrendScanSnapshot> Scans,
    IReadOnlyList<TrendFindingSnapshot> Findings);
