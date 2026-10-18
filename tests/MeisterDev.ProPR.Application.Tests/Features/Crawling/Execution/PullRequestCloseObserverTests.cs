// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestCloseObserverTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com/org")]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.test")]
    [InlineData(ScmProvider.Forgejo, "https://forge.example.test")]
    public async Task OrdinaryCloseDelegatesCapturedCoordinatesToSharedSynchronization(ScmProvider provider, string scope)
    {
        var harness = new Harness(provider, scope);
        var observer = harness.CreateObserver();

        Assert.True((await observer.ObserveAcknowledgedAsync(harness.Key, scope, "project")).Retained);

        await harness.Discussions.Received(1).ObserveAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request =>
                request.ClientId == harness.Key.ClientId && request.ConnectionId == harness.Connection.Id && request.Provider == provider &&
                request.ProviderScopePath == scope && request.ProviderProjectKey == "project" && request.RepositoryId == harness.Key.RepositoryId &&
                request.PullRequestId == harness.Key.PullRequestId && request.PullRequestStatus == PrStatus.Completed && request.DiscussionOnly &&
                !request.AllowReviewSubmission), Arg.Any<CancellationToken>());
        Assert.Empty(harness.Feedback.ReceivedCalls());
        Assert.Empty(harness.Misses.ReceivedCalls());
    }

    [Theory]
    [InlineData(PrStatus.Completed, true, true)]
    [InlineData(PrStatus.Abandoned, true, true)]
    [InlineData(PrStatus.Active, true, false)]
    [InlineData(PrStatus.Completed, false, false)]
    [InlineData(null, true, false)]
    public async Task AcknowledgmentRequiresRetainedConsumersAndAuthoritativeClosure(PrStatus? status, bool retained, bool expected)
    {
        var harness = new Harness();
        harness.Discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
                    { ObservedPullRequestStatus = status, CurrentConsumersRetained = retained });

        Assert.Equal(expected, (await harness.CreateObserver().ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project")).Retained);
    }

    [Fact]
    public async Task UnavailableSynchronizationCannotAcknowledgeFinalEvidence()
    {
        var harness = new Harness();

        Assert.False((await harness.CreateObserver(withSynchronization: false).ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project")).Retained);

        Assert.Empty(harness.Feedback.ReceivedCalls());
        Assert.Empty(harness.Misses.ReceivedCalls());
    }

    [Fact]
    public async Task UnavailableConnectionRepositoryCannotAcknowledgeFinalEvidence()
    {
        var harness = new Harness();
        var observer = new PullRequestCloseObserver(
            LocalScmPolicies.Registry, NullLogger<PullRequestCloseObserver>.Instance, discussionSynchronization: harness.Discussions);

        Assert.False((await observer.ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project")).Retained);

        Assert.Empty(harness.Discussions.ReceivedCalls());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("different-host")]
    public async Task UnavailableConnectionDoesNotReadProviderEvidence(string unavailable)
    {
        var harness = new Harness();
        harness.Rows = unavailable switch
        {
            "missing" => [],
            "inactive" => [harness.Connection with { IsActive = false }],
            _ => [harness.Connection with { HostBaseUrl = "https://different.example.test" }],
        };

        Assert.False((await harness.CreateObserver().ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project")).Retained);

        Assert.Empty(harness.Discussions.ReceivedCalls());
    }

    [Fact]
    public async Task SameAuthorityConnectionsPreserveMostSpecificConnectionSelection()
    {
        var harness = new Harness();
        var specific = harness.Connection with { Id = Guid.NewGuid(), HostBaseUrl = "https://dev.azure.com/org/specific" };
        harness.Rows = [harness.Connection, specific];

        await harness.CreateObserver().ObserveAsync(harness.Key, harness.Scope, "project");

        await harness.Discussions.Received(1).ObserveAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request => request.ConnectionId == specific.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConflictingProviderFamiliesCannotSelectAConnection()
    {
        var harness = new Harness();
        harness.Rows = [harness.Connection, harness.Connection with { Id = Guid.NewGuid(), ProviderFamily = ScmProvider.GitHub }];

        Assert.False((await harness.CreateObserver().ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project")).Retained);

        Assert.Empty(harness.Discussions.ReceivedCalls());
    }

    [Fact]
    public async Task SynchronizationFailureRemainsVisibleToTheCloseCaller()
    {
        var harness = new Harness();
        harness.Discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("evidence unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateObserver().ObserveAcknowledgedAsync(harness.Key, harness.Scope, "project"));
    }

    [Fact]
    public async Task CallerCancellationReachesSharedSynchronization()
    {
        var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        harness.Discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), cancellation.Token)
            .Returns(_ => Task.FromCanceled<PullRequestThreadSnapshot>(cancellation.Token));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CreateObserver().ObserveAcknowledgedAsync(
            harness.Key, harness.Scope, "project", cancellation.Token));
    }

    private sealed class Harness
    {
        public CodeInsightPullRequestKey Key { get; } = new(Guid.NewGuid(), "repo", 42);
        public string Scope { get; }
        public ClientScmConnectionDto Connection { get; }
        public IReadOnlyList<ClientScmConnectionDto> Rows { get; set; }
        public IDiscussionSynchronizationService Discussions { get; } = Substitute.For<IDiscussionSynchronizationService>();
        public IClientScmConnectionRepository Connections { get; } = Substitute.For<IClientScmConnectionRepository>();
        public ICodeInsightProviderFeedbackReader Feedback { get; } = Substitute.For<ICodeInsightProviderFeedbackReader>();
        public ICodeInsightMissHarvester Misses { get; } = Substitute.For<ICodeInsightMissHarvester>();

        public Harness(ScmProvider provider = ScmProvider.AzureDevOps, string scope = "https://dev.azure.com/org")
        {
            this.Scope = scope;
            this.Connection = new(
                Guid.NewGuid(), this.Key.ClientId, provider, scope, ScmAuthenticationKind.PersonalAccessToken,
                "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            this.Rows = [this.Connection];
            this.Connections.GetByClientIdAsync(this.Key.ClientId, Arg.Any<CancellationToken>()).Returns(_ => this.Rows);
            this.Discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
                        { ObservedPullRequestStatus = PrStatus.Completed, CurrentConsumersRetained = true });
        }

        public PullRequestCloseObserver CreateObserver(bool withSynchronization = true) => new(
            LocalScmPolicies.Registry, NullLogger<PullRequestCloseObserver>.Instance,
            scmConnectionRepository: this.Connections, discussionSynchronization: withSynchronization ? this.Discussions : null,
            missHarvester: this.Misses, feedbackReader: this.Feedback);
    }
}
