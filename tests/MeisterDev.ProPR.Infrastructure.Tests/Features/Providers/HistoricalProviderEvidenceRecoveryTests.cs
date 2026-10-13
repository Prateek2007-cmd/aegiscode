// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

[Collection("PostgresIntegration1")]
public sealed class HistoricalProviderEvidenceRecoveryTests(PostgresContainerFixture fixture)
{
    private const string Scope = "AzureDevOps:https://ado.example.test/collection";
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-30);

    [Fact]
    public async Task CancelledHistoricalSweepWaitsForProviderCleanupBeforeReturning()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        h.Gate.IsCollectionEnabledAsync(h.Clients[1], Arg.Any<CancellationToken>()).Returns(false);
        using var cancellation = new CancellationTokenSource();
        // Cleanup must terminate the provider stub even if caller cancellation stops reaching it.
        using var cleanupCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                var token = c.ArgAt<CancellationToken>(5);
                Assert.True(token.CanBeCanceled);
                using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, cleanupCancellation.Token);
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, providerCancellation.Token);
                    return FreshThreads(false);
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled.SetResult();
                        await release.Task;
                    }

                    throw;
                }
                finally
                {
                    terminated.SetResult();
                }
            });
        var sweep = h.Backfill.SweepAsync(2, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
            Assert.False(sweep.IsCompleted, "The shared scope must remain active until the cancelled provider call terminates.");
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            cleanupCancellation.Cancel();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.Null((await h.Db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id)).LastHistoricalRefreshCompletedAt);
        Assert.Null((await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == h.FindingId)).NativeStatus);
        Assert.Empty(await h.Db.CodeInsightHarvestCoverage.Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalOwnershipUsesTheRetainedPublicationReceiptWithoutAnOriginRow(bool commentReceipt)
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        if (!commentReceipt)
        {
            var finding = await h.Db.CodeInsightFindings.SingleAsync(f => f.Id == h.FindingId);
            finding.ProviderCommentId = null;
            await h.Db.SaveChangesAsync();
        }

        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => FreshThreads(c.ArgAt<Guid>(4) == pr.ClientId));
        h.Statuses.GetFeedbackStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(c => c.ArgAt<ThreadOwnershipResolver>(4).OwnsThread(new ThreadCommentRef("own", "1", null, "retired account"))
                ? [new PrThreadStatusEntry("own", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Changed)]
                : Array.Empty<PrThreadStatusEntry>());

        Assert.Equal(2, (await h.Backfill.SweepAsync(25)).Observed);

        Assert.Single(await h.Db.CodeInsightMisses.Where(m => m.CodeInsightPullRequestId == pr.Id).ToListAsync());
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await h.Split.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("system")]
    [InlineData("owned")]
    [InlineData("summary")]
    public async Task IrrelevantHistoricalThreadsDoNotNeedResolutionEvidence(string kind)
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        if (kind == "owned")
        {
            var finding = await h.Db.CodeInsightFindings.SingleAsync(f => f.Id == h.FindingId);
            await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "irrelevant", "2", finding.JobId, Old)]);
        }

        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => c.ArgAt<Guid>(4) != pr.ClientId
                ? FreshThreads(false)
                : new List<PrCommentThread>(FreshThreads(false))
                {
                    new(
                        "irrelevant", "src/irrelevant.cs", 5,
                        [
                            new PrThreadComment(
                                "Bot", kind == "summary" ? HarvestedThreadEligibility.SummaryPrefix + " Review complete" : "Provider activity", CommentId: 2,
                                IsSystemGenerated: kind == "system")
                        ], "unsupported"),
                });

        Assert.Equal(2, (await h.Backfill.SweepAsync(25)).Observed);

        var retainedCoverage = await h.Db.CodeInsightHarvestCoverage.Where(c => h.Aggregates.Select(p => p.Id).Contains(c.CodeInsightPullRequestId))
            .ToListAsync();
        Assert.Equal(2, retainedCoverage.Count);
        Assert.All(retainedCoverage, coverage => Assert.True(coverage.AllHumanThreadsResolved));
        await h.ProjectAll();
        var score = (await h.Reader.QueryAsync(h.Query, h.Clients)).Views[0].Series[0].Points[^1].Score.Summary;
        Assert.NotNull(score.Precision);
        Assert.NotNull(score.Recall);
        Assert.NotNull(score.F1);

        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InconsistentHistoricalReadsFailBeforeAnySourceWrites()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        var job = await h.Db.ReviewJobs.SingleAsync(j => j.ClientId == pr.ClientId);
        await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "own", "1", job.Id, Old)]);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => FreshThreads(c.ArgAt<Guid>(4) == pr.ClientId, ownStatus: "Active"));

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(1, result.Unavailable);
        Assert.Equal(1, result.Observed);
        Assert.Null((await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == h.FindingId)).NativeStatus);
        Assert.Null((await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id)).SourceFingerprint);
        Assert.Empty(await h.Db.CodeInsightHarvestCoverage.Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync());
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("missing-id")]
    [InlineData("thread-local")]
    [InlineData("changed")]
    public async Task CommentOnlyReceiptNeedsUniquePrScopedCorrelationAndCompletesIdempotently(string proof)
    {
        await using var h = await Harness.Create(fixture, proof == "thread-local" ? ScmProvider.AzureDevOps : ScmProvider.GitHub);
        var pr = h.Aggregates[0];
        var finding = await h.Db.CodeInsightFindings.SingleAsync(f => f.Id == h.FindingId);
        finding.ProviderThreadId = null;
        finding.ProviderCommentId = proof == "missing-id" ? "0" : "501";
        await h.Db.SaveChangesAsync();
        var initial = JsonSerializer.Serialize(await h.Db.CodeInsightFindingDispositions.AsNoTracking().SingleAsync(d => d.CodeInsightFindingId == finding.Id));
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                if (c.ArgAt<Guid>(4) != pr.ClientId)
                {
                    return FreshThreads(false);
                }

                if (proof == "changed")
                {
                    finding.ProviderCommentId = "999";
                    await h.Db.SaveChangesAsync();
                }

                var rows = new List<PrCommentThread>(FreshThreads(false))
                {
                    new(
                        "own-native", "src/own.cs", 1,
                        [
                            new PrThreadComment(
                                "Bot", "Handle the input validation failure.", CommentId: proof == "missing" ? 502 : proof == "missing-id" ? 0 : 501)
                        ], "Closed"),
                };
                if (proof == "ambiguous")
                {
                    rows.Add(
                        new("other-native", "src/other.cs", 1, [new PrThreadComment("Bot", "Handle the input validation failure.", CommentId: 501)], "Closed"));
                }

                return (IReadOnlyList<PrCommentThread>)rows;
            });
        h.Statuses.GetFeedbackStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("own-native", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Changed)
            ]);

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(proof == "unique" ? 2 : 1, result.Observed);
        Assert.Equal(proof == "unique" ? 0 : 1, result.Unavailable);
        var retained = await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == finding.Id);
        Assert.Equal(proof == "unique" ? "own-native" : null, retained.ProviderThreadId);
        Assert.Equal(
            initial, JsonSerializer.Serialize(await h.Db.CodeInsightFindingDispositions.AsNoTracking().SingleAsync(d => d.CodeInsightFindingId == finding.Id)));
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await h.Split.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        if (proof == "unique")
        {
            h.Threads.ClearReceivedCalls();
            h.Statuses.ClearReceivedCalls();
            Assert.Equal(0, (await h.Backfill.SweepAsync(25)).Attempted);
            Assert.Empty(h.Threads.ReceivedCalls());
            Assert.Empty(h.Statuses.ReceivedCalls());
        }
    }

    [Fact]
    public async Task ChangedReceiptDuringProviderReadCannotCreateAnInitialDisposition()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        var finding = await h.Db.CodeInsightFindings.SingleAsync(f => f.Id == h.FindingId);
        h.Db.CodeInsightFindingDispositions.RemoveRange(
            await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == finding.Id).ToListAsync());
        await h.Db.SaveChangesAsync();
        await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "own", "1", finding.JobId, Old)]);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                if (c.ArgAt<Guid>(4) == pr.ClientId)
                {
                    finding.ProviderCommentId = "changed";
                    await h.Db.SaveChangesAsync();
                }

                return FreshThreads(false);
            });

        var result = await h.Backfill.SweepAsync(25);

        Assert.Empty(await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == finding.Id).ToListAsync());
        Assert.Equal(1, result.Unavailable);
        Assert.Equal(1, result.Observed);
        Assert.Null((await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == finding.Id)).NativeStatus);
        await h.Split.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("scope")]
    [InlineData("newer-source")]
    public async Task HistoricalFallbackCannotCreateAnInitialDispositionAfterItsReceiptOrSourceChangesDuringJudging(string mutation)
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        var finding = await h.Db.CodeInsightFindings.SingleAsync(f => f.Id == h.FindingId);
        h.Db.CodeInsightFindingDispositions.RemoveRange(
            await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == finding.Id).ToListAsync());
        await h.Db.SaveChangesAsync();
        h.Statuses.GetFeedbackStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(
                [new PrThreadStatusEntry("own", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Unchanged)]);
        h.Split.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (mutation == "receipt")
            {
                finding.ProviderCommentId = "changed";
            }
            else if (mutation == "scope")
            {
                finding.ProviderScope = "AzureDevOps:https://ado.example.test/foreign";
            }
            else
            {
                finding.NativeStatus = "Closed";
                finding.CurrentDisposition = CodeInsightDisposition.FalsePositive;
                finding.CurrentClassifierVersion = "newer-live-judge";
                finding.CurrentClassifierConfidence = .99;
                finding.CurrentCodeChange = ThreadAnchorCodeChange.Unchanged;
                finding.OutcomeSourceFingerprint = "newer-live-source";
                finding.OutcomeObservedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            }

            await h.Db.SaveChangesAsync();
            return new DisregardedFindingJudgement(true, .9, "Valid concern");
        });

        var result = await h.Backfill.SweepAsync(25);

        Assert.Empty(await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == finding.Id).ToListAsync());
        Assert.Equal(mutation == "newer-source" ? 2 : 1, result.Observed);
        var retained = await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == finding.Id);
        if (mutation == "newer-source")
        {
            Assert.Equal(CodeInsightDisposition.FalsePositive, retained.CurrentDisposition);
            Assert.Equal("newer-live-source", retained.OutcomeSourceFingerprint);
        }
        else
        {
            Assert.Null(retained.NativeStatus);
        }

        await h.Split.Received(1).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnchangedHistoricalFailedFallbackWithoutInitialDispositionStopsAtThreeModelAttempts()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        h.Gate.IsCollectionEnabledAsync(h.Clients[1], Arg.Any<CancellationToken>()).Returns(false);
        h.Db.CodeInsightFindingDispositions.RemoveRange(
            await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == h.FindingId).ToListAsync());
        await h.Db.SaveChangesAsync();
        h.Statuses.GetFeedbackStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(
                [new PrThreadStatusEntry("own", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Unchanged)]);
        h.Split.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns((DisregardedFindingJudgement?)null);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await h.Db.Entry(pr).ReloadAsync();
            pr.HistoricalRefreshNotBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
            await h.Db.SaveChangesAsync();
            Assert.Equal(1, (await h.Backfill.SweepAsync(1)).Unavailable);
        }

        await h.Split.Received(3).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(3, (await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == h.FindingId)).OutcomeJudgementAttempts);
        Assert.Empty(await h.Db.CodeInsightFindingDispositions.Where(d => d.CodeInsightFindingId == h.FindingId).ToListAsync());
        Assert.Equal(
            "ClassificationUnavailable", (await h.Db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id)).LastHistoricalRefreshReason);
    }

    private static IReadOnlyList<PrCommentThread> FreshThreads(bool own, string ownStatus = "Closed")
    {
        var threads = new List<PrCommentThread>
        {
            new("human", "src/human.cs", 2, [new PrThreadComment("Jane", "Check synchronization before changing the shared state.", CommentId: 601)], "Fixed"),
        };
        if (own)
        {
            threads.Add(new("own", "src/own.cs", 1, [new PrThreadComment("Bot", "Handle the input validation failure.", CommentId: 1)], ownStatus));
        }

        return threads;
    }

    [Fact]
    public async Task FullHistoricalPipelineRefreshesAMissOnlySourceWithoutExposures()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[1];
        await h.Db.CodeInsightReviewExposures.Where(e => e.CodeInsightPullRequestId == pr.Id).ExecuteDeleteAsync();
        h.Db.Add(new ReviewJob(Guid.NewGuid(), pr.ClientId, "https://ado.example.test/other", "project", pr.RepositoryId, 1, 1));
        await h.Db.SaveChangesAsync();

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(2, result.Observed);
        Assert.NotNull((await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id)).SourceFingerprint);
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalOwnershipUsesCapturedReviewAndMentionNamespaces(bool mention)
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        var ownJob = (await h.Db.ReviewJobs.AsNoTracking().SingleAsync(j => j.ClientId == pr.ClientId)).Id;
        var foreignJob = Guid.NewGuid();
        if (mention)
        {
            ownJob = Guid.NewGuid();
            h.Db.AddRange(
                new MentionReplyJob(ownJob, pr.ClientId, "https://ado.example.test/collection", "project", pr.RepositoryId, 1, "own", 1, "@reviewer explain"),
                new MentionReplyJob(foreignJob, pr.ClientId, "https://ado.example.test/other", "project", pr.RepositoryId, 1, "human", 1, "@reviewer explain"));
        }
        else
        {
            h.Db.Add(new ReviewJob(foreignJob, pr.ClientId, "https://ado.example.test/other", "project", pr.RepositoryId, 1, 1));
        }

        await h.Db.SaveChangesAsync();
        await h.Origins.RecordAsync(
        [
            new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "own", "1", ownJob, Old),
            new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "human", "1", foreignJob, Old),
            new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "orphan", "3", Guid.NewGuid(), Old),
        ]);
        var ownershipProof = new List<(bool Own, bool Foreign, bool Orphan)>();
        h.Statuses.GetFeedbackStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<Guid>(5) == pr.ClientId)
                {
                    var ownership = call.ArgAt<ThreadOwnershipResolver>(4);
                    ownershipProof.Add(
                        (ownership.OwnsComment(new ThreadCommentRef("own", "1", null, "retired account")),
                            ownership.OwnsComment(new ThreadCommentRef("human", "1", null, "Jane")),
                            ownership.OwnsComment(new ThreadCommentRef("orphan", "3", null, "Jane"))));
                }

                return new[]
                {
                    new PrThreadStatusEntry("own", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Changed)
                };
            });

        var result = await h.Backfill.SweepAsync(25);

        Assert.NotEmpty(ownershipProof);
        Assert.All(ownershipProof, proof => Assert.Equal((true, false, false), proof));
        Assert.Equal(2, result.Observed);
        Assert.Single(ownershipProof);
        Assert.False((await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id)).ExcludedAsOwnFinding);
    }

    [Fact]
    public async Task HistoricalOwnedMissExclusionCompletesWithoutAHumanFingerprint()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        var job = await h.Db.ReviewJobs.AsNoTracking().SingleAsync(j => j.ClientId == pr.ClientId);
        await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "human", "1", job.Id, Old)]);

        var first = await h.Backfill.SweepAsync(25);

        Assert.Equal(2, first.Observed);
        var miss = await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id);
        Assert.True(miss.ExcludedAsOwnFinding);
        Assert.Null(miss.SourceFingerprint);
        h.Statuses.ClearReceivedCalls();
        h.Threads.ClearReceivedCalls();
        Assert.Equal(0, (await h.Backfill.SweepAsync(25)).Attempted);
        Assert.Empty(h.Statuses.ReceivedCalls());
        Assert.Empty(h.Threads.ReceivedCalls());
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EligibilityOnlyLegacyExclusionRefreshesACompletePopulationAndReconcilesLiveMissCounts()
    {
        await using var h = await Harness.Create(fixture);
        Assert.Equal(2, (await h.Backfill.SweepAsync(25)).Observed);
        await h.ProjectAll();
        Assert.Equal(2, (await h.Reader.QueryAsync(h.Query, h.Clients)).Views[0].Series[0].Points[^1].Score.Counts.ActedMisses);
        var pr = h.Aggregates[0];
        var before = await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id);
        var priorCoverage = await h.Db.CodeInsightHarvestCoverage.AsNoTracking().SingleAsync(c => c.CodeInsightPullRequestId == pr.Id);
        Assert.True(priorCoverage.EnumerationComplete);
        var latest = await h.Db.CodeInsightThreadEligibility.AsNoTracking()
            .SingleAsync(e => e.CodeInsightPullRequestId == pr.Id && e.ProviderThreadId == "human");
        var misses = new CodeInsightMissStore(h.Db, Substitute.For<ISecretProtectionCodec>());
        await misses.ObserveThreadEligibilityAsync(
            new(pr.ClientId, pr.RepositoryId, pr.PullRequestId), "human", "", true,
            latest.SourceObservedAt.AddTicks(TimeSpan.TicksPerMicrosecond), mode: CodeInsightObservationMode.HistoricalBackfill);
        var job = await h.Db.ReviewJobs.AsNoTracking().SingleAsync(j => j.ClientId == pr.ClientId);
        await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "human", "601", job.Id, Old)]);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => FreshThreads(c.ArgAt<Guid>(4) == pr.ClientId));
        h.Statuses.ClearReceivedCalls();
        h.Threads.ClearReceivedCalls();

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Observed);
        Assert.Equal(0, result.Unavailable);
        var retained = await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.Id == before.Id);
        Assert.True(retained.ExcludedAsOwnFinding);
        Assert.False(retained.CountsAsMiss);
        Assert.Equal(before.EncryptedDiscussion, retained.EncryptedDiscussion);
        Assert.Equal(before.IsSubstantive, retained.IsSubstantive);
        Assert.Equal(before.WasActedOn, retained.WasActedOn);
        Assert.Equal(before.IsInScope, retained.IsInScope);
        Assert.Equal(before.ClassifierVersion, retained.ClassifierVersion);
        Assert.Equal(before.DimensionClassifierVersion, retained.DimensionClassifierVersion);
        var coverage = await h.Db.CodeInsightHarvestCoverage.AsNoTracking().SingleAsync(c => c.CodeInsightPullRequestId == pr.Id);
        Assert.True(coverage.EnumerationComplete);
        Assert.True(coverage.ObservedAt > priorCoverage.ObservedAt);
        await h.ProjectAll();
        Assert.Equal(1, (await h.Reader.QueryAsync(h.Query, h.Clients)).Views[0].Series[0].Points[^1].Score.Counts.ActedMisses);
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await h.Split.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        h.Statuses.ClearReceivedCalls();
        h.Threads.ClearReceivedCalls();
        Assert.Equal(0, (await h.Backfill.SweepAsync(25)).Attempted);
        Assert.Empty(h.Statuses.ReceivedCalls());
        Assert.Empty(h.Threads.ReceivedCalls());
    }

    [Fact]
    public async Task HistoricalUnverifiableOriginMatchingAFreshCommentRemainsUnavailable()
    {
        await using var h = await Harness.Create(fixture);
        var pr = h.Aggregates[0];
        await h.Origins.RecordAsync([new PostedCommentOriginEntry(pr.ClientId, pr.RepositoryId, 1, "human", "1", Guid.NewGuid(), Old)]);

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(1, result.Unavailable);
        Assert.Equal(1, result.Observed);
        var miss = await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id);
        Assert.False(miss.ExcludedAsOwnFinding);
        Assert.Null(miss.SourceFingerprint);
        Assert.Equal("OwnershipUnavailable", (await h.Db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id)).LastHistoricalRefreshReason);
        Assert.Empty(await h.Db.CodeInsightHarvestCoverage.AsNoTracking().Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync());
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("preserve")]
    [InlineData("new")]
    [InlineData("failed")]
    public async Task FullHistoricalObservationPreservesRetentionThroughMissWritesAndRemainsPurgeable(string mode)
    {
        await using var h = await Harness.Create(fixture);
        if (mode != "preserve")
        {
            h.Threads.FetchFeedbackThreadsAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(
                [
                    new PrCommentThread(
                        "human", "src/human.cs", 2,
                        [new PrThreadComment("Jane", "Check synchronization before changing the shared state.", CommentId: 1)], "Fixed"),
                    new PrCommentThread(
                        "new-human", "src/new.cs", 3,
                        [new PrThreadComment("Jane", "Validate the boundary before indexing the buffer.", CommentId: 2)], "Fixed"),
                ]);
            h.Human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
                .Returns(mode == "failed" ? null : new HumanMissJudgement(true, true, true, .9, "Valid concern"));
        }

        await h.Backfill.SweepAsync(25);

        var retained = await h.Db.CodeInsightPullRequests.AsNoTracking().Where(pr => h.Clients.Contains(pr.ClientId)).ToListAsync();
        Assert.Equal(2, retained.Count);
        Assert.All(retained, pr => Assert.Equal(Old.ToUnixTimeMilliseconds(), pr.LastActivityAt.ToUnixTimeMilliseconds()));
        Assert.All(retained, pr => Assert.Equal(Old.ToUnixTimeMilliseconds(), pr.UpdatedAt.ToUnixTimeMilliseconds()));
        Assert.Equal(2, await h.Findings.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Empty(await h.Db.CodeInsightPullRequests.Where(pr => h.Clients.Contains(pr.ClientId)).ToListAsync());
        Assert.Equal(2, await h.Db.ReviewJobs.CountAsync(job => h.Clients.Contains(job.ClientId)));
    }

    [Fact]
    public async Task PurgeDuringHistoricalProviderReadDoesNotRecreateTheRetainedAggregate()
    {
        await using var h = await Harness.Create(fixture);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await h.Findings.PurgeForClientAsync(call.ArgAt<Guid>(4));
                return (IReadOnlyList<PrCommentThread>)new List<PrCommentThread>
                {
                    new(
                        "human", "src/human.cs", 2,
                        [new PrThreadComment("Jane", "Check synchronization before changing the shared state.", CommentId: 1)], "Fixed"),
                };
            });

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(0, result.Observed);
        Assert.Empty(await h.Db.CodeInsightPullRequests.Where(pr => h.Clients.Contains(pr.ClientId)).ToListAsync());
        Assert.Empty(await h.Db.CodeInsightHarvestCoverage.Where(c => h.Aggregates.Select(pr => pr.Id).Contains(c.CodeInsightPullRequestId)).ToListAsync());
        Assert.Equal(2, await h.Db.ReviewJobs.CountAsync(job => h.Clients.Contains(job.ClientId)));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("legacy")]
    [InlineData("mixed")]
    public async Task RealHistoricalPipelineRecoversAllClientScoresAndPreservesReportsSealsAndJudgments(string namespaces)
    {
        await using var h = await Harness.Create(fixture);
        if (namespaces != "canonical")
        {
            foreach (var finding in await h.Db.CodeInsightFindings.Where(f => f.Id == h.FindingId).ToListAsync())
            {
                finding.ProviderScope = "";
            }

            foreach (var exposure in await h.Db.CodeInsightReviewExposures.Where(e => h.Aggregates.Select(p => p.Id).Contains(e.CodeInsightPullRequestId))
                         .ToListAsync())
            {
                if (namespaces == "legacy" || exposure.CodeInsightPullRequestId != h.Aggregates[0].Id)
                {
                    exposure.ProviderScope = "";
                }
            }

            foreach (var miss in await h.Db.CodeInsightMisses.Where(m => h.Aggregates.Select(p => p.Id).Contains(m.CodeInsightPullRequestId)).ToListAsync())
            {
                miss.ProviderScope = "";
            }

            await h.Db.SaveChangesAsync();
        }

        await h.ProjectAll();
        var before = await h.Reader.QueryAsync(h.Query, h.Clients);
        Assert.Null(before.Views[0].Series[0].Points[^1].Score.Summary.F1);
        var request = new ReviewerPerformanceSaveReportRequest(Guid.NewGuid(), "Retained report", h.Query);
        var reports = new ReviewerPerformanceReportStore(h.Db, h.Reader);
        var saved = await reports.SaveAsync(request, h.Clients);
        var sealBefore = JsonSerializer.Serialize(
            await h.Db.CodeInsightPullRequestMetrics.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == h.Aggregates[0].Id));
        var dispositionBefore = JsonSerializer.Serialize(
            await h.Db.CodeInsightFindingDispositions.AsNoTracking().SingleAsync(d => d.CodeInsightFindingId == h.FindingId));
        var missBefore = await h.Db.CodeInsightMisses.AsNoTracking().Where(m => h.Aggregates.Select(p => p.Id).Contains(m.CodeInsightPullRequestId))
            .ToListAsync();

        var result = await h.Backfill.SweepAsync(25);

        Assert.Equal(2, result.Attempted);
        Assert.Equal(2, result.Observed);
        Assert.Equal(0, result.Unavailable);
        var aggregateIds = h.Aggregates.Select(p => p.Id).ToArray();
        Assert.All(
            await h.Db.CodeInsightReviewExposures.AsNoTracking().Where(e => aggregateIds.Contains(e.CodeInsightPullRequestId)).ToListAsync(),
            exposure => Assert.Equal(Scope, exposure.ProviderScope));
        Assert.Equal(Scope, (await h.Db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == h.FindingId)).ProviderScope);
        Assert.Equal(2, await h.Db.CodeInsightHarvestCoverage.CountAsync(c => aggregateIds.Contains(c.CodeInsightPullRequestId)));
        Assert.All(
            await h.Db.CodeInsightPullRequests.AsNoTracking().Where(p => aggregateIds.Contains(p.Id)).ToListAsync(), pr =>
            {
                Assert.Equal(Old.ToUnixTimeMilliseconds(), pr.LastActivityAt.ToUnixTimeMilliseconds());
                Assert.Equal(Old.ToUnixTimeMilliseconds(), pr.UpdatedAt.ToUnixTimeMilliseconds());
            });
        await h.ProjectAll();
        var after = await h.Reader.QueryAsync(h.Query, h.Clients);
        var score = after.Views[0].Series[0].Points[^1].Score;
        Assert.NotNull(score.Summary.Precision);
        Assert.NotNull(score.Summary.Recall);
        Assert.NotNull(score.Summary.F1);
        Assert.Equal(1, score.Counts.Outcomes.Positive);
        Assert.Equal(2, score.Counts.ActedMisses);
        Assert.Null(h.Query.Views[0].ClientIds);
        Assert.Equal(
            sealBefore,
            JsonSerializer.Serialize(
                await h.Db.CodeInsightPullRequestMetrics.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == h.Aggregates[0].Id)));
        Assert.Equal(
            dispositionBefore,
            JsonSerializer.Serialize(await h.Db.CodeInsightFindingDispositions.AsNoTracking().SingleAsync(d => d.CodeInsightFindingId == h.FindingId)));
        Assert.Equal(JsonSerializer.Serialize(saved.Response), JsonSerializer.Serialize((await reports.GetAsync(request.Id, h.Clients))!.Response));
        foreach (var oldMiss in missBefore)
        {
            var miss = await h.Db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.Id == oldMiss.Id);
            Assert.Equal(Scope, miss.ProviderScope);
            Assert.Equal(oldMiss.EncryptedDiscussion, miss.EncryptedDiscussion);
            Assert.Equal(oldMiss.LastJudgedAt, miss.LastJudgedAt);
            Assert.Equal(oldMiss.ClassifierVersion, miss.ClassifierVersion);
            Assert.Equal(oldMiss.DimensionClassifierVersion, miss.DimensionClassifierVersion);
            Assert.NotNull(miss.SourceFingerprint);
        }

        await h.Split.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        await h.Human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        h.Statuses.ClearReceivedCalls();
        h.Threads.ClearReceivedCalls();
        var replay = await h.Backfill.SweepAsync(25);
        Assert.Equal(0, replay.Attempted);
        await h.Statuses.DidNotReceiveWithAnyArgs().GetFeedbackStatusesAsync(default!, default!, default!, 0, default!, default);
        await h.Threads.DidNotReceiveWithAnyArgs().FetchFeedbackThreadsAsync(default!, default!, default!, 0, default);
    }

    [Fact]
    public async Task RealUnavailableProviderCannotMakeHistoricalRecallAvailable()
    {
        await using var h = await Harness.Create(fixture);
        h.Threads.FetchFeedbackThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("provider unavailable"));
        var result = await h.Backfill.SweepAsync(25);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(0, result.Observed);
        Assert.Equal(2, result.Unavailable);
        await h.ProjectAll();
        Assert.Null((await h.Reader.QueryAsync(h.Query, h.Clients)).Views[0].Series[0].Points[^1].Score.Summary.F1);
        var ids = h.Aggregates.Select(p => p.Id).ToArray();
        Assert.DoesNotContain(
            await h.Db.CodeInsightPullRequests.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(),
            p => p.LastHistoricalRefreshCompletedAt is not null);
        Assert.DoesNotContain(
            await h.Db.CodeInsightHarvestCoverage.AsNoTracking().Where(c => ids.Contains(c.CodeInsightPullRequestId)).ToListAsync(),
            c => c.EnumerationComplete);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly List<CodeInsightPullRequest> Aggregates = [];
        internal readonly Guid[] Clients = [Guid.NewGuid(), Guid.NewGuid()];
        internal readonly MeisterProPRDbContext Db;
        internal readonly ICodeInsightsCollectionGate Gate = Substitute.For<ICodeInsightsCollectionGate>();
        internal readonly IHumanMissClassifier Human = Substitute.For<IHumanMissClassifier>();
        internal readonly ReviewerPerformanceCountProjector Projector;

        internal readonly ReviewerPerformanceQuery Query = new()
        {
            Views =
            [
                new ReviewerPerformanceViewQuery { From = DateOnly.FromDateTime(Old.AddDays(-1).UtcDateTime), To = DateOnly.FromDateTime(DateTime.UtcNow) },
            ],
        };

        internal readonly ReviewerPerformanceRangeReader Reader;
        internal readonly IDisregardedFindingClassifier Split = Substitute.For<IDisregardedFindingClassifier>();
        internal readonly IProviderReviewerThreadStatusFetcher Statuses = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        internal readonly IProviderPullRequestFetcher Threads = Substitute.For<IProviderPullRequestFetcher>();
        private readonly Guid _tenant = Guid.NewGuid();
        internal CodeInsightProviderEvidenceBackfill Backfill = null!;
        internal CodeInsightFindingStore Findings = null!;
        internal PostedCommentOriginStore Origins = null!;
        internal Guid FindingId;

        private Harness(string connection)
        {
            this.Db = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, o => o.UseVector()).Options);
            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => this.Clients.Contains(c.ArgAt<Guid>(0)));
            this.Reader = new ReviewerPerformanceRangeReader(this.Db);
            this.Projector = new ReviewerPerformanceCountProjector(this.Db, this.Gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.Db.ReviewerPerformanceReports.Where(r => r.Clients.Any(c => this.Clients.Contains(c.ClientId))).ExecuteDeleteAsync();
                await this.Db.ReviewJobs.Where(j => this.Clients.Contains(j.ClientId)).ExecuteDeleteAsync();
                await this.Db.MentionReplyJobs.Where(j => this.Clients.Contains(j.ClientId)).ExecuteDeleteAsync();
                await this.Db.PostedCommentOrigins.Where(o => this.Clients.Contains(o.ClientId)).ExecuteDeleteAsync();
                await this.Db.Clients.Where(c => this.Clients.Contains(c.Id)).ExecuteDeleteAsync();
                await this.Db.Tenants.Where(t => t.Id == this._tenant).ExecuteDeleteAsync();
            }
            finally
            {
                await this.Db.DisposeAsync();
            }
        }

        internal static async Task<Harness> Create(PostgresContainerFixture fixture, ScmProvider provider = ScmProvider.AzureDevOps)
        {
            Assert.True(fixture.IsAvailable, "Historical recovery verification requires PostgreSQL.");
            var h = new Harness(fixture.ConnectionString);
            var path = provider == ScmProvider.GitHub ? "https://github.example.test" : "https://ado.example.test/collection";
            var scope = provider == ScmProvider.GitHub ? "GitHub:https://github.example.test" : Scope;
            var codec = Substitute.For<ISecretProtectionCodec>();
            codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(c => c.ArgAt<string>(0));
            codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(c => c.ArgAt<string>(0));
            h.Db.Add(new TenantRecord { Id = h._tenant, Slug = h._tenant.ToString("N"), DisplayName = "Historical recovery", CreatedAt = Old });
            foreach (var client in h.Clients)
            {
                h.Db.Add(new ClientRecord { Id = client, TenantId = h._tenant, DisplayName = "Historical recovery", CreatedAt = Old });
            }

            await h.Db.SaveChangesAsync();
            var findings = new CodeInsightFindingStore(h.Db, codec);
            h.Findings = findings;
            var misses = new CodeInsightMissStore(h.Db, codec);
            foreach (var (client, index) in h.Clients.Select((id, index) => (id, index)))
            {
                var pr = new CodeInsightPullRequest
                {
                    Id = Guid.NewGuid(), ClientId = client, RepositoryId = "recovery-" + client,
                    PullRequestId = 1, CreatedAt = Old, LastActivityAt = Old, UpdatedAt = Old, PullRequestState = "Completed",
                };
                h.Aggregates.Add(pr);
                var job = new ReviewJob(
                    new CodeReviewSourceContext(provider, path, "project", "project", CodeReviewPlatformKind.PullRequest, "1"),
                    Guid.NewGuid(), client, path, "project", pr.RepositoryId, 1, 1);
                h.Db.AddRange(
                    pr, job, new CodeInsightReviewExposure
                    {
                        Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id,
                        JobId = job.Id, FilePath = "src/human.cs", RevisionKey = "old", ProviderScope = scope, Source = "runtime", ObservedAt = Old,
                    });
                if (index == 0)
                {
                    var finding = new CodeInsightFinding
                    {
                        Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = job.Id,
                        RevisionKey = "old", FilePath = "src/own.cs", LineNumber = 1, ProviderScope = scope,
                        ProviderThreadId = "own", ProviderCommentId = "1", PublicationState = CodeInsightPublicationState.Published,
                        EncryptedMessage = "Handle the input validation failure.", ObservedAt = Old, CreatedAt = Old, ClassifiedAt = Old,
                    };
                    h.FindingId = finding.Id;
                    h.Db.Add(finding);
                    h.Db.Add(
                        new CodeInsightPullRequestMetric
                        {
                            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = client,
                            RepositoryId = pr.RepositoryId, PullRequestId = 1, CloseState = "Completed", SealedAt = Old,
                            SealedOn = DateOnly.FromDateTime(Old.UtcDateTime), AddressedCount = 3, MissCount = 7,
                        });
                }

                await h.Db.SaveChangesAsync();
                if (index == 0)
                {
                    await findings.RecordDispositionAsync(
                        h.FindingId, new CodeInsightDispositionRecord(
                            CodeInsightDisposition.Addressed,
                            ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null));
                }

                await misses.RecordMissAsync(
                    new CodeInsightPullRequestKey(client, pr.RepositoryId, 1), new CodeInsightMissRecord(
                        "human", "src/human.cs", 2,
                        "Jane: Check synchronization before changing the shared state.", true, true, true, .9, "old-human", true,
                        "concurrency", CodeInsightFindingQualifier.Missing, "old-types", .9, SourceObservedAt: Old, ProviderScope: scope));
            }

            h.Statuses.Provider.Returns(provider);
            h.Statuses.GetFeedbackStatusesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    [new PrThreadStatusEntry("own", "Closed", "src/own.cs", "Bot: Handle the input validation failure.", 0, ThreadAnchorCodeChange.Changed)]);
            h.Threads.Provider.Returns(provider);
            h.Threads.FetchFeedbackThreadsAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(
                [
                    new PrCommentThread(
                        "human", "src/human.cs", 2, [new PrThreadComment("Jane", "Check synchronization before changing the shared state.", CommentId: 1)],
                        "Fixed"),
                ]);
            h.Human.ClassifierVersion.Returns("new-human");
            h.Split.ClassifierVersion.Returns("new-split");
            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => new[]
            {
                new ClientScmConnectionDto(
                    Guid.NewGuid(), c.ArgAt<Guid>(0), provider, path,
                    ScmAuthenticationKind.PersonalAccessToken, "ADO", true, "verified", null, null, null, Old, Old),
            });
            var origins = new PostedCommentOriginStore(h.Db, providerRegistry: LocalScmPolicies.Registry);
            h.Origins = origins;
            var disposition = new CodeInsightDispositionService(
                LocalScmPolicies.Registry.CompatibilityCodec, findings, findings, h.Split,
                h.Gate, NullLogger<CodeInsightDispositionService>.Instance, performanceEvidence: new CodeInsightPerformanceEvidenceStore(h.Db));
            var harvester = new CodeInsightMissHarvester(
                LocalScmPolicies.Registry.CompatibilityCodec, findings, misses, h.Human,
                h.Gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance, performanceProjector: h.Projector);
            var observer = new PullRequestCloseObserver(
                LocalScmPolicies.Registry, NullLogger<PullRequestCloseObserver>.Instance,
                missHarvester: harvester, findingStore: findings, collectionGate: h.Gate, scmConnectionRepository: connections,
                postedCommentOriginStore: origins, dispositionService: disposition,
                harvestCoverageRecorder: new CodeInsightHarvestCoverageRecorder(
                    h.Db, h.Gate, h.Projector, NullLogger<CodeInsightHarvestCoverageRecorder>.Instance),
                feedbackReader: new CodeInsightProviderFeedbackReader([h.Threads], [h.Statuses]));
            var options = Substitute.For<IOptionsMonitor<CodeInsightsOptions>>();
            options.CurrentValue.Returns(new CodeInsightsOptions());
            h.Backfill = new CodeInsightProviderEvidenceBackfill(
                new CodeInsightHistoricalRefreshStore(h.Db, h.Gate, providerRegistry: LocalScmPolicies.Registry), h.Db, h.Gate, observer,
                LocalScmPolicies.Registry, options);
            foreach (var pr in h.Aggregates)
            {
                pr.LastActivityAt = Old;
                pr.UpdatedAt = Old;
            }

            await h.Db.SaveChangesAsync();
            return h;
        }

        internal async Task ProjectAll()
        {
            foreach (var pr in this.Aggregates)
            {
                await this.Projector.ProjectPullRequestAsync(pr.Id);
            }
        }
    }
}
