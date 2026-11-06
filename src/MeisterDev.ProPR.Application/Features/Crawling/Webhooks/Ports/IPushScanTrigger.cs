// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>
///     What a push-triggered scan attempt decided. Every outcome is honest: a scan
///     is only ever reported as started when the scan service actually queued one.
/// </summary>
public enum PushScanTriggerOutcomeKind
{
    /// <summary>A scan was queued for the pushed commit.</summary>
    Started,

    /// <summary>This (repository, commit SHA) already has a scan; no second scan was started.</summary>
    AlreadyScanned,

    /// <summary>Push scans are switched off for this client.</summary>
    Disabled,

    /// <summary>The pushed branch is not in the client's configured branch filter.</summary>
    BranchNotConfigured,

    /// <summary>The scan could not be started (reason carries the detail); no scan ran.</summary>
    ScanStartFailed,

    /// <summary>The push carried no commit to scan (for example a branch deletion).</summary>
    IgnoredNoCommit,
}

/// <summary>One push delivery, already verified and classified, asking for a security scan.</summary>
public sealed record PushScanTriggerRequest(
    string RepositoryUrl,
    string Branch,
    string HeadSha,
    string? Pusher);

/// <summary>What the push-scan trigger decided for one push delivery.</summary>
public sealed record PushScanTriggerOutcome(
    PushScanTriggerOutcomeKind Kind,
    string Reason,
    Guid? ScanId = null);

/// <summary>
///     Starts a security scan for a verified push webhook delivery. Implemented in the
///     security-scanning module; the webhook handler only depends on this port so the
///     Application project never references the scanning module. The implementation
///     owns dedupe, the per-client toggle and branch filter, and the honest outcome.
/// </summary>
public interface IPushScanTrigger
{
    /// <summary>Decides what one push does: start a scan, or report honestly why not.</summary>
    Task<PushScanTriggerOutcome> HandlePushAsync(
        Guid clientId,
        PushScanTriggerRequest request,
        CancellationToken ct = default);
}
