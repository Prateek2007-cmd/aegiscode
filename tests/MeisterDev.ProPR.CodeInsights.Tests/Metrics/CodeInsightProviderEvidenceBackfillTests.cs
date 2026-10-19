// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Workers;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class CodeInsightProviderEvidenceBackfillTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepairRequiredSourceFailuresDetermineTheAggregateRegardlessOfOrder(bool repairFirst, bool missingCoordinates)
    {
        await using var h = new Harness();
        var other = new ReviewJob(Guid.NewGuid(), h.Job.ClientId, "https://ado.example.test/other", "project", "repo", 1, 1);
        h.Db.Add(other);
        await h.Db.SaveChangesAsync();
        var repair = new CodeInsightHistoricalRefreshSource(
            "AzureDevOps:https://ado.example.test/other",
            missingCoordinates ? null : other.Id, CodeInsightHistoricalGap.None);
        h.Claims(h.Claim with { Sources = repairFirst ? [repair, h.Claim.Sources[0]] : [h.Claim.Sources[0], repair] });
        h.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.ArgAt<CodeInsightProviderEvidenceTarget>(0).ProviderScope == repair.ProviderScope
                ? new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable,
                    UnavailableReason: CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable,
                    Stage: CodeInsightObservationStage.ConnectionLookup)
                : new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable,
                    UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed, Stage: CodeInsightObservationStage.ProviderRead));

        Assert.Equal(new CodeInsightProviderEvidenceBackfillResult(1, 0, 1, 0), await h.Sut.SweepAsync(1));

        var reason = missingCoordinates
            ? CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable
            : CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable;
        await h.Store.Received(1).RecordResultAsync(
            h.Claim.AggregateId, h.Claim.AttemptId,
            Arg.Is<CodeInsightObservationResult>(result => result.UnavailableReason == reason),
            Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await h.Observer.Received(missingCoordinates ? 1 : 2).RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ObservationWithoutTimestampLogsItsAcknowledgmentFailure()
    {
        await using var h = new Harness();
        h.Claims(h.Claim);
        h.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightObservationResult(CodeInsightObservationStatus.Observed));
        var logger = new RecoveryCapturingLogger<CodeInsightProviderEvidenceBackfill>();
        using var services = new ServiceCollection().AddSingleton(h.Store).AddSingleton(h.Db).AddSingleton(h.Gate).AddSingleton(h.Observer)
            .AddSingleton(LocalScmPolicies.Registry).AddSingleton(Monitor(new CodeInsightsOptions()))
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<CodeInsightProviderEvidenceBackfill>>(logger).BuildServiceProvider();
        var sut = ActivatorUtilities.CreateInstance<CodeInsightProviderEvidenceBackfill>(services);
        Assert.Equal(new CodeInsightProviderEvidenceBackfillResult(1, 0, 1, 0), await sut.SweepAsync(1));
        var log = Assert.Single(logger.Entries, entry => entry.Fields.ContainsKey("ProviderScope"));
        Assert.Equal(CodeInsightObservationStage.ObservationAcknowledgment, log.Fields["Stage"]);
        Assert.Equal(CodeInsightObservationUnavailableReason.EvidenceRetentionFailed, log.Fields["Reason"]);
        Assert.Equal(h.Claim.Key.PullRequestId, log.Fields["PullRequestId"]);
        Assert.Null(log.Exception);
        await h.Store.Received(1).RecordResultAsync(
            h.Claim.AggregateId, h.Claim.AttemptId,
            Arg.Is<CodeInsightObservationResult>(result => result.Stage == CodeInsightObservationStage.ObservationAcknowledgment),
            Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WholeSweepDeadlineCancelsObservationAndWaitsForCleanupWithoutAnotherClaim()
    {
        await using var h = new Harness();
        h.Claims(h.Claim, h.Claim with { AggregateId = Guid.NewGuid(), AttemptId = Guid.NewGuid() });
        var clock = new DeadlineClock(DateTimeOffset.UtcNow);
        using var abort = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
                return new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, clock.GetUtcNow());
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await release.Task;
                throw;
            }
            finally
            {
                terminated.SetResult();
            }
        });
        var sut = new CodeInsightProviderEvidenceBackfill(
            h.Store, h.Db, h.Gate, h.Observer, LocalScmPolicies.Registry,
            Monitor(new CodeInsightsOptions { ProviderEvidenceSweepBudgetSeconds = 10 }), timeProvider: clock);
        var sweep = sut.SweepAsync(2, abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Same(cancelled.Task, await Task.WhenAny(cancelled.Task, Task.Delay(1000)));
            await Task.Delay(30);
            Assert.False(sweep.IsCompleted);
            release.SetResult();
            Assert.Equal(new CodeInsightProviderEvidenceBackfillResult(1, 0, 1, 0), await sweep);
            await h.Store.Received(1).ClaimAsync(1, Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await h.Store.Received(1).RecordResultAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Is<CodeInsightObservationResult>(r =>
                    r.Status == CodeInsightObservationStatus.Unavailable &&
                    r.UnavailableReason == CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded),
                Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            abort.Cancel();
            release.TrySetResult();
            try
            {
                await sweep;
            }
            catch (OperationCanceledException)
            {
            }

            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateObserverReturnAfterLeaseDeadlineCannotCompleteOrObserveTheNextScope(bool faults)
    {
        await using var h = new Harness();
        var clock = new DeadlineClock(DateTimeOffset.UtcNow);
        var other = new ReviewJob(Guid.NewGuid(), h.Job.ClientId, "https://ado.example.test/other", "project", "repo", 1, 1);
        h.Db.Add(other);
        await h.Db.SaveChangesAsync();
        h.Claims(
            h.Claim with
            {
                LeaseUntil = clock.GetUtcNow().AddMinutes(1),
                Sources =
                [
                    h.Claim.Sources[0],
                    new CodeInsightHistoricalRefreshSource("AzureDevOps:https://ado.example.test/other", other.Id, CodeInsightHistoricalGap.HarvestCoverage)
                ],
            }, h.Claim with { AggregateId = Guid.NewGuid(), AttemptId = Guid.NewGuid() });
        h.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            if (faults)
            {
                throw new InvalidOperationException("Provider read failed after cancellation.");
            }

            return new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, clock.GetUtcNow());
        });
        var sut = new CodeInsightProviderEvidenceBackfill(
            h.Store, h.Db, h.Gate, h.Observer, LocalScmPolicies.Registry,
            Monitor(new CodeInsightsOptions()), timeProvider: clock);

        Assert.Equal(new CodeInsightProviderEvidenceBackfillResult(1, 0, 1, 0), await sut.SweepAsync(2));

        await h.Observer.Received(1).RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>());
        await h.Store.Received(1).ClaimAsync(1, Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await h.Store.DidNotReceive().RecordResultAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Is<CodeInsightObservationResult>(r => r.Status == CodeInsightObservationStatus.Observed),
            Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlowEarlierCandidateDoesNotConsumeTheLaterCandidatesLease()
    {
        await using var h = new Harness();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var claims = 0;
        h.Store.ClaimAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            claims++;
            var now = call.ArgAt<DateTimeOffset>(1);
            return claims <= 2
                ? new[] { h.Claim with { AggregateId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), AttemptedAt = now, LeaseUntil = now.AddMinutes(1) } }
                : [];
        });
        var observed = 0;
        h.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            observed++;
            clock.Now = clock.Now.AddSeconds(45);
            return new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, clock.Now);
        });
        var sut = new CodeInsightProviderEvidenceBackfill(
            h.Store, h.Db, h.Gate, h.Observer,
            LocalScmPolicies.Registry, Monitor(new CodeInsightsOptions { ProviderEvidenceLeaseMinutes = 1 }), timeProvider: clock);
        await sut.SweepAsync(2);
        Assert.Equal(2, observed);
        Assert.Equal(2, claims);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedJobsObserveEveryScopeAndOnlyThenCompleteTheClaim(bool noAutomaticGaps)
    {
        await using var h = new Harness();
        var other = new ReviewJob(Guid.NewGuid(), h.Job.ClientId, "https://ado.example.test/other", "project", "repo", 1, 1);
        h.Db.Add(other);
        await h.Db.SaveChangesAsync();
        var claim = h.Claim with
        {
            Gaps = noAutomaticGaps ? CodeInsightHistoricalGap.None : h.Claim.Gaps,
            Sources =
            [
                h.Claim.Sources[0] with { Gaps = noAutomaticGaps ? CodeInsightHistoricalGap.None : h.Claim.Sources[0].Gaps },
                new CodeInsightHistoricalRefreshSource(
                    "AzureDevOps:https://ado.example.test/other", other.Id,
                    noAutomaticGaps ? CodeInsightHistoricalGap.None : CodeInsightHistoricalGap.HarvestCoverage),
            ],
        };
        h.Claims(claim);
        Assert.Equal(1, (await h.Sut.SweepAsync(7)).Observed);
        await h.Store.Received(2).ClaimAsync(1, Arg.Any<DateTimeOffset>(), TimeSpan.FromMinutes(15), Arg.Any<CancellationToken>());
        await h.Observer.Received(2).RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>());
        await h.Store.Received(1).RecordResultAsync(
            claim.AggregateId, claim.AttemptId,
            Arg.Is<CodeInsightObservationResult>(r => r.Status == CodeInsightObservationStatus.Observed && r.ObservedAt != null),
            Arg.Any<DateTimeOffset>(), TimeSpan.FromHours(6), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialScopeFailureCannotCompleteAndDoesNotBlockAnotherCandidate()
    {
        await using var h = new Harness();
        var claim = h.Claim with
        {
            Sources = [h.Claim.Sources[0], new CodeInsightHistoricalRefreshSource("missing", null, CodeInsightHistoricalGap.HarvestCoverage)],
        };
        var other = h.Claim with { AggregateId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        h.Claims(claim, other);
        Assert.Equal(1, (await h.Sut.SweepAsync(2)).Observed);
        await h.Store.Received(1).RecordResultAsync(
            claim.AggregateId, claim.AttemptId,
            Arg.Is<CodeInsightObservationResult>(r => r.Status == CodeInsightObservationStatus.Unavailable
                                                      && r.UnavailableReason == CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable),
            Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledAfterClaimSkipsProviderAndEvidenceWrites()
    {
        await using var h = new Harness();
        h.Claims(h.Claim);
        h.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        Assert.Equal(0, (await h.Sut.SweepAsync(1)).Observed);
        await h.Observer.DidNotReceiveWithAnyArgs().RefreshAsync(default!);
    }

    [Fact]
    public async Task ExpiredCandidateDoesNotStartSourceWork()
    {
        await using var h = new Harness();
        h.Claims(h.Claim with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.Equal(0, (await h.Sut.SweepAsync(1)).Observed);
        await h.Observer.DidNotReceiveWithAnyArgs().RefreshAsync(default!);
    }

    [Fact]
    public void EnvironmentLimitIsIndependentAndHasFiniteBounds()
    {
        var options = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(
            options,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                    { ["CODE_INSIGHTS_PROVIDER_EVIDENCE_BACKFILL_MAX_PULL_REQUESTS"] = "9", ["CODE_INSIGHTS_BACKFILL_MAX_JOBS"] = "2" }).Build());
        Assert.Equal(9, options.EffectiveProviderEvidenceBackfillMaxPullRequests);
        Assert.Equal(2, options.EffectiveBackfillMaxJobs);
        options.ProviderEvidenceBackfillMaxPullRequests = int.MaxValue;
        Assert.Equal(250, options.EffectiveProviderEvidenceBackfillMaxPullRequests);
        options.ProviderEvidenceLeaseMinutes = 0;
        options.ProviderEvidenceRetryCooldownMinutes = int.MaxValue;
        Assert.Equal(TimeSpan.FromMinutes(1), options.ProviderEvidenceLease);
        Assert.Equal(TimeSpan.FromDays(1), options.ProviderEvidenceRetryCooldown);
    }

    [Fact]
    public void ProviderSweepBudgetBindsIndependentlyAndHasFiniteBounds()
    {
        var options = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(
            options,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["CODE_INSIGHTS_PROVIDER_EVIDENCE_SWEEP_BUDGET_SECONDS"] = "17" }).Build());
        Assert.Equal(TimeSpan.FromSeconds(17), options.ProviderEvidenceSweepBudget);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ProviderEvidenceLease);
        options.ProviderEvidenceSweepBudgetSeconds = 0;
        Assert.Equal(TimeSpan.FromSeconds(10), options.ProviderEvidenceSweepBudget);
        options.ProviderEvidenceSweepBudgetSeconds = int.MaxValue;
        Assert.Equal(TimeSpan.FromMinutes(10), options.ProviderEvidenceSweepBudget);
    }

    [Fact]
    public async Task ActualWorkerDelegatesProviderSweepAndIsolatesItsFailure()
    {
        var providerSweep = Substitute.For<ICodeInsightProviderEvidenceBackfill>();
        providerSweep.SweepAsync(9, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("provider offline"));
        var projector = Substitute.For<ICodeInsightRollupProjector>();
        var seals = Substitute.For<ICodeInsightSealSweeper>();
        var steps = new List<string>();
        projector.BackfillAsync(3, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("project");
            return 0;
        });
        seals.SweepAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("seal");
            return 0;
        });
        providerSweep.SweepAsync(9, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("provider");
            return Task.FromException<CodeInsightProviderEvidenceBackfillResult>(new InvalidOperationException("provider offline"));
        });
        var services = new ServiceCollection().AddSingleton(providerSweep).AddSingleton(projector).AddSingleton(seals);
        using var sp = services.BuildServiceProvider();
        var options = Monitor(new CodeInsightsOptions { ProviderEvidenceBackfillMaxPullRequests = 9, BackfillMaxJobs = 3 });
        var worker = new CodeInsightCatchUpWorker(sp.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<CodeInsightCatchUpWorker>.Instance);
        await worker.CatchUpOnceAsync(CancellationToken.None);
        await providerSweep.Received(1).SweepAsync(9, Arg.Any<CancellationToken>());
        await projector.Received(1).BackfillAsync(3, Arg.Any<CancellationToken>());
        await seals.Received(1).SweepAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "project", "seal", "provider" }, steps);
    }

    private static IOptionsMonitor<CodeInsightsOptions> Monitor(CodeInsightsOptions value)
    {
        var monitor = Substitute.For<IOptionsMonitor<CodeInsightsOptions>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return this.Now;
        }
    }

    private sealed class DeadlineClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<DeadlineTimer> _timers = [];
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => this._now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timer.Change(dueTime, period);
            this._timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            this._now += elapsed;
            foreach (var timer in this._timers.ToArray())
            {
                timer.FireIfDue();
            }
        }

        private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                this._due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }

            internal void FireIfDue()
            {
                if (this._due is not null && this._due <= clock.GetUtcNow())
                {
                    this._due = null;
                    callback(state);
                }
            }

            public void Dispose() => this._due = null;

            public ValueTask DisposeAsync()
            {
                this.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly CodeInsightHistoricalRefreshCandidate Claim;

        internal readonly MeisterProPRDbContext Db = new(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        internal readonly ICodeInsightsCollectionGate Gate = Substitute.For<ICodeInsightsCollectionGate>();
        internal readonly ReviewJob Job;
        internal readonly ICodeInsightHistoricalObserver Observer = Substitute.For<ICodeInsightHistoricalObserver>();
        internal readonly ICodeInsightHistoricalRefreshStore Store = Substitute.For<ICodeInsightHistoricalRefreshStore>();
        internal readonly CodeInsightProviderEvidenceBackfill Sut;

        internal Harness()
        {
            this.Job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://ado.example.test/collection", "project", "repo", 1, 1);
            this.Db.Add(this.Job);
            this.Db.SaveChanges();
            var now = DateTimeOffset.UtcNow;
            this.Claim = new CodeInsightHistoricalRefreshCandidate(
                Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(15), new CodeInsightPullRequestKey(this.Job.ClientId, "repo", 1),
                CodeInsightHistoricalGap.NativeOutcome,
                [
                    new CodeInsightHistoricalRefreshSource(
                        "AzureDevOps:https://ado.example.test/collection", this.Job.Id, CodeInsightHistoricalGap.NativeOutcome),
                ]);
            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Observer.RefreshAsync(Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, now));
            this.Store.RecordResultAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CodeInsightObservationResult>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()).Returns(true);
            this.Sut = new CodeInsightProviderEvidenceBackfill(
                this.Store, this.Db, this.Gate, this.Observer, LocalScmPolicies.Registry, Monitor(new CodeInsightsOptions()));
        }

        public ValueTask DisposeAsync()
        {
            return this.Db.DisposeAsync();
        }

        internal void Claims(params CodeInsightHistoricalRefreshCandidate[] candidates)
        {
            var queue = new Queue<CodeInsightHistoricalRefreshCandidate>(candidates);
            this.Store.ClaimAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(_ => queue.Count == 0 ? [] : new[] { queue.Dequeue() });
        }
    }
}
