// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Request to start a security scan of a repository.</summary>
public sealed record StartSecurityScanRequest(
    string RepositoryUrl,
    string? Ref = null,
    IReadOnlyList<string>? Scanners = null);

/// <summary>Response to a scan initiation: 202 Accepted with the scan identity.</summary>
public sealed record StartSecurityScanResponse(Guid ScanId, string Status);

/// <summary>Severity counts for one scan.</summary>
public sealed record SecurityScanSummaryResponse(
    int Critical,
    int High,
    int Medium,
    int Low,
    int Info);

/// <summary>Full scan metadata response.</summary>
public sealed record SecurityScanResponse(
    Guid ScanId,
    Guid ClientId,
    string RepositoryUrl,
    string? Ref,
    string Status,
    int ProgressPct,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    SecurityScanCoverage? Coverage,
    SecurityScanSummaryResponse Summary);

/// <summary>One finding as served to the UI. Secret values are always redacted.</summary>
public sealed record SecurityFindingResponse(
    Guid Id,
    string Scanner,
    string? RuleId,
    string Severity,
    int PriorityScore,
    IReadOnlyList<string> ScoreGaps,
    string Title,
    string Category,
    string? FilePath,
    int? Line,
    string? Evidence,
    string? Remediation,
    string Confidence,
    string Status,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>Paginated findings envelope.</summary>
public sealed record PagedSecurityFindingsResponse(
    IReadOnlyList<SecurityFindingResponse> Items,
    int TotalCount);

/// <summary>Request to suppress a finding. The reason is mandatory and audited.</summary>
public sealed record SuppressFindingRequest(string Reason);
