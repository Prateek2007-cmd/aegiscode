// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>Fingerprint stability and scoring determinism.</summary>
public sealed class SecurityScanScoringTests
{
    private static EngineFinding Finding(
        string scanner = "dep-scan",
        string? ruleId = "GHSA-test",
        string severity = "high",
        string confidence = "high",
        string? package = "lodash",
        string? version = "4.17.20",
        string? file = "package.json",
        int? line = 12) =>
        new(scanner, ruleId, severity, confidence, "title", "vulnerability",
            file, line, "evidence", "remediation", package, version, "4.17.21");

    [Fact]
    public void Fingerprint_is_deterministic()
    {
        var a = SecurityScanFingerprint.Compute(Finding());
        var b = SecurityScanFingerprint.Compute(Finding());
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex
    }

    [Fact]
    public void Fingerprint_changes_with_identity_fields()
    {
        var base_ = SecurityScanFingerprint.Compute(Finding());
        Assert.NotEqual(base_, SecurityScanFingerprint.Compute(Finding(ruleId: "GHSA-other")));
        Assert.NotEqual(base_, SecurityScanFingerprint.Compute(Finding(version: "4.17.19")));
        Assert.NotEqual(base_, SecurityScanFingerprint.Compute(Finding(line: 13)));
    }

    [Fact]
    public void Fingerprint_ignores_volatile_evidence()
    {
        var a = Finding();
        var b = a with { Evidence = "different evidence text", Title = "different title" };
        Assert.Equal(SecurityScanFingerprint.Compute(a), SecurityScanFingerprint.Compute(b));
    }

    [Fact]
    public void Scoring_rewards_fixed_version_and_secret_exposure()
    {
        var (withFix, _) = SecurityScanPrioritizer.Score(Finding(severity: "high"));
        var (withoutFix, gaps) = SecurityScanPrioritizer.Score(Finding(severity: "high") with { FixedVersion = null });

        Assert.True(withFix > withoutFix);
        Assert.Contains("no fixed version published", gaps);

        var (secretHigh, _) = SecurityScanPrioritizer.Score(
            Finding(scanner: "secret-scan", severity: "medium", confidence: "high") with { FixedVersion = null });
        Assert.True(secretHigh >= 70);

        var (lowConf, _) = SecurityScanPrioritizer.Score(Finding(confidence: "low"));
        var (highConf, _) = SecurityScanPrioritizer.Score(Finding(confidence: "high"));
        Assert.True(lowConf < highConf);
    }

    [Fact]
    public void Scoring_always_names_missing_inputs()
    {
        var (_, gaps) = SecurityScanPrioritizer.Score(Finding());
        Assert.Contains("no reachability evidence", gaps);
        Assert.Contains("no exploit-maturity data", gaps);
    }

    [Fact]
    public void Scoring_is_bounded()
    {
        var (score, _) = SecurityScanPrioritizer.Score(
            Finding(scanner: "secret-scan", severity: "critical", confidence: "high"));
        Assert.InRange(score, 0, 100);
    }
}
