// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ModelEvidenceMaintenanceBoundaryTests
{
    [Fact]
    public async Task UnindexableLongPathsDoNotConsumeThePendingJobBatch()
    {
        await using var harness = new Harness();
        harness.Seed(1, new string('x', 513));
        var good = harness.Seed(2, "good.cs");
        await harness.Db.SaveChangesAsync();
        Assert.Equal(1, await harness.Maintenance.SweepAsync(1));
        Assert.Equal(good.Job.Id, (await harness.Db.CodeInsightReviewExposures.SingleAsync()).JobId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WholeSourceReplayEligibilityIsCheckedBeforeTheRetryBatchLimit(bool oversized)
    {
        await using var harness = new Harness();
        var bad = harness.Seed(1, "bad.cs", oversized ? 101 : 2);
        if (!oversized)
        {
            bad.Concerns[1].RetainedSourceComplete = false;
        }

        var good = harness.Seed(2, "good.cs", 1);
        await harness.Db.SaveChangesAsync();
        await harness.Maintenance.SweepAsync(1);
        await harness.Observer.Received(1).ReplayRetainedAsync(
            new(good.Job.ClientId, good.Job.RepositoryId, 42), Harness.Scope,
            good.Connection, Arg.Any<CancellationToken>());
        await harness.Observer.DidNotReceive().ReplayRetainedAsync(
            new(bad.Job.ClientId, bad.Job.RepositoryId, 42), Harness.Scope,
            bad.Connection, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentMatcherInputLimitHasNoAutomaticOrExplicitUnchangedReplay()
    {
        await using var harness = new Harness();
        var source = harness.Seed(1, "f.cs", 1);
        source.Concerns[0].JudgementFailed = false;
        harness.Db.CodeInsightModelConcernEvidence.Add(
            new()
            {
                Id = Guid.NewGuid(), HumanConcernId = source.Concerns[0].Id, ModelId = "A", LogicalModelName = "logical-A",
                Decision = "unknown", UnavailableReason = "model-concern-input-limit-exceeded"
            });
        await harness.Db.SaveChangesAsync();
        await harness.Maintenance.SweepAsync(1);
        await harness.Observer.DidNotReceiveWithAnyArgs().ReplayRetainedAsync(default!, default!, default, default);
        Assert.Equal(
            ModelEvidenceRetryResult.Unreconstructable,
            await harness.Maintenance.RetryAsync(new(source.Aggregate.Id, Harness.Scope, source.Connection), [source.Job.ClientId]));
        await harness.Observer.DidNotReceiveWithAnyArgs().ReplayRetainedAsync(default!, default!, default, default);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal const string Scope = "AzureDevOps:https://dev.azure.com";

        internal MeisterProPRDbContext Db { get; } =
            new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        internal ICodeInsightModelConcernObserver Observer { get; } = Substitute.For<ICodeInsightModelConcernObserver>();
        internal ModelEvidenceMaintenance Maintenance { get; }

        internal Harness()
        {
            var gate = Substitute.For<ICodeInsightsCollectionGate>();
            gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            var store = Substitute.For<IDiscussionSynchronizationStore>();
            var lease = Substitute.For<IDiscussionSynchronizationLease>();
            lease.BeginWrites().Returns(Substitute.For<IDisposable>());
            store.TryAcquireRetainedReplayAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(lease);
            this.Observer.ReplayRetainedAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(true);
            this.Maintenance = new(
                this.Db, gate,
                new CodeInsightReviewExposureCollector(this.Db, gate, NullLogger<CodeInsightReviewExposureCollector>.Instance), this.Observer, store);
        }

        internal (ReviewJob Job, CodeInsightPullRequest Aggregate, Guid Connection, CodeInsightHumanConcern[] Concerns) Seed(
            int ordinal, string path, int concernCount = 0)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}");
            var job = new ReviewJob(id, Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo-" + ordinal, 42, 1);
            var connection = Guid.NewGuid();
            var file = new ReviewFileResult(job.Id, path);
            file.RecordModelEvidence(
                ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A")]) with
                {
                    JobId = job.Id, ClientId = job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = 42,
                    ProviderScope = Scope, SourceConnectionId = connection, RevisionKey = "1", FilePath = path
                });
            var aggregate = new CodeInsightPullRequest { Id = id, ClientId = job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = 42 };
            var concerns = Enumerable.Range(0, concernCount).Select(index => new CodeInsightHumanConcern
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = Scope, ConnectionId = connection,
                NativeIdentity = Guid.NewGuid().ToString("N"), RetainedSourceComplete = true,
                JudgementFailed = true, UnavailableReason = "model-concern-judgement-failed"
            }).ToArray();
            this.Db.AddRange(job, file, aggregate);
            this.Db.AddRange(concerns);
            return (job, aggregate, connection, concerns);
        }

        public ValueTask DisposeAsync() => this.Db.DisposeAsync();
    }
}
