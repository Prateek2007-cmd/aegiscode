// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Threading.Channels;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Regression coverage for the suppression-history JSON fix (2026-10-24):
///     the old implementation built the history with TrimEnd(']') string surgery,
///     which corrupted an empty history ("[]") into invalid JSON ("[,{...}]") on
///     the first suppression. The fix parses, appends, and re-serializes, and
///     tolerates a corrupt history instead of failing the request.
///     These tests run against an EF Core InMemory database (no Postgres needed).
/// </summary>
public sealed class SecurityScanSuppressionTests
{
    private static SecurityScanService CreateService(MeisterProPRDbContext db)
    {
        var channel = Channel.CreateUnbounded<SecurityScanJob>();
        return new SecurityScanService(
            db,
            channel.Writer,
            Options.Create(new SecurityScanningOptions()),
            NullLogger<SecurityScanService>.Instance);
    }

    private static MeisterProPRDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    private static SecurityFindingRecord SeedFinding(
        MeisterProPRDbContext db, Guid clientId, string? suppressionsJson)
    {
        var finding = new SecurityFindingRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ScanId = Guid.NewGuid(),
            Fingerprint = $"fp-{Guid.NewGuid():N}",
            Scanner = "dep-scan",
            Severity = 2,
            Title = "Test finding",
            Category = "test",
            SuppressionsJson = suppressionsJson,
            Status = (int)SecurityFindingStatus.Open,
        };
        db.SecurityFindings.Add(finding);
        db.SaveChanges();
        return finding;
    }

    private static List<JsonElement> ParseHistory(string? json) =>
        JsonSerializer.Deserialize<List<JsonElement>>(json ?? "null") ?? [];

    [Fact]
    public async Task Suppressing_a_finding_with_empty_history_writes_valid_json_with_one_entry()
    {
        using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finding = SeedFinding(db, clientId, "[]");
        var service = CreateService(db);

        var ok = await service.SuppressFindingAsync(clientId, finding.Id, "false positive", null, CancellationToken.None);

        Assert.True(ok);
        var updated = await db.SecurityFindings.FindAsync(finding.Id);
        Assert.NotNull(updated);
        Assert.Equal((int)SecurityFindingStatus.Suppressed, updated.Status);
        var history = ParseHistory(updated.SuppressionsJson);
        Assert.Single(history);
        Assert.Equal("false positive", history[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Suppressing_appends_to_existing_history_instead_of_replacing_it()
    {
        using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finding = SeedFinding(
            db, clientId, """[{"reason":"first","byUserId":null,"at":"2026-10-24T00:00:00+00:00"}]""");
        var service = CreateService(db);

        var ok = await service.SuppressFindingAsync(clientId, finding.Id, "second reason", null, CancellationToken.None);

        Assert.True(ok);
        var updated = await db.SecurityFindings.FindAsync(finding.Id);
        Assert.NotNull(updated);
        var history = ParseHistory(updated.SuppressionsJson);
        Assert.Equal(2, history.Count);
        Assert.Equal("first", history[0].GetProperty("reason").GetString());
        Assert.Equal("second reason", history[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Suppressing_with_corrupt_history_starts_fresh_instead_of_failing()
    {
        using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finding = SeedFinding(db, clientId, "[,{broken");
        var service = CreateService(db);

        var ok = await service.SuppressFindingAsync(clientId, finding.Id, "recovery", null, CancellationToken.None);

        Assert.True(ok);
        var updated = await db.SecurityFindings.FindAsync(finding.Id);
        Assert.NotNull(updated);
        var history = ParseHistory(updated.SuppressionsJson);
        Assert.Single(history);
        Assert.Equal("recovery", history[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Suppressing_a_missing_or_other_client_finding_returns_false()
    {
        using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finding = SeedFinding(db, clientId, "[]");
        var service = CreateService(db);

        Assert.False(await service.SuppressFindingAsync(clientId, Guid.NewGuid(), "nope", null, CancellationToken.None));
        Assert.False(await service.SuppressFindingAsync(Guid.NewGuid(), finding.Id, "nope", null, CancellationToken.None));
    }

    [Fact]
    public async Task Suppressing_without_a_reason_throws()
    {
        using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finding = SeedFinding(db, clientId, "[]");
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SuppressFindingAsync(clientId, finding.Id, "  ", null, CancellationToken.None));
    }
}
