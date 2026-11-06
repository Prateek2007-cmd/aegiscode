// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>One radar alert as served to the UI. Severities use the same capitalized words as scan findings.</summary>
public sealed record RadarAlertResponse(
    Guid Id,
    Guid ScanId,
    string RepositoryUrl,
    string PackageName,
    string PackageVersion,
    string Ecosystem,
    string VulnId,
    string? CveId,
    string Severity,
    string? Summary,
    string? FixedVersion,
    string OsvUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string Status,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Alerts envelope: newest first, possibly empty — never a fake count.</summary>
public sealed record RadarAlertListResponse(IReadOnlyList<RadarAlertResponse> Items);

/// <summary>Per-client radar watch settings.</summary>
public sealed record RadarSettingsResponse(
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? LastPassAt);

/// <summary>Request to change the per-client radar watch settings. Null fields keep their current value.</summary>
public sealed record UpdateRadarSettingsRequest(
    bool? Enabled,
    int? IntervalMinutes);

/// <summary>One repository's share of a radar pass.</summary>
public sealed record RadarPassRepoResponse(
    string RepositoryUrl,
    Guid? ScanId,
    int ComponentsChecked,
    int ComponentsSkipped,
    int NewOccurrences,
    int NewAlerts,
    int DetailsSkipped,
    string? Error);

/// <summary>
///     Result of one radar pass. <c>NothingNew</c> is true only when at least one
///     repository was checked and no new vulnerability of any severity appeared.
///     When nothing could be checked, <c>NothingNew</c> is false and
///     <c>Message</c> explains why — an empty pass never pretends to be a clean bill of health.
/// </summary>
public sealed record RadarPassResponse(
    DateTimeOffset CheckedAt,
    int ReposChecked,
    int ComponentsChecked,
    int ComponentsSkipped,
    int NewOccurrences,
    int NewAlerts,
    bool NothingNew,
    IReadOnlyList<RadarPassRepoResponse> Repos,
    string? Message);
