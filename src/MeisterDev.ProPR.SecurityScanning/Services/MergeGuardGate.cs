// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     The merge-guard decision, as a pure function of evidence: open scan findings by
///     severity, review findings by severity, and the freshness of the latest scan. No
///     I/O, no GitHub calls — every branch is unit-testable, and every outcome names its
///     reason instead of hiding behind a bare pass/fail.
///     <para>
///         Severity mapping: scan severities use their own scale (Critical 0 … Low 3). Review
///         severities map onto the same scale — Error counts as High, Warning counts as
///         Medium; Suggestion and Info never block a merge, at any threshold. A finding
///         blocks when its severity is at least as severe as the configured threshold.
///         Only <em>open</em> scan findings count: acknowledged, suppressed, and resolved
///         findings are a human's explicit decision and never block. Review findings are
///         the ones just published, so they are unresolved by construction.
///     </para>
///     <para>
///         Precedence is deliberate: real blocking evidence beats missing data (a review
///         Error fails the gate even when no scan exists), and missing or stale data beats
///         a pass (no scan, an incomplete scan, or a scan older than
///         <see cref="StaleAfter" /> is neutral, never a pass).
///     </para>
/// </summary>
public static class MergeGuardGate
{
    /// <summary>A scan older than this no longer reflects the pull request: neutral, not a pass.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>What the latest scan for the repository actually says. Counts are open findings only.</summary>
    public sealed record ScanEvidence(
        bool HasScan,
        string? ScanStatus,
        DateTimeOffset? ScanFinishedAt,
        int Critical,
        int High,
        int Medium,
        int Low);

    /// <summary>What the just-published review says. Only Error and Warning participate in the gate.</summary>
    public sealed record ReviewEvidence(int Errors, int Warnings);

    /// <summary>The decision, its human-readable reason, and the blocking counts behind it.</summary>
    public sealed record Evaluation(
        MergeGuardDecision Decision,
        string Reason,
        int BlockingScanFindings,
        int BlockingReviewFindings);

    /// <summary>Evaluates the gate. <paramref name="threshold" /> must be Critical, High, Medium, or Low.</summary>
    public static Evaluation Evaluate(
        ScanEvidence scan,
        ReviewEvidence review,
        SecurityFindingSeverity threshold,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(review);
        if (threshold is not SecurityFindingSeverity.Critical
            and not SecurityFindingSeverity.High
            and not SecurityFindingSeverity.Medium
            and not SecurityFindingSeverity.Low)
        {
            throw new ArgumentException("Threshold must be Critical, High, Medium, or Low.", nameof(threshold));
        }

        var thresholdRank = (int)threshold;
        var blockingScan = CountAtOrAboveThreshold(scan, thresholdRank);
        var blockingReview = CountReviewAtOrAboveThreshold(review, thresholdRank);
        var blockingTotal = blockingScan + blockingReview;

        if (blockingTotal > 0)
        {
            return new Evaluation(
                MergeGuardDecision.Failure,
                BuildFailureReason(scan, review, blockingScan, blockingReview),
                blockingScan,
                blockingReview);
        }

        if (!scan.HasScan)
        {
            return new Evaluation(
                MergeGuardDecision.Neutral,
                "No security scan exists for this repository yet, so the guard cannot confirm it is safe to merge.",
                0,
                0);
        }

        if (scan.ScanStatus is not nameof(SecurityScanStatus.Succeeded)
            and not nameof(SecurityScanStatus.PartiallySucceeded))
        {
            var status = string.IsNullOrWhiteSpace(scan.ScanStatus) ? "unknown" : scan.ScanStatus;
            return new Evaluation(
                MergeGuardDecision.Neutral,
                $"The latest security scan did not complete successfully (status: {status}), so the guard cannot confirm it is safe to merge.",
                0,
                0);
        }

        if (scan.ScanFinishedAt is null || now - scan.ScanFinishedAt.Value > StaleAfter)
        {
            var finished = scan.ScanFinishedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "unknown";
            return new Evaluation(
                MergeGuardDecision.Neutral,
                $"The latest security scan finished on {finished} UTC, more than 7 days ago, so it no longer reflects this pull request.",
                0,
                0);
        }

        return new Evaluation(
            MergeGuardDecision.Success,
            BuildSuccessReason(scan, review),
            0,
            0);
    }

    private static int CountAtOrAboveThreshold(ScanEvidence scan, int thresholdRank)
    {
        // Rank 0 is most severe: a finding blocks when its rank is at or below the threshold rank.
        var count = 0;
        if (0 <= thresholdRank)
        {
            count += scan.Critical;
        }

        if (1 <= thresholdRank)
        {
            count += scan.High;
        }

        if (2 <= thresholdRank)
        {
            count += scan.Medium;
        }

        if (3 <= thresholdRank)
        {
            count += scan.Low;
        }

        return count;
    }

    private static int CountReviewAtOrAboveThreshold(ReviewEvidence review, int thresholdRank)
    {
        // Error maps to High (rank 1), Warning maps to Medium (rank 2). Suggestion and
        // Info never block, at any threshold.
        var count = 0;
        if (1 <= thresholdRank)
        {
            count += review.Errors;
        }

        if (2 <= thresholdRank)
        {
            count += review.Warnings;
        }

        return count;
    }

    private static string BuildFailureReason(
        ScanEvidence scan,
        ReviewEvidence review,
        int blockingScan,
        int blockingReview)
    {
        var parts = new List<string>();
        if (blockingScan > 0)
        {
            parts.Add(
                $"{blockingScan} blocking open scan finding(s)" +
                $" ({scan.Critical} critical, {scan.High} high, {scan.Medium} medium, {scan.Low} low open)");
        }

        if (blockingReview > 0)
        {
            parts.Add($"{blockingReview} blocking review finding(s) ({review.Errors} error(s), {review.Warnings} warning(s))");
        }

        return string.Join("; ", parts) + ".";
    }

    private static string BuildSuccessReason(ScanEvidence scan, ReviewEvidence review)
    {
        var finished = scan.ScanFinishedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "unknown";
        return $"No blocking findings. Latest scan finished {finished} UTC " +
               $"({scan.Critical} critical, {scan.High} high, {scan.Medium} medium, {scan.Low} low open); " +
               $"review raised {review.Errors} error(s) and {review.Warnings} warning(s).";
    }
}
