// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the merge-guard service flow: disabled returns null without posting,
///     decisions post the matching conclusion, the same head reuses the check run
///     (update instead of a second check), a new head posts a new one, a poster
///     failure is reported honestly instead of thrown, and config validation.
///     Tests run against a fresh temp directory per test — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class MergeGuardServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-merge-guard-service-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempRoot))
            {
                Directory.Delete(this.tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            /* best-effort cleanup */
        }
        catch (UnauthorizedAccessException)
        {
            /* best-effort cleanup */
        }
    }

    private sealed class FakeScanService : ISecurityScanService
    {
        public SecurityScanResponse? LatestScan { get; set; }
        public Dictionary<string, int> OpenCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<StartSecurityScanResponse> StartScanAsync(Guid clientId, StartSecurityScanRequest request, Guid? userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<SecurityScanResponse?> GetScanAsync(Guid clientId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();

        public Task<SecurityScanResponse?> GetLatestScanForRepositoryAsync(Guid clientId, string repositoryUrl, CancellationToken ct) =>
            Task.FromResult(this.LatestScan);

        public Task<IReadOnlyList<SecurityScanResponse>> ListScansAsync(Guid clientId, int limit, int offset, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> CancelScanAsync(Guid clientId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();

        public Task<PagedSecurityFindingsResponse> GetFindingsAsync(
            Guid clientId, Guid scanId, string? severity, string? scanner, string? status,
            int limit, int offset, CancellationToken ct)
        {
            var total = severity is not null
                        && string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase)
                        && this.OpenCounts.TryGetValue(severity, out var count)
                ? count
                : 0;
            return Task.FromResult(new PagedSecurityFindingsResponse([], total));
        }

        public Task<string?> GetSbomJsonAsync(Guid clientId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();

        public Task<bool> SuppressFindingAsync(Guid clientId, Guid findingId, string reason, Guid? userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> AcknowledgeFindingAsync(Guid clientId, Guid findingId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed record PosterCall(
        string HeadSha,
        MergeGuardDecision Decision,
        long? ExistingCheckRunId);

    private sealed class FakePoster : IScmCheckRunPoster
    {
        public List<PosterCall> Calls { get; } = [];
        public bool ThrowOnPost { get; set; }

        public Task<CheckRunPostResult> PostOrUpdateCheckRunAsync(
            Guid clientId, ProviderHostRef host, string owner, string repository, string headSha,
            string name, MergeGuardDecision decision, string title, string summary, string? detailsUrl,
            long? existingCheckRunId, CancellationToken ct = default)
        {
            if (this.ThrowOnPost)
            {
                throw new InvalidOperationException("GitHub refused the check run (403).");
            }

            this.Calls.Add(new PosterCall(headSha, decision, existingCheckRunId));
            // An update returns the same id GitHub already issued for that head.
            var id = existingCheckRunId ?? 1000 + this.Calls.Count;
            return Task.FromResult(new CheckRunPostResult(id, $"https://github.com/acme/web/check-runs/{id}"));
        }
    }

    private static (CodeReviewRef Review, ReviewRevision Revision, ReviewResult Result) Review(
        string headSha = "abc1234567890abcdef",
        params ReviewComment[] comments)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "123", "acme", "acme/web", "web");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "pr-7", 7);
        var revision = new ReviewRevision(headSha, "def0000000000", null, null, null);
        return (review, revision, new ReviewResult("Looks fine.", comments));
    }

    private static SecurityScanResponse Scan(
        Guid clientId,
        string status = "Succeeded",
        DateTimeOffset? finishedAt = null,
        int critical = 0,
        int high = 0,
        int medium = 0,
        int low = 0) =>
        new(
            Guid.NewGuid(), clientId, "https://github.com/acme/web", null, status, 100,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(-2),
            finishedAt ?? DateTimeOffset.UtcNow.AddHours(-1), null, null,
            new SecurityScanSummaryResponse(critical, high, medium, low, 0));

    private MergeGuardService CreateService(FakeScanService scans, FakePoster poster)
    {
        var store = new JsonFileMergeGuardStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileMergeGuardStore>.Instance);
        return new MergeGuardService(
            scans,
            poster,
            store,
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<MergeGuardService>.Instance,
            Options.Create(new PublicApplicationOptions { UiOrigin = null }));
    }

    private async Task EnableGuardAsync(Guid clientId, string threshold = "High")
    {
        var store = new JsonFileMergeGuardStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileMergeGuardStore>.Instance);
        await store.SaveConfigAsync(clientId, new MergeGuardConfigRecord(true, threshold), CancellationToken.None);
    }

    [Fact]
    public async Task DisabledGuard_ReturnsNull_AndPostsNothing()
    {
        var scans = new FakeScanService();
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        var (review, revision, result) = Review();

        var report = await service.EvaluateAndReportAsync(Guid.NewGuid(), review, revision, result, CancellationToken.None);

        Assert.Null(report);
        Assert.Empty(poster.Calls);
    }

    [Fact]
    public async Task NoScan_PostsNeutral()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = null };
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);
        var (review, revision, result) = Review();

        var report = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(MergeGuardDecision.Neutral, report.Decision);
        Assert.True(report.Posted);
        var call = Assert.Single(poster.Calls);
        Assert.Equal(MergeGuardDecision.Neutral, call.Decision);
        Assert.Contains("No security scan exists", report.Reason);
    }

    [Fact]
    public async Task CriticalOpenFinding_PostsFailure()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = Scan(clientId, critical: 1) };
        scans.OpenCounts["Critical"] = 1;
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);
        var (review, revision, result) = Review();

        var report = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(MergeGuardDecision.Failure, report.Decision);
        Assert.Equal(1, report.BlockingScanFindings);
        Assert.True(report.Posted);
        Assert.Equal(MergeGuardDecision.Failure, Assert.Single(poster.Calls).Decision);
    }

    [Fact]
    public async Task CleanFreshScan_PostsSuccess()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = Scan(clientId) };
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);
        var (review, revision, result) = Review();

        var report = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(MergeGuardDecision.Success, report.Decision);
        Assert.True(report.Posted);
        Assert.Equal(MergeGuardDecision.Success, Assert.Single(poster.Calls).Decision);
    }

    [Fact]
    public async Task SameHead_ReusesCheckRunId_NewHead_PostsNew()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = Scan(clientId) };
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);

        var (review, revision, result) = Review(headSha: "abc1234567890abcdef");
        var first = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);
        var second = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, poster.Calls.Count);
        Assert.Null(poster.Calls[0].ExistingCheckRunId);
        Assert.Equal(first.CheckRunId, poster.Calls[1].ExistingCheckRunId);
        Assert.Equal(first.CheckRunId, second.CheckRunId);

        var (review2, revision2, result2) = Review(headSha: "fff9999999999fffff");
        var third = await service.EvaluateAndReportAsync(clientId, review2, revision2, result2, CancellationToken.None);

        Assert.NotNull(third);
        Assert.Equal(3, poster.Calls.Count);
        Assert.Null(poster.Calls[2].ExistingCheckRunId);
        Assert.NotEqual(first.CheckRunId, third.CheckRunId);
    }

    [Fact]
    public async Task PosterFailure_ReportedHonestly_NotThrown()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = Scan(clientId, critical: 1) };
        scans.OpenCounts["Critical"] = 1;
        var poster = new FakePoster { ThrowOnPost = true };
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);
        var (review, revision, result) = Review();

        var report = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(MergeGuardDecision.Failure, report.Decision);
        Assert.False(report.Posted);
        Assert.Contains("403", report.PostError);
    }

    [Fact]
    public async Task ReviewError_Blocks_WhenScanIsClean()
    {
        var clientId = Guid.NewGuid();
        var scans = new FakeScanService { LatestScan = Scan(clientId) };
        var poster = new FakePoster();
        var service = this.CreateService(scans, poster);
        await this.EnableGuardAsync(clientId);
        var (review, revision, result) = Review(comments: [new ReviewComment("a.cs", 10, CommentSeverity.Error, "Null dereference.")]);

        var report = await service.EvaluateAndReportAsync(clientId, review, revision, result, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(MergeGuardDecision.Failure, report.Decision);
        Assert.Equal(1, report.BlockingReviewFindings);
    }

    [Fact]
    public async Task UpdateConfig_InvalidThreshold_Throws()
    {
        var service = this.CreateService(new FakeScanService(), new FakePoster());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateConfigAsync(Guid.NewGuid(), true, "Banana", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateConfig_Valid_Persists_AndGetStatusReflectsIt()
    {
        var service = this.CreateService(new FakeScanService(), new FakePoster());
        var clientId = Guid.NewGuid();

        var saved = await service.UpdateConfigAsync(clientId, true, "Medium", CancellationToken.None);
        var status = await service.GetStatusAsync(clientId, CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.Equal("Medium", saved.Threshold);
        Assert.True(status.Config.Enabled);
        Assert.Equal("Medium", status.Config.Threshold);
        Assert.Empty(status.Checks);
    }
}
