// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class HistoricalDiscussionLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedHistoricalReadRenewsItsLeaseAndStopsOnLostOwnership(bool loseOwnership)
    {
        var clock = new FakeTimeProvider();
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), key.ClientId, ScmProvider.AzureDevOps, "https://dev.azure.com/org",
            ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, clock.GetUtcNow(), clock.GetUtcNow());
        var scope = ProviderSourceIdentity.FromReviewSource(
            connection.ProviderFamily, connection.HostBaseUrl,
            LocalScmPolicies.Registry.GetSourceIdentityPolicy(connection.ProviderFamily)).Value;
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(key.ClientId, Arg.Any<CancellationToken>()).Returns([connection]);
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(key.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var lease = Substitute.For<IDiscussionSynchronizationLease>();
        lease.State.Returns(new DiscussionSynchronizationState());
        lease.ObservedAt.Returns(clock.GetUtcNow());
        var renewals = 0;
        var expires = clock.GetUtcNow().AddMinutes(2);
        var replaced = false;
        lease.EnsureCurrentAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (replaced || clock.GetUtcNow() >= expires)
            {
                throw new InvalidOperationException("Lease lost");
            }

            expires = clock.GetUtcNow().AddMinutes(2);
            Interlocked.Increment(ref renewals);
            return Task.CompletedTask;
        });
        store.TryAcquireAsync(
            Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(lease);
        var feedback = Substitute.For<ICodeInsightProviderFeedbackReader>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CodeInsightProviderFeedback>(TaskCreationOptions.RunContinuationsAsynchronously);
        feedback.ReadAsync(
            Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
            CodeInsightObservationMode.HistoricalBackfill).Returns(call =>
        {
            started.TrySetResult();
            return release.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        var origins = Substitute.For<IPostedCommentOriginStore>();
        origins.GetOriginsForProviderSourceAsync(
                key.ClientId, key.RepositoryId, key.PullRequestId, connection.ProviderFamily, scope, Arg.Any<CancellationToken>())
            .Returns(new PostedCommentOriginSourceSnapshot([], []));
        var findings = Substitute.For<ICodeInsightFindingStore>();
        findings.GetNativeFeedbackFindingsAsync(key, scope, Arg.Any<CancellationToken>()).Returns([]);
        findings.GetHistoricalMissThreadIdsAsync(key, scope, Arg.Any<CancellationToken>()).Returns([]);
        var coverage = Substitute.For<ICodeInsightHarvestCoverageRecorder>();
        coverage.RecordAcknowledgedAsync(key, scope, Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightEvidenceAcknowledgment(true));
        var observer = new PullRequestCloseObserver(
            LocalScmPolicies.Registry, NullLogger<PullRequestCloseObserver>.Instance,
            collectionGate: gate, scmConnectionRepository: connections, findingStore: findings, postedCommentOriginStore: origins,
            feedbackReader: feedback, missHarvester: Substitute.For<ICodeInsightMissHarvester>(),
            dispositionService: Substitute.For<ICodeInsightDispositionService>(),
            harvestCoverageRecorder: coverage, discussionStore: store, timeProvider: clock);
        var running = observer.RefreshAsync(new(key, connection.ProviderFamily, connection.HostBaseUrl, "project", scope, connection.Id));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var before = Volatile.Read(ref renewals);
                clock.Advance(TimeSpan.FromSeconds(30));
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                while (Volatile.Read(ref renewals) <= before)
                {
                    await Task.Delay(5, wait.Token);
                }
            }

            Assert.False(running.IsCompleted);
            if (loseOwnership)
            {
                replaced = true;
                clock.Advance(TimeSpan.FromSeconds(30));
                await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Empty(coverage.ReceivedCalls());
            }
            else
            {
                release.TrySetResult(new([], []));
                Assert.Equal(CodeInsightObservationStatus.Observed, (await running).Status);
            }
        }
        finally
        {
            release.TrySetResult(new([], []));
            try
            {
                await running;
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
