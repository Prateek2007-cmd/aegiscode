// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One deterministic security scan of a repository: its lifecycle, coverage, and summary.
///     Findings live in <see cref="SecurityFindingRecord" /> keyed by scan.
///     Rows are never deleted by the scan pipeline; retention is a client policy concern.
/// </summary>
public sealed class SecurityScanRecord
{
    /// <summary>Unique identifier for this scan.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client, which scopes the record. Must not be <see cref="Guid.Empty" />.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Validated repository URL the scan ran against. Required, at most 512 characters.</summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>Branch, tag, or commit the scan targeted, if the caller specified one.</summary>
    public string? Ref { get; init; }

    /// <summary>Scan lifecycle state.</summary>
    public int Status { get; set; }

    /// <summary>0–100 progress hint for the UI. Best-effort; the worker updates it at stage boundaries.</summary>
    public int ProgressPct { get; set; }

    /// <summary>When the scan was queued.</summary>
    public DateTimeOffset QueuedAt { get; init; }

    /// <summary>When the worker started executing.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the scan reached a terminal state.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Human-readable error for failed scans. Never contains secrets or stack traces.</summary>
    public string? Error { get; set; }

    /// <summary>Engine version string reported by the sidecar, for reproducibility.</summary>
    public string? EngineVersion { get; set; }

    /// <summary>JSON: per-scanner versions / rule-set revisions reported by the engine.</summary>
    public string? ScannerVersionsJson { get; set; }

    /// <summary>JSON: <c>SecurityScanCoverage</c> — manifests found, files scanned, scanners run/failed/skipped.</summary>
    public string? CoverageJson { get; set; }

    /// <summary>Relative path of the stored CycloneDX SBOM artifact, if one was produced.</summary>
    public string? SbomPath { get; set; }

    /// <summary>Severity counts, denormalized for cheap history queries.</summary>
    public int CriticalCount { get; set; }

    /// <summary>Severity counts, denormalized for cheap history queries.</summary>
    public int HighCount { get; set; }

    /// <summary>Severity counts, denormalized for cheap history queries.</summary>
    public int MediumCount { get; set; }

    /// <summary>Severity counts, denormalized for cheap history queries.</summary>
    public int LowCount { get; set; }

    /// <summary>Severity counts, denormalized for cheap history queries.</summary>
    public int InfoCount { get; set; }

    /// <summary>User that initiated the scan, if known.</summary>
    public Guid? CreatedByUserId { get; init; }
}
