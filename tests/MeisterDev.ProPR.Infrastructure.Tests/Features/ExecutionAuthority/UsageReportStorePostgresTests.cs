// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.ExecutionAuthority;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.ExecutionAuthority;

[Collection("PostgresIntegration2")]
public sealed class UsageReportStorePostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.Context();
        await db.AiUsageReports.Where(report => report.ConnectionId == this._connectionId).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task StoredReports_AreReadBackInSequenceAtOnce()
    {
        var store = new UsageReportStore(new ContextFactory(this.Context), this._time);
        var execution = new ExecutionIdentity(ExecutionKind.ThreadPass, Guid.NewGuid(), 2);

        var first = await store.AppendAsync(this.Report(execution, inputTokens: 100));
        var second = await store.AppendAsync(this.Report(null, inputTokens: 200));

        Assert.True(second.Sequence > first.Sequence);
        var read = (await store.ReadAsync(first.Sequence - 1, 10, CancellationToken.None)).Where(this.Mine).ToList();

        Assert.Equal([first.Sequence, second.Sequence], read.Select(report => report.Sequence));
        Assert.Equal(first, read[0]);
        Assert.Equal(second, read[1]);
    }

    // An append waits while an earlier append holds the lock, so it cannot commit a higher sequence before the
    // earlier report is committed and a reader cannot move its cursor past the earlier report.
    [Fact]
    public async Task AnAppend_WaitsUntilAnEarlierAppendHasCommitted()
    {
        var store = new UsageReportStore(new ContextFactory(this.Context), this._time);
        await using var earlier = this.Context();
        await using var transaction = await earlier.Database.BeginTransactionAsync();
        await earlier.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({UsageReportStore.AppendLockKey}, 0))");

        var append = store.AppendAsync(this.Report(null, inputTokens: 300));

        Assert.NotSame(append, await Task.WhenAny(append, Task.Delay(TimeSpan.FromMilliseconds(500))));
        await transaction.CommitAsync();
        Assert.True((await append).Sequence > 0);
    }

    private bool Mine(UsageReport report) => report.Connection.ConnectionId == this._connectionId;

    private UsageReport Report(ExecutionIdentity? execution, long inputTokens)
    {
        return new UsageReport(
            0,
            execution,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new AiConnectionRevision(this._connectionId, 3),
            "openai",
            "gpt-test",
            AiOperation.Chat,
            inputTokens,
            20,
            5,
            0,
            2,
            false,
            TimeSpan.FromMilliseconds(1_500),
            this._time.GetUtcNow());
    }

    private MeisterProPRDbContext Context() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);

    private sealed class ContextFactory(Func<MeisterProPRDbContext> create) : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext() => create();
    }
}
