// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     Per-client push-scan configuration as persisted in the JSON store. Default:
///     enabled, <c>main</c> branch only. Branches are glob patterns (<c>*</c> and
///     <c>?</c>, same as the webhook repo filters); <c>refs/heads/</c> prefixes are
///     stripped on save. Kept separate from the response DTO so storage can evolve
///     without changing the API contract.
/// </summary>
public sealed record PushScanConfigRecord(bool Enabled, IReadOnlyList<string> Branches);

/// <summary>
///     One push-triggered scan attempt, as persisted in the JSON store. The commit
///     SHA and branch are stored here — the scan record itself only carries the
///     branch as its ref — so the dashboard can show "triggered by push &lt;sha&gt;
///     on &lt;branch&gt;" without another GitHub read. Outcome is
///     <c>pending</c> while the scan is being queued, <c>started</c> once the scan
///     service actually queued it, or <c>failed</c> with the reason when the scan
///     could not start. A failed attempt never blocks a later retry of the same
///     commit; only a pending or started attempt dedupes.
/// </summary>
public sealed record PushScanEntryRecord(
    string RepositoryUrl,
    string Branch,
    string HeadSha,
    string? Pusher,
    string Outcome,
    string Reason,
    Guid? ScanId,
    string? ScanStatus,
    DateTimeOffset UpdatedAt);

/// <summary>One client's whole push-scan file: its config plus its latest scan attempts, newest first.</summary>
public sealed record PushScanStateRecord(
    PushScanConfigRecord Config,
    IReadOnlyList<PushScanEntryRecord> Entries);
