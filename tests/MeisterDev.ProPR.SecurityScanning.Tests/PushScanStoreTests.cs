// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the push-scan JSON store: default state (enabled, main only),
///     config round-trip, (repository, commit SHA) reservation dedupe, failed
///     attempts not blocking a retry, client isolation, and corrupt-file tolerance.
///     Tests run against a fresh temp directory per test — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class PushScanStoreTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-push-scan-tests", Guid.NewGuid().ToString("N"));

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

    private JsonFilePushScanStore CreateStore()
    {
        return new JsonFilePushScanStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFilePushScanStore>.Instance);
    }

    private static PushScanEntryRecord Pending(
        string repo = "https://github.com/acme/web",
        string branch = "main",
        string headSha = "abc1234567890abcdef1234567890abcdef12345678",
        string? pusher = "prateek")
    {
        return new(repo, branch, headSha, pusher, "pending", "Scan is being queued.", null, null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DefaultState_IsEnabledMainOnlyWithNoScans()
    {
        var store = this.CreateStore();

        var state = await store.GetStateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(state.Config.Enabled);
        Assert.Equal(["main"], state.Config.Branches);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public async Task SaveConfig_RoundTrips_AndKeepsEntries()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        await store.TryReserveAsync(clientId, Pending(), CancellationToken.None);

        var saved = await store.SaveConfigAsync(clientId, new PushScanConfigRecord(false, ["main", "release/*"]), CancellationToken.None);

        Assert.False(saved.Enabled);
        Assert.Equal(["main", "release/*"], saved.Branches);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);
        Assert.Single(state.Entries);
    }

    [Fact]
    public async Task TryReserve_DedupesSameRepoAndSha()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();

        var first = await store.TryReserveAsync(clientId, Pending(), CancellationToken.None);
        var second = await store.TryReserveAsync(clientId, Pending(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryReserve_AllowsDifferentShaAndDifferentRepo()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();

        var first = await store.TryReserveAsync(clientId, Pending(), CancellationToken.None);
        var otherSha = await store.TryReserveAsync(
            clientId,
            Pending(headSha: "def1234567890abcdef1234567890abcdef12345678"),
            CancellationToken.None);
        var otherRepo = await store.TryReserveAsync(
            clientId,
            Pending(repo: "https://github.com/acme/api"),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(otherSha);
        Assert.NotNull(otherRepo);
    }

    [Fact]
    public async Task TryReserve_AllowsRetryAfterAFailedAttempt()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        var pending = Pending();
        await store.TryReserveAsync(clientId, pending, CancellationToken.None);

        await store.UpdateEntryAsync(clientId, pending with { Outcome = "failed", Reason = "The scan could not be started." }, CancellationToken.None);

        var retry = await store.TryReserveAsync(clientId, Pending(), CancellationToken.None);

        Assert.NotNull(retry);
    }

    [Fact]
    public async Task UpdateEntry_ReplacesPendingWithStartedOutcome()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        var pending = Pending();
        await store.TryReserveAsync(clientId, pending, CancellationToken.None);

        var scanId = Guid.NewGuid();
        await store.UpdateEntryAsync(
            clientId,
            pending with { Outcome = "started", Reason = "Scan queued.", ScanId = scanId, ScanStatus = "Queued" },
            CancellationToken.None);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);
        var entry = Assert.Single(state.Entries);
        Assert.Equal("started", entry.Outcome);
        Assert.Equal(scanId, entry.ScanId);
    }

    [Fact]
    public async Task State_IsIsolatedPerClient()
    {
        var store = this.CreateStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await store.TryReserveAsync(first, Pending(), CancellationToken.None);
        await store.SaveConfigAsync(second, new PushScanConfigRecord(false, ["develop"]), CancellationToken.None);

        var firstState = await store.GetStateAsync(first, CancellationToken.None);
        var secondState = await store.GetStateAsync(second, CancellationToken.None);

        Assert.Single(firstState.Entries);
        Assert.True(firstState.Config.Enabled);
        Assert.Empty(secondState.Entries);
        Assert.False(secondState.Config.Enabled);
    }

    [Fact]
    public async Task CorruptFile_IsTreatedAsDefault()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        var root = this.tempRoot;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, $"{clientId:N}.push-scans.json"),
            "{ not json",
            CancellationToken.None);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);

        Assert.True(state.Config.Enabled);
        Assert.Equal(["main"], state.Config.Branches);
        Assert.Empty(state.Entries);
    }
}
