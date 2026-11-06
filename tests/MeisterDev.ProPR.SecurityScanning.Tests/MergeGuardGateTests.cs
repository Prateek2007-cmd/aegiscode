// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the merge-guard decision: blocking evidence fails, clean and fresh
///     data passes, missing/incomplete/stale data is neutral (never a pass), and the
///     threshold is respected. Pure function — no database, no network.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class MergeGuardGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    private static MergeGuardGate.ScanEvidence FreshScan(int critical = 0, int high = 0, int medium = 0, int low = 0) =>
        new(true, nameof(SecurityScanStatus.Succeeded), Now.AddHours(-1), critical, high, medium, low);

    private static MergeGuardGate.ReviewEvidence CleanReview() => new(0, 0);

    [Fact]
    public void CriticalOpenFinding_FailsAtHighThreshold()
    {
        var evaluation = MergeGuardGate.Evaluate(FreshScan(critical: 1), CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Failure, evaluation.Decision);
        Assert.Equal(1, evaluation.BlockingScanFindings);
        Assert.Equal(0, evaluation.BlockingReviewFindings);
    }

    [Fact]
    public void HighOpenFinding_FailsAtHighThreshold()
    {
        var evaluation = MergeGuardGate.Evaluate(FreshScan(high: 2), CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Failure, evaluation.Decision);
        Assert.Equal(2, evaluation.BlockingScanFindings);
    }

    [Fact]
    public void HighOpenFinding_PassesAtCriticalThreshold()
    {
        // Threshold respected: High does not block when only Critical blocks.
        var evaluation = MergeGuardGate.Evaluate(FreshScan(high: 2), CleanReview(), SecurityFindingSeverity.Critical, Now);

        Assert.Equal(MergeGuardDecision.Success, evaluation.Decision);
    }

    [Fact]
    public void MediumOpenFinding_FailsAtMediumThreshold_PassesAtHigh()
    {
        Assert.Equal(
            MergeGuardDecision.Failure,
            MergeGuardGate.Evaluate(FreshScan(medium: 1), CleanReview(), SecurityFindingSeverity.Medium, Now).Decision);
        Assert.Equal(
            MergeGuardDecision.Success,
            MergeGuardGate.Evaluate(FreshScan(medium: 1), CleanReview(), SecurityFindingSeverity.High, Now).Decision);
    }

    [Fact]
    public void CleanFreshScanAndCleanReview_Passes()
    {
        var evaluation = MergeGuardGate.Evaluate(FreshScan(), CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Success, evaluation.Decision);
        Assert.Contains("No blocking findings", evaluation.Reason);
    }

    [Fact]
    public void NoScan_IsNeutral_NeverAPass()
    {
        var evaluation = MergeGuardGate.Evaluate(
            new MergeGuardGate.ScanEvidence(false, null, null, 0, 0, 0, 0),
            CleanReview(),
            SecurityFindingSeverity.High,
            Now);

        Assert.Equal(MergeGuardDecision.Neutral, evaluation.Decision);
        Assert.Contains("No security scan exists", evaluation.Reason);
    }

    [Fact]
    public void ReviewError_FailsEvenWithNoScan()
    {
        // Real blocking evidence beats missing data: an Error fails the gate even
        // when no scan exists. Only a clean review with no scan is neutral.
        var evaluation = MergeGuardGate.Evaluate(
            new MergeGuardGate.ScanEvidence(false, null, null, 0, 0, 0, 0),
            new MergeGuardGate.ReviewEvidence(Errors: 1, Warnings: 0),
            SecurityFindingSeverity.High,
            Now);

        Assert.Equal(MergeGuardDecision.Failure, evaluation.Decision);
        Assert.Equal(1, evaluation.BlockingReviewFindings);
    }

    [Fact]
    public void FailedScan_IsNeutral()
    {
        var evaluation = MergeGuardGate.Evaluate(
            new MergeGuardGate.ScanEvidence(true, nameof(SecurityScanStatus.Failed), Now.AddHours(-1), 0, 0, 0, 0),
            CleanReview(),
            SecurityFindingSeverity.High,
            Now);

        Assert.Equal(MergeGuardDecision.Neutral, evaluation.Decision);
        Assert.Contains("did not complete successfully", evaluation.Reason);
    }

    [Fact]
    public void RunningScan_IsNeutral()
    {
        var evaluation = MergeGuardGate.Evaluate(
            new MergeGuardGate.ScanEvidence(true, nameof(SecurityScanStatus.Running), null, 0, 0, 0, 0),
            CleanReview(),
            SecurityFindingSeverity.High,
            Now);

        Assert.Equal(MergeGuardDecision.Neutral, evaluation.Decision);
    }

    [Fact]
    public void StaleScan_IsNeutral()
    {
        var stale = new MergeGuardGate.ScanEvidence(true, nameof(SecurityScanStatus.Succeeded), Now.AddDays(-8), 0, 0, 0, 0);
        var evaluation = MergeGuardGate.Evaluate(stale, CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Neutral, evaluation.Decision);
        Assert.Contains("more than 7 days ago", evaluation.Reason);
    }

    [Fact]
    public void SevenDayOldScan_StillFresh()
    {
        var almostStale = new MergeGuardGate.ScanEvidence(true, nameof(SecurityScanStatus.Succeeded), Now.AddDays(-7).AddMinutes(1), 0, 0, 0, 0);
        var evaluation = MergeGuardGate.Evaluate(almostStale, CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Success, evaluation.Decision);
    }

    [Fact]
    public void ReviewError_MapsToHigh_WarningMapsToMedium()
    {
        // Error fails at High but not at Critical; Warning fails at Medium but not at High.
        var errors = new MergeGuardGate.ReviewEvidence(Errors: 1, Warnings: 0);
        var warnings = new MergeGuardGate.ReviewEvidence(Errors: 0, Warnings: 1);

        Assert.Equal(
            MergeGuardDecision.Failure,
            MergeGuardGate.Evaluate(FreshScan(), errors, SecurityFindingSeverity.High, Now).Decision);
        Assert.Equal(
            MergeGuardDecision.Success,
            MergeGuardGate.Evaluate(FreshScan(), errors, SecurityFindingSeverity.Critical, Now).Decision);
        Assert.Equal(
            MergeGuardDecision.Failure,
            MergeGuardGate.Evaluate(FreshScan(), warnings, SecurityFindingSeverity.Medium, Now).Decision);
        Assert.Equal(
            MergeGuardDecision.Success,
            MergeGuardGate.Evaluate(FreshScan(), warnings, SecurityFindingSeverity.High, Now).Decision);
    }

    [Fact]
    public void InvalidThreshold_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MergeGuardGate.Evaluate(FreshScan(), CleanReview(), SecurityFindingSeverity.Info, Now));
    }

    [Fact]
    public void PartiallySucceededScan_CountsAsSuccessful()
    {
        var partial = new MergeGuardGate.ScanEvidence(true, nameof(SecurityScanStatus.PartiallySucceeded), Now.AddHours(-1), 0, 0, 0, 0);
        var evaluation = MergeGuardGate.Evaluate(partial, CleanReview(), SecurityFindingSeverity.High, Now);

        Assert.Equal(MergeGuardDecision.Success, evaluation.Decision);
    }
}
