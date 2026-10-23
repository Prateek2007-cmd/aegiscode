// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One normalized deterministic security finding. The fingerprint
///     (<c>ClientId + Fingerprint</c> unique) drives dedup and regression detection
///     across scans: a finding seen again keeps its identity; a missing one resolves.
///     Secret values are never stored — evidence is redacted at the engine boundary
///     and re-verified before persistence.
/// </summary>
public sealed class SecurityFindingRecord
{
    /// <summary>Unique identifier for this finding row.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client, which scopes the record. Must not be <see cref="Guid.Empty" />.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Scan that produced this row.</summary>
    public Guid ScanId { get; init; }

    /// <summary>
    ///     Stable identity across scans: SHA-256 over scanner, rule id, package@version,
    ///     and redacted file:line. Unique per client.
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Deterministic scanner: <c>dep-scan</c>, <c>secret-scan</c>, <c>package-risk</c>, <c>sbom</c>.</summary>
    public string Scanner { get; init; } = string.Empty;

    /// <summary>Rule identifier: OSV/GHSA id, gitleaks rule id, or engine rule name.</summary>
    public string? RuleId { get; init; }

    /// <summary>Source-provided severity (0=Critical … 4=Info). AegisCode priority is separate.</summary>
    public int Severity { get; set; }

    /// <summary>Deterministic 0–100 priority computed from evidence. See the integration doc.</summary>
    public int PriorityScore { get; set; }

    /// <summary>JSON array naming every scoring input that was missing.</summary>
    public string? ScoreGapsJson { get; set; }

    /// <summary>Short human-readable title. Required.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Finding category (e.g. <c>vulnerability</c>, <c>secret</c>, <c>package-risk</c>).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Repository-relative file path, if the finding is file-scoped.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based line number, if known.</summary>
    public int? Line { get; init; }

    /// <summary>Safe-to-display evidence. Redacted; never contains raw secret values.</summary>
    public string? Evidence { get; set; }

    /// <summary>Remediation guidance. Deterministic templates unless marked AI-generated.</summary>
    public string? Remediation { get; set; }

    /// <summary>Scanner confidence: <c>high</c>, <c>medium</c>, or <c>low</c>.</summary>
    public string Confidence { get; init; } = "medium";

    /// <summary>Triage state (0=Open … 3=Resolved).</summary>
    public int Status { get; set; }

    /// <summary>First scan that reported this fingerprint.</summary>
    public Guid FirstSeenScanId { get; init; }

    /// <summary>Most recent scan that reported this fingerprint.</summary>
    public Guid LastSeenScanId { get; set; }

    /// <summary>When this row was first written.</summary>
    public DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>When this fingerprint was last observed.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>JSON array of <c>{ reason, byUserId, at }</c> suppression entries. Audit trail.</summary>
    public string? SuppressionsJson { get; set; }

    /// <summary>Navigation to the producing scan.</summary>
    public SecurityScanRecord? Scan { get; init; }
}
