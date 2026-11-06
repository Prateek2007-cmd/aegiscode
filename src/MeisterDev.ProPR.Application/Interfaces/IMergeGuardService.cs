// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Outcome of a merge-guard evaluation for one pull request.</summary>
public enum MergeGuardDecision
{
    /// <summary>
    ///     No blocking evidence and the scan data is fresh enough to trust: the check reports success.
    /// </summary>
    Success = 0,

    /// <summary>
    ///     Blocking evidence (a scan or review finding at or above the configured threshold):
    ///     the check reports failure.
    /// </summary>
    Failure = 1,

    /// <summary>
    ///     No blocking evidence, but the data is missing, incomplete, or stale: the check reports
    ///     neutral, never a pass. A neutral check does not block merging, and says why.
    /// </summary>
    Neutral = 2,
}

/// <summary>
///     What the merge guard decided for one pull request, and whether that decision reached the
///     provider as a check run. A report is returned even when nothing was posted, so a caller
///     can tell "guard off" apart from "posted" apart from "tried and failed" — a failed post
///     is never reported as a pass.
/// </summary>
public sealed record MergeGuardReport(
    MergeGuardDecision Decision,
    string Reason,
    int BlockingScanFindings,
    int BlockingReviewFindings,
    string? ScanStatus,
    DateTimeOffset? ScanFinishedAt,
    long? CheckRunId,
    string? CheckRunUrl,
    bool Posted,
    string? PostError);

/// <summary>
///     The merge guard: evaluates a finished review against the latest security scan for the
///     same repository and reports the outcome as a provider check run on the pull request
///     head, so branch protection can block merging on failure. Implementations are
///     best-effort by contract: evaluation or reporting failures are reported as
///     <see cref="MergeGuardDecision.Neutral" /> with the reason, never thrown past the
///     review publication that triggered them.
/// </summary>
public interface IMergeGuardService
{
    /// <summary>
    ///     Evaluates the guard for one published review and posts or updates the check run.
    ///     Returns <see langword="null" /> only when the guard is disabled for the client
    ///     (nothing is evaluated and nothing is posted); every other path returns a report.
    /// </summary>
    Task<MergeGuardReport?> EvaluateAndReportAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        CancellationToken ct = default);
}
