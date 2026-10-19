// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.Ports;
using Microsoft.Extensions.Options;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ModelEvidenceMaintenanceTests
{
    [Fact]
    public async Task RetainedRetryOnlyResetsAndReplaysTheCapturedSourceOnASharedAggregate()
    {
        await using var db =
            new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 42, 1);
        const string scope = "AzureDevOps:https://dev.azure.com";
        var connection = Guid.NewGuid();
        var aggregate = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = job.ClientId, RepositoryId = "repo", PullRequestId = 42 };
        var a = new CodeInsightHumanConcern
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = scope,
            ConnectionId = connection, RetainedSourceComplete = true, JudgementFailed = true, FailedAttempts = 3
        };
        var b = new CodeInsightHumanConcern
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = "GitHub:https://other.example",
            ConnectionId = Guid.NewGuid(), RetainedSourceComplete = false, JudgementFailed = true, FailedAttempts = 3,
            LastModelEvidenceRetryAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var file = new ReviewFileResult(job.Id, "a.cs");
        file.RecordModelEvidence(
            ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A")]) with
            {
                JobId = job.Id,
                ClientId = job.ClientId, RepositoryId = "repo", PullRequestId = 42, ProviderScope = scope, SourceConnectionId = connection, RevisionKey = "1"
            });
        db.AddRange(job, aggregate, a, b, file);
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var observer = Substitute.For<ICodeInsightModelConcernObserver>();
        observer.ReplayRetainedAsync(Arg.Any<CodeInsightPullRequestKey>(), scope, connection, Arg.Any<CancellationToken>()).Returns(true);
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var lease = Substitute.For<IDiscussionSynchronizationLease>();
        lease.BeginWrites().Returns(Substitute.For<IDisposable>());
        store.TryAcquireRetainedReplayAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(lease);
        var maintenance = new ModelEvidenceMaintenance(db, gate, Substitute.For<ICodeInsightReviewExposureCollector>(), observer, store);
        Assert.Equal(ModelEvidenceRetryResult.Retried, await maintenance.RetryAsync(new(aggregate.Id, scope, connection), [job.ClientId]));
        Assert.Equal(0, a.FailedAttempts);
        Assert.Equal(3, b.FailedAttempts);
        await observer.Received(1).ReplayRetainedAsync(new(job.ClientId, "repo", 42), scope, connection, Arg.Any<CancellationToken>());
        await observer.DidNotReceive().ReplayRetainedAsync(Arg.Any<CodeInsightPullRequestKey>(), b.ProviderScope, b.ConnectionId, Arg.Any<CancellationToken>());

        a.FailedAttempts = 2;
        a.LastModelEvidenceRetryAt = DateTimeOffset.UtcNow.AddHours(-1);
        observer.ClearReceivedCalls();
        observer.ReplayRetainedAsync(Arg.Any<CodeInsightPullRequestKey>(), scope, connection, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            a.FailedAttempts++;
            await db.SaveChangesAsync();
            return true;
        });
        await db.SaveChangesAsync();
        await maintenance.SweepAsync(1);
        Assert.Equal(3, a.FailedAttempts);
        a.LastModelEvidenceRetryAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        await maintenance.SweepAsync(1);
        await observer.Received(1).ReplayRetainedAsync(new(job.ClientId, "repo", 42), scope, connection, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetainedRetryIsScopedDisabledAndCooledDownBeforeReadingAnyProof()
    {
        await using var db =
            new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var connection = Guid.NewGuid();
        var aggregate = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 42 };
        db.Add(aggregate);
        var retained = new CodeInsightHumanConcern
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ProviderScope = "GitHub:https://github.com", ConnectionId = connection,
            LastModelEvidenceRetryAt = now.AddMinutes(-6)
        };
        db.Add(retained);
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        var options = Substitute.For<IOptionsMonitor<CodeInsightsOptions>>();
        options.CurrentValue.Returns(new CodeInsightsOptions { ModelEvidenceRetryCooldownMinutes = 17 });
        var maintenance = new ModelEvidenceMaintenance(db, gate, Substitute.For<ICodeInsightReviewExposureCollector>(), options: options);
        Assert.Equal(
            ModelEvidenceRetryResult.NotFound, await maintenance.RetryAsync(new(aggregate.Id, "GitHub:https://github.com", connection), [Guid.NewGuid()]));
        Assert.Equal(
            ModelEvidenceRetryResult.Disabled, await maintenance.RetryAsync(new(aggregate.Id, "GitHub:https://github.com", connection), [aggregate.ClientId]));
        gate.IsCollectionEnabledAsync(aggregate.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        Assert.Equal(
            ModelEvidenceRetryResult.Cooldown, await maintenance.RetryAsync(new(aggregate.Id, "GitHub:https://github.com", connection), [aggregate.ClientId]));
        retained.LastModelEvidenceRetryAt = null;
        await db.SaveChangesAsync();
        Assert.Equal(
            ModelEvidenceRetryResult.Unreconstructable,
            await maintenance.RetryAsync(new(aggregate.Id, "GitHub:https://github.com", connection), [aggregate.ClientId]));
    }

    [Fact]
    public async Task LostBestEffortNotificationStillIndexesCertifiedZeroAndPreservesIncompleteHistory()
    {
        await using var db =
            new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 42, 1);
        db.ReviewJobs.Add(job);
        var file = new ReviewFileResult(job.Id, "a.cs");
        file.RecordModelEvidence(
            ModelOpportunityEvaluatorTests.Proof(
                [
                    ModelOpportunityEvaluatorTests.Pass("A"), ModelOpportunityEvaluatorTests.Pass("B") with
                    {
                        Disposition = ModelGenerationDisposition.Failed, CandidateSetComplete = false
                    }
                ]) with
                {
                    JobId = job.Id, ClientId = job.ClientId,
                    RepositoryId = job.RepositoryId, PullRequestId = 42, ProviderScope = "AzureDevOps:https://dev.azure.com", RevisionKey = "1"
                });
        db.ReviewFileResults.Add(file);
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var collector = new CodeInsightReviewExposureCollector(db, gate, NullLogger<CodeInsightReviewExposureCollector>.Instance);
        var maintenance = new ModelEvidenceMaintenance(db, gate, collector);
        Assert.Equal(1, await maintenance.SweepAsync(10));
        var aggregate = Assert.Single(db.CodeInsightPullRequests);
        Assert.Equal(job.ClientId, aggregate.ClientId);
        Assert.Equal("A", Assert.Single(db.CodeInsightReviewExposures).ModelId);
        Assert.Empty(db.CodeInsightHarvestCoverage);
        Assert.Equal(0, await maintenance.SweepAsync(10));
        Assert.Single(db.CodeInsightReviewExposures);
    }
}
