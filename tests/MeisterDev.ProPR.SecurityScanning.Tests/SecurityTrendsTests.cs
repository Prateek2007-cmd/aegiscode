// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for security posture trends: the before/after story built only
///     from stored scans and findings. The service is exercised through a fake
///     history reader — no database, no HTTP — so every number in the response
///     can be traced to the history handed in.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class SecurityTrendsTests
{
    private const int Open = (int)SecurityFindingStatus.Open;
    private const int Resolved = (int)SecurityFindingStatus.Resolved;

    private static readonly Guid Scan1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Scan2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Scan3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RepoBScan = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string RepoA = "https://github.com/o/repo-a";
    private const string RepoB = "https://github.com/o/repo-b";

    private sealed class FakeHistoryReader(SecurityScanHistory history) : ISecurityScanHistoryReader
    {
        public Task<SecurityScanHistory> ReadHistoryAsync(Guid clientId, CancellationToken ct) =>
            Task.FromResult(history);
    }

    private static Task<SecurityTrendsResponse> GetTrendsAsync(SecurityScanHistory history) =>
        new SecurityTrendsService(new FakeHistoryReader(history)).GetTrendsAsync(Guid.NewGuid(), CancellationToken.None);

    private static TrendScanSnapshot Scan(Guid id, string repo, DateTimeOffset at, int critical, int high = 0, int medium = 0, int low = 0, int info = 0) =>
        new(id, repo, at, critical, high, medium, low, info);

    private static TrendFindingSnapshot Finding(string fingerprint, Guid firstSeenScan, Guid lastSeenScan, DateTimeOffset firstSeenAt, int status) =>
        new(fingerprint, firstSeenScan, lastSeenScan, firstSeenAt, status);

    // --- Not enough history ---------------------------------------------------

    [Fact]
    public async Task NoScans_ReturnsNotEnoughHistoryWithNoPoints()
    {
        var trends = await GetTrendsAsync(new SecurityScanHistory([], []));

        Assert.False(trends.HasEnoughHistory);
        Assert.Equal(0, trends.ScanCount);
        Assert.Empty(trends.Points);
        Assert.Null(trends.MeanTimeToResolveDays);
        Assert.Equal(0, trends.ResolvedFindingsCount);
        Assert.Contains("Not enough history", trends.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneScan_ReturnsNotEnoughHistoryEvenWithFindings()
    {
        var at = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [Scan(Scan1, RepoA, at, critical: 5)],
            [Finding("fp-1", Scan1, Scan1, at, Open)]);
        var trends = await GetTrendsAsync(history);

        Assert.False(trends.HasEnoughHistory);
        Assert.Equal(1, trends.ScanCount);
        Assert.Empty(trends.Points);
        Assert.Contains("2 completed scans", trends.Message, StringComparison.Ordinal);
    }

    // --- The before/after story -------------------------------------------------

    [Fact]
    public async Task FourteenCriticalsToThree_CountsNewFixedAndMttrFromStoredData()
    {
        // Scan 1 (Oct 20): 14 criticals. Scan 2 (Nov 3, exactly 14 days later):
        // 3 criticals. 11 of the original 14 disappeared in scan 2; the other 3
        // are still open. Nothing here is invented — every count is a finding row.
        var firstAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var secondAt = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var findings = new List<TrendFindingSnapshot>();
        for (var i = 0; i < 11; i++)
        {
            findings.Add(Finding($"fp-fixed-{i}", Scan1, Scan1, firstAt, Resolved));
        }

        for (var i = 0; i < 3; i++)
        {
            findings.Add(Finding($"fp-open-{i}", Scan1, Scan2, firstAt, Open));
        }

        var history = new SecurityScanHistory(
            [Scan(Scan1, RepoA, firstAt, critical: 14), Scan(Scan2, RepoA, secondAt, critical: 3)],
            findings);
        var trends = await GetTrendsAsync(history);

        Assert.True(trends.HasEnoughHistory);
        Assert.Equal(2, trends.ScanCount);
        Assert.Null(trends.Message);
        Assert.Equal(2, trends.Points.Count);

        var first = trends.Points[0];
        Assert.Equal(14, first.Critical);
        Assert.Equal(14, first.Total);
        Assert.Equal(14, first.NewFindings);
        Assert.Equal(0, first.FixedFindings);

        var second = trends.Points[1];
        Assert.Equal(3, second.Critical);
        Assert.Equal(3, second.Total);
        Assert.Equal(0, second.NewFindings);
        Assert.Equal(11, second.FixedFindings);

        Assert.Equal(11, trends.ResolvedFindingsCount);
        Assert.Equal(14.0, trends.MeanTimeToResolveDays);
    }

    [Fact]
    public async Task ThreeScans_FixedAttributedToTheScanWhereTheFindingDisappeared()
    {
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [
                Scan(Scan1, RepoA, t1, critical: 2),
                Scan(Scan2, RepoA, t2, critical: 2),
                Scan(Scan3, RepoA, t3, critical: 1),
            ],
            [
                // Seen in scans 1 and 2, gone in scan 3: fixed in scan 3, not scan 2.
                Finding("fp-a", Scan1, Scan2, t1, Resolved),
                // Still open in scan 3.
                Finding("fp-b", Scan1, Scan3, t1, Open),
                // Brand new in scan 2, still open.
                Finding("fp-c", Scan2, Scan3, t2, Open),
                // Brand new in scan 3.
                Finding("fp-d", Scan3, Scan3, t3, Open),
            ]);
        var trends = await GetTrendsAsync(history);

        Assert.Equal([2, 0, 1], trends.Points.Select(p => p.NewFindings).ToArray());
        Assert.Equal([0, 0, 1], trends.Points.Select(p => p.FixedFindings).ToArray());
        Assert.Equal(1, trends.ResolvedFindingsCount);
        // First seen Oct 1, disappeared at the Oct 15 scan: 14 days.
        Assert.Equal(14.0, trends.MeanTimeToResolveDays);
    }

    [Fact]
    public async Task OpenFindingsNeverCountTowardMttr()
    {
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [Scan(Scan1, RepoA, t1, critical: 2), Scan(Scan2, RepoA, t2, critical: 1)],
            [
                Finding("fp-fixed", Scan1, Scan1, t1, Resolved),
                // Open for 10 days and counting — not resolved, so no MTTR contribution.
                Finding("fp-open", Scan1, Scan2, t1, Open),
            ]);
        var trends = await GetTrendsAsync(history);

        Assert.Equal(1, trends.ResolvedFindingsCount);
        Assert.Equal(10.0, trends.MeanTimeToResolveDays);
        Assert.Equal(1, trends.Points[1].FixedFindings);
    }

    [Fact]
    public async Task ResolvedFindingWithNoSuccessorScan_IsLeftOutOfMttrAndFixed()
    {
        // Last seen in the newest scan yet already Resolved: the stored data
        // cannot prove which scan it disappeared in, so it must not be counted.
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [Scan(Scan1, RepoA, t1, critical: 1), Scan(Scan2, RepoA, t2, critical: 0)],
            [Finding("fp-odd", Scan1, Scan2, t1, Resolved)]);
        var trends = await GetTrendsAsync(history);

        Assert.Equal(0, trends.ResolvedFindingsCount);
        Assert.Null(trends.MeanTimeToResolveDays);
        Assert.Equal(0, trends.Points[1].FixedFindings);
    }

    [Fact]
    public async Task NoResolvedFindings_MttrIsNullNotZero()
    {
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [Scan(Scan1, RepoA, t1, critical: 1), Scan(Scan2, RepoA, t2, critical: 1)],
            [Finding("fp-open", Scan1, Scan2, t1, Open)]);
        var trends = await GetTrendsAsync(history);

        Assert.Equal(0, trends.ResolvedFindingsCount);
        Assert.Null(trends.MeanTimeToResolveDays);
        Assert.Equal(0, trends.Points[1].FixedFindings);
    }

    // --- Multi-repo attribution ---------------------------------------------------

    [Fact]
    public async Task ScanOfAnotherRepoNeverFixesThisReposFindings()
    {
        // Repo A scanned Oct 1 and Oct 15; repo B scanned Oct 8 in between.
        // The repo A finding that disappeared must be fixed in repo A's Oct 15
        // scan, not in repo B's Oct 8 scan.
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var tB = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [
                Scan(Scan1, RepoA, t1, critical: 1),
                Scan(RepoBScan, RepoB, tB, critical: 0),
                Scan(Scan2, RepoA, t2, critical: 0),
            ],
            [
                Finding("fp-a", Scan1, Scan1, t1, Resolved),
                Finding("fp-b-new", RepoBScan, RepoBScan, tB, Open),
            ]);
        var trends = await GetTrendsAsync(history);

        var byScan = trends.Points.ToDictionary(p => p.ScanId);
        Assert.Equal(0, byScan[Scan1].FixedFindings);
        Assert.Equal(0, byScan[RepoBScan].FixedFindings);
        Assert.Equal(1, byScan[Scan2].FixedFindings);
        Assert.Equal(1, byScan[RepoBScan].NewFindings);
        Assert.Equal(14.0, trends.MeanTimeToResolveDays);
    }

    // --- Severity totals come from the stored scan counts ----------------------------

    [Fact]
    public async Task PointTotalsSumAllSeveritiesFromStoredCounts()
    {
        var t1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var history = new SecurityScanHistory(
            [
                Scan(Scan1, RepoA, t1, critical: 1, high: 2, medium: 3, low: 4, info: 5),
                Scan(Scan2, RepoA, t2, critical: 0, high: 1, medium: 0, low: 0, info: 0),
            ],
            []);
        var trends = await GetTrendsAsync(history);

        Assert.Equal(15, trends.Points[0].Total);
        Assert.Equal(1, trends.Points[1].Total);
        Assert.Equal(2, trends.Points[0].High);
        Assert.Equal(3, trends.Points[0].Medium);
    }
}
