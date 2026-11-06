// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for custom review rules: creation validation (invalid regex rejected
///     with the real error, never stored), update, enable/disable, delete, client
///     scoping, and the JSON-file store round-trip. Enforcement itself is covered by
///     the pure-function tests in <c>CustomReviewRulesTests</c>.
///     Tests run against an in-memory store (or a fresh temp directory for the file
///     store) — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class ReviewRuleServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-review-rules-tests", Guid.NewGuid().ToString("N"));

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

    private sealed class InMemoryReviewRuleStore : IReviewRuleStore
    {
        private readonly Dictionary<Guid, ReviewRuleRecord> records = new();

        public Task<IReadOnlyList<ReviewRuleRecord>> ListAsync(Guid clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReviewRuleRecord>>(this.records.Values.Where(r => r.ClientId == clientId).ToList());

        public Task<ReviewRuleRecord?> GetAsync(Guid clientId, Guid ruleId, CancellationToken ct) =>
            Task.FromResult(this.records.TryGetValue(ruleId, out var record) && record.ClientId == clientId ? record : null);

        public Task<ReviewRuleRecord> SaveAsync(ReviewRuleRecord record, CancellationToken ct)
        {
            this.records[record.Id] = record;
            return Task.FromResult(record);
        }

        public Task<bool> DeleteAsync(Guid clientId, Guid ruleId, CancellationToken ct) =>
            Task.FromResult(this.records.TryGetValue(ruleId, out var record) && record.ClientId == clientId && this.records.Remove(ruleId));
    }

    private static ReviewRuleService CreateService(IReviewRuleStore store) =>
        new(store, NullLogger<ReviewRuleService>.Instance);

    [Fact]
    public async Task Create_ValidRule_StoredEnabled()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();

        var entry = await service.CreateAsync(
            clientId, "No Console.WriteLine", "Console.WriteLine", false, "Error", "Do not write to the console.", CancellationToken.None);

        Assert.True(entry.Enabled);
        Assert.Equal("Error", entry.Severity);
        Assert.Single(await service.ListAsync(clientId, CancellationToken.None));
        Assert.Single(await service.ListEnabledAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_InvalidRegex_RejectedWithRealError_NothingStored()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(clientId, "Bad regex", "([unclosed", true, "Error", "Never stored.", CancellationToken.None));

        Assert.NotNull(ex.Message);
        Assert.Empty(await service.ListAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_UnknownSeverity_Rejected()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(clientId, "Bad severity", "x", false, "Critical", "Nope.", CancellationToken.None));
    }

    [Fact]
    public async Task Create_MissingNameOrMessage_Rejected()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(clientId, "  ", "x", false, "Warning", "Has message.", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(clientId, "Has name", "x", false, "Warning", "  ", CancellationToken.None));
    }

    [Fact]
    public async Task SetEnabled_DisabledRule_NotReturnedByListEnabled()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();
        var entry = await service.CreateAsync(clientId, "Rule", "TODO", false, "Warning", "No TODOs.", CancellationToken.None);

        var disabled = await service.SetEnabledAsync(clientId, entry.Id, false, CancellationToken.None);

        Assert.NotNull(disabled);
        Assert.False(disabled.Enabled);
        Assert.Empty(await service.ListEnabledAsync(clientId, CancellationToken.None));
        Assert.Single(await service.ListAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task Update_UnknownRule_ReturnsNull()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var result = await service.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), "X", "x", false, "Info", "M.", true, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_RemovesRule_AndSecondDeleteReturnsFalse()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientId = Guid.NewGuid();
        var entry = await service.CreateAsync(clientId, "Rule", "TODO", false, "Warning", "No TODOs.", CancellationToken.None);

        Assert.True(await service.DeleteAsync(clientId, entry.Id, CancellationToken.None));
        Assert.False(await service.DeleteAsync(clientId, entry.Id, CancellationToken.None));
        Assert.Empty(await service.ListAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task List_IsClientScoped()
    {
        var service = CreateService(new InMemoryReviewRuleStore());
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await service.CreateAsync(clientA, "Rule A", "TODO", false, "Warning", "No TODOs.", CancellationToken.None);

        Assert.Single(await service.ListAsync(clientA, CancellationToken.None));
        Assert.Empty(await service.ListAsync(clientB, CancellationToken.None));
    }

    [Fact]
    public async Task JsonFileStore_RoundTrip()
    {
        var options = Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot });
        var store = new JsonFileReviewRuleStore(options, NullLogger<JsonFileReviewRuleStore>.Instance);
        var service = CreateService(store);
        var clientId = Guid.NewGuid();

        var entry = await service.CreateAsync(clientId, "Rule", "TODO", false, "Warning", "No TODOs.", CancellationToken.None);

        // A fresh store instance over the same root reads the same rule back.
        var fresh = new JsonFileReviewRuleStore(options, NullLogger<JsonFileReviewRuleStore>.Instance);
        var reloaded = await fresh.GetAsync(clientId, entry.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal("TODO", reloaded.Pattern);
    }
}
