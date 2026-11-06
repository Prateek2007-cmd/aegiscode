// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Request to change the merge guard for a client. Threshold must be Critical, High, Medium, or Low.</summary>
public sealed record UpdateMergeGuardConfigRequest(bool Enabled, string Threshold);

/// <summary>The merge-guard configuration as served to the UI.</summary>
public sealed record MergeGuardConfigResponse(bool Enabled, string Threshold);

/// <summary>One reported check as served to the UI. Conclusion is success, failure, or neutral.</summary>
public sealed record MergeGuardCheckResponse(
    string RepositoryUrl,
    int PullRequestNumber,
    string HeadSha,
    long? CheckRunId,
    string? CheckRunUrl,
    string Conclusion,
    string Title,
    string Reason,
    int BlockingScanFindings,
    int BlockingReviewFindings,
    DateTimeOffset UpdatedAt,
    bool Posted,
    string? PostError);

/// <summary>
///     The guard's current state for a client: its config plus its latest checks, newest
///     first. Empty checks means the guard never reported for this client — shown as
///     "no checks yet", never as a pass.
/// </summary>
public sealed record MergeGuardStatusResponse(
    MergeGuardConfigResponse Config,
    IReadOnlyList<MergeGuardCheckResponse> Checks);
