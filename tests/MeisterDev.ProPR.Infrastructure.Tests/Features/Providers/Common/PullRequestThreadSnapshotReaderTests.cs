// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.TestSupport;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class PullRequestThreadSnapshotReaderTests
{
    [Fact]
    public async Task AliasedCurrentAndCloseCoordinatesShareOneAuthoritativeReadAndOwnership()
    {
        var adapter = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        adapter.Provider.Returns(ScmProvider.AzureDevOps);
        var owner = Guid.NewGuid();
        adapter.ReadSnapshotAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], []) { AuthenticatedIdentity = new ThreadOwnerIdentity(owner) });
        var reader = new PullRequestThreadSnapshotReader([adapter], LocalScmPolicies.Registry);
        var target = new PullRequestThreadSnapshotTarget(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.AzureDevOps,
            "https://dev.azure.com/ORG/", "Project", "REPO", 42);
        var snapshot = await reader.ReadAsync(target, ThreadOwnershipResolver.None);
        reader.AcknowledgeCurrentConsumers(target, snapshot);
        var feedback = new CodeInsightProviderFeedbackReader([], [adapter], reader);
        var ownership = ThreadOwnershipResolver.None;
        var close = await feedback.ReadAsync(
            new(
                new(target.ClientId, "repo", 42), target.Provider,
                "https://dev.azure.com/org", "project", "AzureDevOps:https://dev.azure.com", target.ConnectionId), ownership,
            mode: CodeInsightObservationMode.Current);
        Assert.True(close.CurrentConsumersRetained);
        Assert.Equal(owner, ownership.Identity.Id);
        await adapter.Received(1).ReadSnapshotAsync(
            "https://dev.azure.com/org", "project", "repo", 42,
            Arg.Any<ThreadOwnershipResolver>(), target.ClientId, Arg.Any<CancellationToken>(), target.ConnectionId);
    }

    [Fact]
    public async Task InvalidatingSourceClearsCapturedConnectionCachesAndPreservesOtherClients()
    {
        var adapter = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        adapter.Provider.Returns(ScmProvider.AzureDevOps);
        var status = "Active";
        adapter.ReadSnapshotAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(_ => new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [new("1", null, null, [], status)], []));
        var reader = new PullRequestThreadSnapshotReader([adapter], LocalScmPolicies.Registry);
        var target = new PullRequestThreadSnapshotTarget(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.AzureDevOps,
            "https://dev.azure.com/org", "project", "repo", 42);
        var otherConnection = target with { ConnectionId = Guid.NewGuid() };
        var otherClient = target with { ClientId = Guid.NewGuid() };
        await reader.ReadAsync(target, ThreadOwnershipResolver.None);
        await reader.ReadAsync(otherConnection, ThreadOwnershipResolver.None);
        await reader.ReadAsync(otherClient, ThreadOwnershipResolver.None);
        status = "Fixed";
        reader.Invalidate(otherConnection);
        Assert.Equal("Fixed", Assert.Single((await reader.ReadAsync(target, ThreadOwnershipResolver.None)).Threads).Status);
        Assert.Equal("Fixed", Assert.Single((await reader.ReadAsync(otherConnection, ThreadOwnershipResolver.None)).Threads).Status);
        Assert.Equal("Active", Assert.Single((await reader.ReadAsync(otherClient, ThreadOwnershipResolver.None)).Threads).Status);
        Assert.Equal(5, adapter.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "ReadSnapshotAsync"));
    }

    [Fact]
    public async Task ClientAndCapturedConnectionRemainIndependentCacheScopes()
    {
        var adapter = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        adapter.Provider.Returns(ScmProvider.AzureDevOps);
        adapter.ReadSnapshotAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], []));
        var reader = new PullRequestThreadSnapshotReader([adapter], LocalScmPolicies.Registry);
        var target = new PullRequestThreadSnapshotTarget(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.AzureDevOps,
            "https://dev.azure.com/org", "project", "repo", 42);
        await reader.ReadAsync(target, ThreadOwnershipResolver.None);
        await reader.ReadAsync(target with { ClientId = Guid.NewGuid() }, ThreadOwnershipResolver.None);
        await reader.ReadAsync(target with { ConnectionId = Guid.NewGuid() }, ThreadOwnershipResolver.None);
        Assert.Equal(3, adapter.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "ReadSnapshotAsync"));
    }
}
