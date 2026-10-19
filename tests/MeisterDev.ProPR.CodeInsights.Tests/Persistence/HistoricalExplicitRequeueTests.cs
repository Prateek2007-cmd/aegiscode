// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class HistoricalExplicitRequeueTests(PostgresContainerFixture fixture)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExplicitRequeueObservesFailedJudgmentsWithCompleteSourcesOnce(bool postgres, bool dimensionsFailed, bool priorCompletion)
    {
        const string scope = "AzureDevOps:https://ado.example.test/collection";
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var client = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>();
        if (postgres)
        {
            Assert.True(fixture.IsAvailable, "Explicit requeue verification requires PostgreSQL.");
            options.UseNpgsql(fixture.ConnectionString, settings => settings.UseVector());
        }
        else
        {
            options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        }

        await using var db = new MeisterProPRDbContext(options.Options);
        db.AddRange(
            new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Requeue", CreatedAt = now },
            new ClientRecord { Id = client, TenantId = tenant, DisplayName = "Requeue", CreatedAt = now });
        var earlier = new List<CodeInsightPullRequest>();
        var earlierCoverage = new List<CodeInsightHarvestCoverage>();
        for (var number = 1; number <= 251; number++)
        {
            var completed = Aggregate(number);
            completed.LastHistoricalRefreshAttemptId = Guid.NewGuid();
            completed.LastHistoricalRefreshAttemptAt = now.AddDays(-5);
            completed.LastHistoricalRefreshCompletedAt = now.AddDays(-4);
            completed.HistoricalRefreshNotBefore = now.AddDays(-4);
            earlier.Add(completed);
            earlierCoverage.Add(Coverage(completed));
        }

        var pr = Aggregate(1000);
        var oldToken = priorCompletion ? Guid.NewGuid() : (Guid?)null;
        pr.LastHistoricalRefreshAttemptId = oldToken;
        pr.LastHistoricalRefreshAttemptAt = priorCompletion ? now.AddDays(-2) : null;
        pr.LastHistoricalRefreshCompletedAt = priorCompletion ? now.AddDays(-1) : null;
        pr.HistoricalRefreshNotBefore = priorCompletion ? now.AddDays(-1) : null;
        var attemptedAt = pr.LastHistoricalRefreshAttemptAt;
        var completedAt = pr.LastHistoricalRefreshCompletedAt;
        var miss = new CodeInsightMiss
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = scope, ProviderThreadId = "human",
            HarvestedAt = now.AddDays(-30), SourceObservedAt = now.AddDays(-1), SourceFingerprint = "retained-source",
            JudgementFailed = !dimensionsFailed, DimensionJudgementFailed = dimensionsFailed,
        };
        miss.RecordJudgement(true, true, true, dimensionsFailed ? .9 : null, "retained-human", true, "Retained discussion", now.AddDays(-1));
        var job = new ReviewJob(Guid.NewGuid(), client, "https://ado.example.test/collection", "project", "repo", 1000, 1);
        db.AddRange(earlier);
        db.AddRange(earlierCoverage);
        db.AddRange(pr, Coverage(pr), miss, job);
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(client, Arg.Any<CancellationToken>()).Returns(true);
        var store = new CodeInsightHistoricalRefreshStore(db, gate, providerRegistry: LocalScmPolicies.Registry);
        try
        {
            Assert.Equal(CodeInsightHistoricalRequeueResult.Queued, await store.RequeueAsync(pr.Id, [client], now));
            var queued = await db.CodeInsightPullRequests.AsNoTracking().SingleAsync(row => row.Id == pr.Id);
            Assert.Null(queued.LastHistoricalRefreshAttemptId);
            Assert.Equal(attemptedAt, queued.LastHistoricalRefreshAttemptAt);
            Assert.Equal(completedAt, queued.LastHistoricalRefreshCompletedAt);
            Assert.Equal(now.AddDays(-30), queued.LastActivityAt);
            Assert.Equal(now.AddDays(-30), queued.UpdatedAt);

            var claim = Assert.Single(await store.ClaimAsync(1, now.AddSeconds(1), TimeSpan.FromMinutes(15)));
            Assert.Equal(pr.Id, claim.AggregateId);
            Assert.Equal(CodeInsightHistoricalGap.None, claim.Gaps);
            Assert.Equal(job.Id, Assert.Single(claim.Sources).RepresentativeJobId);
            Assert.Equal(CodeInsightHistoricalGap.None, Assert.Single(claim.Sources).Gaps);
            var retained = await db.CodeInsightMisses.AsNoTracking().SingleAsync(row => row.Id == miss.Id);
            Assert.Equal("retained-source", retained.SourceFingerprint);
            Assert.Equal("Retained discussion", retained.EncryptedDiscussion);
            Assert.Equal(!dimensionsFailed, retained.JudgementFailed);
            Assert.Equal(dimensionsFailed, retained.DimensionJudgementFailed);
            if (oldToken is not null)
            {
                Assert.False(
                    await store.RecordResultAsync(
                        pr.Id, oldToken.Value,
                        new(CodeInsightObservationStatus.Observed, now), now.AddSeconds(2), TimeSpan.FromHours(6)));
            }

            Assert.True(
                await store.RecordResultAsync(
                    pr.Id, claim.AttemptId,
                    new(CodeInsightObservationStatus.Observed, now.AddSeconds(1)), now.AddSeconds(2), TimeSpan.FromHours(6)));
            db.RemoveRange(earlierCoverage);
            db.RemoveRange(earlier);
            await db.SaveChangesAsync();
            Assert.Empty(await store.ClaimAsync(1, now.AddMinutes(20), TimeSpan.FromMinutes(15)));
        }
        finally
        {
            if (postgres)
            {
                await db.ReviewJobs.Where(row => row.ClientId == client).ExecuteDeleteAsync();
                await db.Clients.Where(row => row.Id == client).ExecuteDeleteAsync();
                await db.Tenants.Where(row => row.Id == tenant).ExecuteDeleteAsync();
            }
        }

        CodeInsightPullRequest Aggregate(int number) => new()
        {
            Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = number,
            CreatedAt = now.AddDays(-30), LastActivityAt = now.AddDays(-30), UpdatedAt = now.AddDays(-30),
        };

        CodeInsightHarvestCoverage Coverage(CodeInsightPullRequest aggregate) => new()
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = scope,
            EnumerationComplete = true, AllHumanThreadsResolved = true, ObservedAt = now.AddDays(-1),
        };
    }
}
