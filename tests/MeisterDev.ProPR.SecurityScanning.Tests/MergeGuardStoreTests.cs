// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the merge-guard JSON store: default state, config round-trip,
///     per-PR check upsert, client isolation, and corrupt-file tolerance.
///     Tests run against a fresh temp directory per test — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class MergeGuardStoreTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-merge-guard-tests", Guid.NewGuid().ToString("N"));

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

    private JsonFileMergeGuardStore CreateStore()
    {
        return new JsonFileMergeGuardStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileMergeGuardStore>.Instance);
    }

    private static MergeGuardCheckRecord Check(
        string repo = "https://github.com/acme/web",
        int pr = 7,
        string headSha = "abc1234",
        string conclusion = "success") =>
        new(
            repo, pr, headSha, 123, "https://github.com/acme/web/check-runs/123", conclusion,
            "Merge guard passed", "No blocking findings.", 0, 0,
            DateTimeOffset.UtcNow, true, null);

    [Fact]
    public async Task DefaultState_IsDisabledHighThresholdWithNoChecks()
    {
        var store = this.CreateStore();

        var state = await store.GetStateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(state.Config.Enabled);
        Assert.Equal("High", state.Config.Threshold);
        Assert.Empty(state.Checks);
    }

    [Fact]
    public async Task SaveConfig_RoundTrips_AndKeepsChecks()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        await store.SaveCheckAsync(clientId, Check(), CancellationToken.None);

        var saved = await store.SaveConfigAsync(clientId, new MergeGuardConfigRecord(true, "Medium"), CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.Equal("Medium", saved.Threshold);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);
        Assert.True(state.Config.Enabled);
        Assert.Single(state.Checks);
    }

    [Fact]
    public async Task SaveCheck_UpsertsSamePullRequest()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        await store.SaveCheckAsync(clientId, Check(conclusion: "failure"), CancellationToken.None);

        await store.SaveCheckAsync(clientId, Check(conclusion: "success"), CancellationToken.None);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);
        var only = Assert.Single(state.Checks);
        Assert.Equal("success", only.Conclusion);
    }

    [Fact]
    public async Task SaveCheck_AppendsDifferentPullRequests()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        await store.SaveCheckAsync(clientId, Check(pr: 7), CancellationToken.None);
        await store.SaveCheckAsync(clientId, Check(pr: 8), CancellationToken.None);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);

        Assert.Equal(2, state.Checks.Count);
    }

    [Fact]
    public async Task Clients_AreIsolated()
    {
        var store = this.CreateStore();
        await store.SaveConfigAsync(Guid.NewGuid(), new MergeGuardConfigRecord(true, "Critical"), CancellationToken.None);

        var other = await store.GetStateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(other.Config.Enabled);
        Assert.Empty(other.Checks);
    }

    [Fact]
    public async Task CorruptFile_TreatedAsDefault()
    {
        var store = this.CreateStore();
        var clientId = Guid.NewGuid();
        var root = this.tempRoot;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, $"{clientId:N}.merge-guard.json"),
            "{ not json",
            CancellationToken.None);

        var state = await store.GetStateAsync(clientId, CancellationToken.None);

        Assert.False(state.Config.Enabled);
        Assert.Empty(state.Checks);
    }
}
