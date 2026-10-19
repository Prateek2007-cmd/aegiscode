// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Workers;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.TestSupport;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceRecoveryTests
{
    [Theory]
    [InlineData(false, "queued")]
    [InlineData(true, "in-progress")]
    public async Task CapturedRecoveryRequiresAnAttemptTokenToReportAnActiveLease(bool hasToken, string expected)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 1,
            LastHistoricalRefreshAttemptId = hasToken ? Guid.NewGuid() : null,
            LastHistoricalRefreshAttemptAt = now.AddHours(-1),
            HistoricalRefreshNotBefore = now.AddHours(1),
        };
        db.AddRange(
            aggregate, new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ClientId = client, RepositoryId = "repo", PullRequestId = 1,
                ProviderScope = "AzureDevOps:https://ado.example.test/org", BucketDate = new(2026, 10, 1), Outcome = "collectionUnknown", Count = 1,
            });
        await db.SaveChangesAsync();

        var response = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new ReviewerPerformanceQuery { Views = [new() { From = new(2026, 10, 1), To = new(2026, 10, 1) }] }, [client]);

        Assert.Equal(expected, Assert.Single(response.Views[0].ExclusionCatalog!).RecoveryDisposition);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public void ExplicitRequestRemainsQueuedBeforeAndAfterEligibilityTimeDespitePriorCompletion(int delayMinutes)
    {
        var now = DateTimeOffset.UtcNow;
        var aggregate = new CodeInsightPullRequest
        {
            LastHistoricalRefreshAttemptId = null,
            LastHistoricalRefreshAttemptAt = now.AddDays(-1),
            LastHistoricalRefreshCompletedAt = now.AddDays(-1).AddMinutes(1),
            HistoricalRefreshNotBefore = now.AddMinutes(delayMinutes),
        };
        Assert.Equal("queued", CodeInsightHistoricalRecoveryPolicy.Disposition(aggregate, true, now));
    }

    [Fact]
    public void SuccessfulObservationReportsCompletionWithoutClaimingAnActiveLease()
    {
        var now = DateTimeOffset.UtcNow;
        var aggregate = new CodeInsightPullRequest
        {
            LastHistoricalRefreshAttemptId = Guid.NewGuid(),
            LastHistoricalRefreshAttemptAt = now.AddMinutes(-5),
            LastHistoricalRefreshCompletedAt = now.AddMinutes(-1),
            HistoricalRefreshNotBefore = now.AddMinutes(-1),
        };
        Assert.Equal("complete", CodeInsightHistoricalRecoveryPolicy.Disposition(aggregate, true, now));
    }

    [Theory]
    [InlineData(true, "retryable")]
    [InlineData(false, "collection-disabled")]
    [InlineData(null, "collection-disabled")]
    public async Task CapturedRecoveryUsesCurrentCollectionPolicyWhileRetainingItsLastFailure(bool? enabled, string expected)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = Guid.NewGuid();
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 1,
            LastHistoricalRefreshReason = "CollectionDisabled",
        };
        if (enabled is not null)
        {
            db.Clients.Add(new ClientRecord { Id = client, DisplayName = "Client", CodeInsightsCollectionEnabled = enabled.Value });
        }

        db.AddRange(
            aggregate, new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ClientId = client, RepositoryId = "repo", PullRequestId = 1,
                ProviderScope = "AzureDevOps:https://ado.example.test/org", BucketDate = new(2026, 10, 1), Outcome = "collectionUnknown", Count = 1,
            });
        await db.SaveChangesAsync();

        var response = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new ReviewerPerformanceQuery { Views = [new() { From = new(2026, 10, 1), To = new(2026, 10, 1) }] }, [client]);

        var excluded = Assert.Single(response.Views[0].ExclusionCatalog!);
        Assert.Equal(expected, excluded.RecoveryDisposition);
        Assert.Equal(enabled, excluded.CollectionEnabled);
        Assert.Equal("CollectionDisabled", excluded.LastRefreshReason);
    }

    [Theory]
    [InlineData("SourceCoordinatesUnavailable")]
    [InlineData("ProviderConnectionUnavailable")]
    public async Task ManagedClaimsExcludeRepairRequiredSourcesBeforeTheScanLimit(string reason)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        CodeInsightPullRequest ready = null!;
        for (var number = 0; number < 252; number++)
        {
            var pr = new CodeInsightPullRequest
            {
                Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = number + 1,
                LastHistoricalRefreshReason = number == 251 ? "FutureFailure" : reason,
                LastHistoricalRefreshAttemptAt = number == 251 ? now.AddDays(-1) : null,
            };
            db.AddRange(
                pr, new CodeInsightFinding
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = "AzureDevOps:https://ado.test/org",
                    ProviderThreadId = "thread", PublicationState = CodeInsightPublicationState.Published,
                });
            ready = pr;
        }

        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(client, Arg.Any<CancellationToken>()).Returns(true);
        var store = new CodeInsightHistoricalRefreshStore(db, gate, providerRegistry: LocalScmPolicies.Registry);
        Assert.Equal(ready.Id, Assert.Single(await store.ClaimAsync(1, now, TimeSpan.FromMinutes(15))).AggregateId);
    }

    [Theory]
    [InlineData("SourceCoordinatesUnavailable", "repair-required")]
    [InlineData("ProviderConnectionUnavailable", "repair-required")]
    [InlineData("ProviderIdentityUnavailable", "repair-required")]
    [InlineData("HistoricalCapabilityUnsupported", "repair-required")]
    [InlineData("OwnershipUnavailable", "retryable")]
    [InlineData("ProviderReadFailed", "retryable")]
    [InlineData("ProcessingDeadlineExceeded", "retryable")]
    [InlineData("FutureFailure", "retryable")]
    public async Task ExclusionRecoveryUsesTheAutomaticRetryPolicy(string reason, string expected)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = Guid.NewGuid();
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 1,
            LastHistoricalRefreshReason = reason,
        };
        db.AddRange(
            aggregate, new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ClientId = client, RepositoryId = "repo", PullRequestId = 1,
                ProviderScope = "AzureDevOps:https://ado.example.test/org", BucketDate = new(2026, 10, 1), Outcome = "collectionUnknown", Count = 1,
            });
        await db.SaveChangesAsync();
        var response = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new ReviewerPerformanceQuery { Views = [new() { From = new(2026, 10, 1), To = new(2026, 10, 1) }] }, [client]);
        Assert.Equal(expected, Assert.Single(response.Views[0].ExclusionCatalog!).RecoveryDisposition);
    }

    [Fact]
    public void RecoveryLeaseAndCooldownBindIndependentlyWithFiniteBounds()
    {
        var options = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(
            options, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CODE_INSIGHTS_PROVIDER_EVIDENCE_LEASE_MINUTES"] = "7",
                    ["CODE_INSIGHTS_PROVIDER_EVIDENCE_RETRY_COOLDOWN_MINUTES"] = "29",
                }).Build());
        Assert.Equal(TimeSpan.FromMinutes(7), options.ProviderEvidenceLease);
        Assert.Equal(TimeSpan.FromMinutes(29), options.ProviderEvidenceRetryCooldown);
        options.ProviderEvidenceLeaseMinutes = int.MaxValue;
        options.ProviderEvidenceRetryCooldownMinutes = 0;
        Assert.Equal(TimeSpan.FromHours(1), options.ProviderEvidenceLease);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ProviderEvidenceRetryCooldown);
    }

    [Fact]
    public async Task WorkerLogsTheAcknowledgedProviderSweepTotals()
    {
        var provider = Substitute.For<ICodeInsightProviderEvidenceBackfill>();
        provider.SweepAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new CodeInsightProviderEvidenceBackfillResult(5, 2, 2, 1));
        using var services = new ServiceCollection().AddSingleton(provider).AddSingleton(Substitute.For<ICodeInsightRollupProjector>())
            .AddSingleton(Substitute.For<ICodeInsightSealSweeper>()).BuildServiceProvider();
        var logger = new RecoveryCapturingLogger<CodeInsightCatchUpWorker>();
        var worker = new CodeInsightCatchUpWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CodeInsightsOptions>(new()), logger);
        await worker.CatchUpOnceAsync(CancellationToken.None);
        var entry = Assert.Single(logger.Entries, entry => entry.Fields.ContainsKey("Attempted"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(5, entry.Fields["Attempted"]);
        Assert.Equal(2, entry.Fields["Observed"]);
        Assert.Equal(2, entry.Fields["Unavailable"]);
        Assert.Equal(1, entry.Fields["Disabled"]);
    }
}

internal sealed record RecoveryCapturedLog(LogLevel Level, IReadOnlyDictionary<string, object?> Fields, Exception? Exception, string Message);

internal sealed class RecoveryCapturingLogger<T> : ILogger<T>
{
    internal List<RecoveryCapturedLog> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        this.Entries.Add(
            new(
                logLevel, state is IEnumerable<KeyValuePair<string, object?>> fields
                    ? fields.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : new Dictionary<string, object?>(), exception, formatter(state, exception)));
    }
}
