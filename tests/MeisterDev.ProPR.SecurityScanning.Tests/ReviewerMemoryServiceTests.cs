// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for reviewer memory: outcome counting, the auto-mute threshold (five
///     dismissals with no accepts), sticky muting, un-mute and reset, prompt-guidance
///     capping, client scoping, and the JSON-file store round-trip.
///     Tests run against an in-memory store (or a fresh temp directory for the file
///     store) — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class ReviewerMemoryServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-reviewer-memory-tests", Guid.NewGuid().ToString("N"));

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

    private sealed class InMemoryReviewerMemoryStore : IReviewerMemoryStore
    {
        private readonly Dictionary<string, ReviewerMemoryRecord> records = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<ReviewerMemoryRecord>> ListAsync(Guid clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReviewerMemoryRecord>>(this.records.Values.Where(r => r.ClientId == clientId).ToList());

        public Task<ReviewerMemoryRecord?> GetAsync(Guid clientId, string ruleKey, CancellationToken ct) =>
            Task.FromResult(this.records.TryGetValue(Key(clientId, ruleKey), out var record) ? record : null);

        public Task<ReviewerMemoryRecord> SaveAsync(ReviewerMemoryRecord record, CancellationToken ct)
        {
            this.records[Key(record.ClientId, record.RuleKey)] = record;
            return Task.FromResult(record);
        }

        public Task<bool> DeleteAsync(Guid clientId, string ruleKey, CancellationToken ct) =>
            Task.FromResult(this.records.Remove(Key(clientId, ruleKey)));

        private static string Key(Guid clientId, string ruleKey) => clientId.ToString("N") + "|" + ruleKey;
    }

    private static ReviewerMemoryService CreateService(IReviewerMemoryStore store) =>
        new(store, NullLogger<ReviewerMemoryService>.Instance);

    private static async Task<ReviewerMemoryEntry> DismissAsync(
        ReviewerMemoryService service,
        Guid clientId,
        string ruleKey,
        string label = "Avoid Console.WriteLine.") =>
        await service.RecordOutcomeAsync(clientId, ruleKey, label, "review", ReviewerMemoryOutcome.Dismissed, CancellationToken.None);

    [Fact]
    public async Task FirstDismissal_CreatesEntryWithOneDismissalNotMuted()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        var entry = await DismissAsync(service, clientId, "review:avoid console.writeline");

        Assert.Equal("review:avoid console.writeline", entry.RuleKey);
        Assert.Equal(0, entry.Accepts);
        Assert.Equal(1, entry.Dismissals);
        Assert.False(entry.Muted);
        Assert.Null(entry.MutedAt);
        Assert.Null(entry.MutedReason);
    }

    [Fact]
    public async Task FiveDismissals_MutesRuleWithReason()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        ReviewerMemoryEntry entry = null!;
        for (var i = 0; i < 5; i++)
        {
            entry = await DismissAsync(service, clientId, "review:avoid console.writeline");
        }

        Assert.True(entry.Muted);
        Assert.NotNull(entry.MutedAt);
        Assert.Contains("5", entry.MutedReason, StringComparison.Ordinal);
        Assert.Contains("no accepts", entry.MutedReason, StringComparison.Ordinal);

        var muted = await service.GetMutedRuleKeysAsync(clientId, CancellationToken.None);
        Assert.Contains("review:avoid console.writeline", muted);
    }

    [Fact]
    public async Task FourDismissalsThenAccept_DoesNotMute()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        for (var i = 0; i < 4; i++)
        {
            await DismissAsync(service, clientId, "review:avoid console.writeline");
        }

        var entry = await service.RecordOutcomeAsync(
            clientId, "review:avoid console.writeline", "Avoid Console.WriteLine.",
            "review", ReviewerMemoryOutcome.Accepted, CancellationToken.None);

        Assert.Equal(1, entry.Accepts);
        Assert.Equal(4, entry.Dismissals);
        Assert.False(entry.Muted);
    }

    [Fact]
    public async Task AcceptAfterMute_KeepsMutedUntilExplicitUnmute()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await DismissAsync(service, clientId, "review:avoid console.writeline");
        }

        var entry = await service.RecordOutcomeAsync(
            clientId, "review:avoid console.writeline", "Avoid Console.WriteLine.",
            "review", ReviewerMemoryOutcome.Accepted, CancellationToken.None);

        Assert.Equal(1, entry.Accepts);
        Assert.True(entry.Muted, "Muting is sticky: only an explicit un-mute lifts it.");
    }

    [Fact]
    public async Task Unmute_ClearsFlagKeepsCounts()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await DismissAsync(service, clientId, "review:avoid console.writeline");
        }

        var unmuted = await service.UnmuteAsync(clientId, "review:avoid console.writeline", CancellationToken.None);

        Assert.NotNull(unmuted);
        Assert.False(unmuted!.Muted);
        Assert.Null(unmuted.MutedAt);
        Assert.Null(unmuted.MutedReason);
        Assert.Equal(0, unmuted.Accepts);
        Assert.Equal(5, unmuted.Dismissals);

        var muted = await service.GetMutedRuleKeysAsync(clientId, CancellationToken.None);
        Assert.DoesNotContain("review:avoid console.writeline", muted);
    }

    [Fact]
    public async Task Unmute_UnknownRule_ReturnsNull()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var entry = await service.UnmuteAsync(Guid.NewGuid(), "review:no such rule", CancellationToken.None);
        Assert.Null(entry);
    }

    [Fact]
    public async Task Reset_DeletesEntryAndReturnsTrue_UnknownReturnsFalse()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        await DismissAsync(service, clientId, "review:avoid console.writeline");

        Assert.True(await service.ResetAsync(clientId, "review:avoid console.writeline", CancellationToken.None));
        Assert.False(await service.ResetAsync(clientId, "review:avoid console.writeline", CancellationToken.None));
        Assert.Empty(await service.ListAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task RecordOutcome_InvalidKeyOrSource_ThrowsArgumentException()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordOutcomeAsync(clientId, "not-a-key", "Label", "review", ReviewerMemoryOutcome.Dismissed, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordOutcomeAsync(clientId, "review:some rule", "Label", "other", ReviewerMemoryOutcome.Dismissed, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordOutcomeAsync(clientId, "review:some rule", "   ", "review", ReviewerMemoryOutcome.Dismissed, CancellationToken.None));
    }

    [Fact]
    public async Task Outcomes_AreScopedPerClient()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        await DismissAsync(service, clientA, "review:avoid console.writeline");

        Assert.Single(await service.ListAsync(clientA, CancellationToken.None));
        Assert.Empty(await service.ListAsync(clientB, CancellationToken.None));
    }

    [Fact]
    public async Task BuildPromptGuidance_OnlyMutedRulesCappedAtTen()
    {
        var service = CreateService(new InMemoryReviewerMemoryStore());
        var clientId = Guid.NewGuid();

        Assert.Empty(await service.BuildPromptGuidanceAsync(clientId, CancellationToken.None));

        for (var rule = 0; rule < 11; rule++)
        {
            for (var i = 0; i < 5; i++)
            {
                await DismissAsync(service, clientId, $"review:muted rule {rule}", $"Muted rule {rule}.");
            }
        }

        // One rule below the threshold never appears in guidance.
        await DismissAsync(service, clientId, "review:not muted yet", "Not muted yet.");

        var guidance = await service.BuildPromptGuidanceAsync(clientId, CancellationToken.None);
        Assert.Equal(10, guidance.Count);
        Assert.All(guidance, line => Assert.Contains("muted by reviewer memory", line, StringComparison.Ordinal));
        Assert.DoesNotContain(guidance, line => line.Contains("Not muted yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsonFileStore_RoundTripsAcrossServiceInstances()
    {
        var options = Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot });
        var store = new JsonFileReviewerMemoryStore(options, NullLogger<JsonFileReviewerMemoryStore>.Instance);
        var clientId = Guid.NewGuid();

        var first = CreateService(store);
        await DismissAsync(first, clientId, "review:avoid console.writeline");

        // A fresh service instance over the same store reads the same on-disk memory.
        var second = CreateService(store);
        var entries = await second.ListAsync(clientId, CancellationToken.None);
        Assert.Single(entries);
        Assert.Equal(1, entries[0].Dismissals);

        Assert.True(await second.ResetAsync(clientId, "review:avoid console.writeline", CancellationToken.None));
        Assert.Empty(await second.ListAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task JsonFileStore_CorruptFile_ReadsAsEmpty()
    {
        var options = Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot });
        var store = new JsonFileReviewerMemoryStore(options, NullLogger<JsonFileReviewerMemoryStore>.Instance);
        var clientId = Guid.NewGuid();
        var path = Path.Combine(this.tempRoot, $"{clientId:N}.reviewer-memory.json");

        Directory.CreateDirectory(this.tempRoot);
        await File.WriteAllTextAsync(path, "{ this is not json");

        var entries = await store.ListAsync(clientId, CancellationToken.None);
        Assert.Empty(entries);
    }
}
