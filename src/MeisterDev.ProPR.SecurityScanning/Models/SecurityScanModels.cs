// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Lifecycle of a security scan. Queued → Running → one of the terminal states.</summary>
public enum SecurityScanStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    PartiallySucceeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>Source-provided severity of a finding. AegisCode's own priority is stored separately.</summary>
public enum SecurityFindingSeverity
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
    Info = 4,
}

/// <summary>Triage state of a finding.</summary>
public enum SecurityFindingStatus
{
    Open = 0,
    Acknowledged = 1,
    Suppressed = 2,
    Resolved = 3,
}

/// <summary>Deterministic scanner that produced a finding.</summary>
public static class SecurityScanners
{
    public const string DepScan = "dep-scan";
    public const string SecretScan = "secret-scan";
    public const string PackageRisk = "package-risk";
    public const string Sbom = "sbom";

    public static readonly string[] All = [DepScan, SecretScan, PackageRisk, Sbom];
}

/// <summary>The unit of work the background worker dequeues.</summary>
public sealed record SecurityScanJob(Guid ScanId, Guid ClientId, IReadOnlyList<string> Scanners);

/// <summary>Coverage metadata: what the scan actually did — the basis for honest status.</summary>
public sealed record SecurityScanCoverage(
    IReadOnlyList<string> ManifestsFound,
    int FilesScanned,
    IReadOnlyList<string> ScannersRun,
    IReadOnlyList<string> ScannersFailed,
    IReadOnlyList<string> ScannersSkipped);

/// <summary>One normalized finding, as parsed from engine output.</summary>
public sealed record EngineFinding(
    string Scanner,
    string? RuleId,
    string Severity,
    string Confidence,
    string Title,
    string Category,
    string? FilePath,
    int? Line,
    string? Evidence,
    string? Remediation,
    string? PackageName,
    string? PackageVersion,
    string? FixedVersion);

/// <summary>Parsed result of one engine sidecar invocation.</summary>
public sealed record EngineScanResult(
    IReadOnlyList<EngineFinding> Findings,
    string? SbomJson,
    SecurityScanCoverage Coverage,
    bool IsPartial,
    string? PartialReason);
