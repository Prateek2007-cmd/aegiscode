// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Request to change push scans for a client. Branches may be omitted or empty to reset to main only.</summary>
public sealed record UpdatePushScanConfigRequest(bool Enabled, IReadOnlyList<string>? Branches);

/// <summary>The push-scan configuration as served to the UI.</summary>
public sealed record PushScanConfigResponse(bool Enabled, IReadOnlyList<string> Branches);

/// <summary>One push-triggered scan attempt as served to the UI. Outcome is pending, started, or failed.</summary>
public sealed record PushScanEntryResponse(
    string RepositoryUrl,
    string Branch,
    string HeadSha,
    string? Pusher,
    string Outcome,
    string Reason,
    Guid? ScanId,
    string? ScanStatus,
    DateTimeOffset UpdatedAt);

/// <summary>
///     The push-scan state for a client: its config plus its latest push-triggered
///     scans, newest first. An empty list means no push ever started a scan for
///     this client — shown as "no push scans yet", never as a scan.
/// </summary>
public sealed record PushScanStatusResponse(
    PushScanConfigResponse Config,
    IReadOnlyList<PushScanEntryResponse> RecentScans);
