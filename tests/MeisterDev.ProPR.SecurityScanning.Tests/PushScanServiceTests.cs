// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for push-triggered scans: the default (enabled, main only) starts a
///     scan, the same commit SHA pushed twice starts at most one scan (including
///     across branches), a disabled client and an unconfigured branch start nothing,
///     branch globs match, a failed start is reported honestly and can be retried,
///     and a branch deletion (all-zero SHA) is ignored. Tests run against a fresh
///     temp directory per test — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class PushScanServiceTests : IDisposable
{
    private const string Repo = "https://github.com/acme/web";
    private const string Sha = "abc1234567890abcdef1234567890abcdef12345678";
    private const string OtherSha = "def1234567890abcdef1234567890abcdef12345678";

    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-push-scan-service-tests", Guid.NewGuid().ToString("N"));

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
        public List<StartSecurityScanRequest> Started { get; } = [];
        public bool ThrowOnStart { get; set; }
        public string LiveStatus { get; set; } = "Succeeded";

        public Task<StartSecurityScanResponse> StartScanAsync(Guid clientId, StartSecurityScanRequest request, Guid? userId, CancellationToken ct)
        {
            if (this.ThrowOnStart)
            {
                throw new InvalidOperationException("Security scanning is disabled on this installation.");
            }

            this.Started.Add(request);
            return Task.FromResult(new StartSecurityScanResponse(Guid.NewGuid(), nameof(SecurityScanStatus.Queued)));
        }

        public Task<SecurityScanResponse?> GetScanAsync(Guid clientId, Guid scanId, CancellationToken ct)
        {
            return Task.FromResult<SecurityScanResponse?>(
                new SecurityScanResponse(
                    scanId, clientId, Repo, "main", this.LiveStatus, 100,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
                    new SecurityScanSummaryResponse(0, 0, 0, 0, 0)));
        }

        public Task<SecurityScanResponse?> GetLatestScanForRepositoryAsync(Guid clientId, string repositoryUrl, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SecurityScanResponse>> ListScansAsync(Guid clientId, int limit, int offset, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> CancelScanAsync(Guid clientId, Guid scanId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<PagedSecurityFindingsResponse> GetFindingsAsync(
            Guid clientId, Guid scanId, string? severity, string? scanner, string? status,
            int limit, int offset, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetSbomJsonAsync(Guid clientId, Guid scanId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> SuppressFindingAsync(Guid clientId, Guid findingId, string reason, Guid? userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> AcknowledgeFindingAsync(Guid clientId, Guid findingId, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private PushScanService CreateService(FakeScanService fake)
    {
        var store = new JsonFilePushScanStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFilePushScanStore>.Instance);
        return new PushScanService(
            fake,
            store,
            Options.Create(new SecurityScanningOptions()),
            NullLogger<PushScanService>.Instance);
    }

    private static PushScanTriggerRequest Push(string branch = "main", string sha = Sha, string repo = Repo) =>
        new(repo, branch, sha, "prateek");

    [Fact]
    public async Task DefaultConfig_StartsScanOnMain()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var outcome = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Started, outcome.Kind);
        Assert.NotNull(outcome.ScanId);
        var started = Assert.Single(fake.Started);
        Assert.Equal(Repo, started.RepositoryUrl);
        Assert.Equal("main", started.Ref);
    }

    [Fact]
    public async Task SameCommitTwice_StartsAtMostOneScan()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var first = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);
        var second = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Started, first.Kind);
        Assert.Equal(PushScanTriggerOutcomeKind.AlreadyScanned, second.Kind);
        Assert.Single(fake.Started);
    }

    [Fact]
    public async Task SameCommitOnAnotherBranch_IsStillADuplicate()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();
        await service.UpdateConfigAsync(clientId, true, ["main", "release/*"], CancellationToken.None);

        var first = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);
        var second = await service.HandlePushAsync(clientId, Push("release/1.2"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Started, first.Kind);
        Assert.Equal(PushScanTriggerOutcomeKind.AlreadyScanned, second.Kind);
        Assert.Single(fake.Started);
    }

    [Fact]
    public async Task DisabledConfig_StartsNothing()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();
        await service.UpdateConfigAsync(clientId, false, ["main"], CancellationToken.None);

        var outcome = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Disabled, outcome.Kind);
        Assert.Empty(fake.Started);
    }

    [Fact]
    public async Task BranchNotConfigured_StartsNothing()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var outcome = await service.HandlePushAsync(clientId, Push("feature/new-thing"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.BranchNotConfigured, outcome.Kind);
        Assert.Empty(fake.Started);
    }

    [Fact]
    public async Task BranchGlob_ReleaseBranchStarts()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();
        await service.UpdateConfigAsync(clientId, true, ["main", "release/*"], CancellationToken.None);

        var outcome = await service.HandlePushAsync(clientId, Push("release/1.2"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Started, outcome.Kind);
        Assert.Single(fake.Started);
    }

    [Fact]
    public async Task ScanStartThrows_ReportsFailureHonestlyAndAllowsRetry()
    {
        var fake = new FakeScanService { ThrowOnStart = true };
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var failed = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.ScanStartFailed, failed.Kind);
        Assert.Null(failed.ScanId);
        Assert.Empty(fake.Started);

        fake.ThrowOnStart = false;
        var retry = await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.Started, retry.Kind);
        Assert.Single(fake.Started);
    }

    [Fact]
    public async Task AllZeroSha_IsIgnoredAsNoCommit()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var outcome = await service.HandlePushAsync(
            clientId,
            Push("main", new string('0', 40)),
            CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.IgnoredNoCommit, outcome.Kind);
        Assert.Empty(fake.Started);
    }

    [Fact]
    public async Task DisallowedHost_ReportsScanStartFailed()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var outcome = await service.HandlePushAsync(
            clientId,
            Push("main", Sha, "https://evil.example.com/acme/web"),
            CancellationToken.None);

        Assert.Equal(PushScanTriggerOutcomeKind.ScanStartFailed, outcome.Kind);
        Assert.Empty(fake.Started);
    }

    [Fact]
    public async Task UpdateConfig_InvalidBranches_Throws()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateConfigAsync(clientId, true, ["my branch"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateConfigAsync(
            clientId,
            true,
            Enumerable.Range(0, 21).Select(i => $"branch-{i}").ToList(),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateConfig_EmptyBranches_ResetsToMainOnly()
    {
        var fake = new FakeScanService();
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();

        var saved = await service.UpdateConfigAsync(clientId, true, [], CancellationToken.None);

        Assert.Equal(["main"], saved.Branches);
    }

    [Fact]
    public async Task GetStatus_EnrichesEntriesWithLiveScanStatus()
    {
        var fake = new FakeScanService { LiveStatus = "Succeeded" };
        var service = this.CreateService(fake);
        var clientId = Guid.NewGuid();
        await service.HandlePushAsync(clientId, Push("main"), CancellationToken.None);
        await service.HandlePushAsync(clientId, Push("main", OtherSha), CancellationToken.None);

        var status = await service.GetStatusAsync(clientId, CancellationToken.None);

        Assert.True(status.Config.Enabled);
        Assert.Equal(["main"], status.Config.Branches);
        Assert.Equal(2, status.RecentScans.Count);
        Assert.All(
            status.RecentScans, entry =>
            {
                Assert.Equal("started", entry.Outcome);
                Assert.Equal("Succeeded", entry.ScanStatus);
                Assert.NotNull(entry.ScanId);
            });
        Assert.Equal(OtherSha, status.RecentScans[0].HeadSha);
        Assert.Equal("prateek", status.RecentScans[0].Pusher);
    }
}
