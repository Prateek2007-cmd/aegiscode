// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Triage state of a radar alert.</summary>
public enum RadarAlertStatus
{
    New = 0,
    Acknowledged = 1,
    Resolved = 2,
}

/// <summary>
///     One radar alert as persisted in the per-client JSON store. An alert is one
///     (vulnerability, package, version) triple: the same CVE on a different
///     package version is a different alert, because the fix (upgrade target)
///     differs. Kept separate from the response DTO so storage can evolve
///     without changing the API contract.
/// </summary>
public sealed record RadarAlertRecord(
    Guid Id,
    Guid ClientId,
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
    RadarAlertStatus Status,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Summary of one radar pass, stored on the per-client radar state.</summary>
public sealed record RadarPassSummaryRecord(
    DateTimeOffset CheckedAt,
    int ReposChecked,
    int ComponentsChecked,
    int ComponentsSkipped,
    int NewOccurrences,
    int NewAlerts,
    IReadOnlyList<string> Errors);

/// <summary>
///     Per-client radar state as persisted in the per-client JSON store:
///     the watch settings, the last pass summary, and the "seen" ledger.
///     The ledger holds one key per (vulnerability, package, version) the radar
///     has ever reported — <c>{vulnId}|{package}@{version}</c> — plus coarse
///     <c>{vulnId}</c> keys seeded from past scan findings. A vulnerability is
///     new only when neither its exact key nor its coarse key is in the ledger.
/// </summary>
public sealed record RadarStateRecord(
    Guid ClientId,
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? LastPassAt,
    RadarPassSummaryRecord? LastPass,
    IReadOnlyList<string> SeenKeys);
