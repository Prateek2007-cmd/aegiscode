// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class CodeInsightProviderFeedbackReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryForgejoUsesItsCurrentReadersAndPreservesReviewerCompleteness(bool reviewerFailure)
    {
        var threads = Substitute.For<IProviderPullRequestFetcher>();
        var statuses = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        threads.Provider.Returns(ScmProvider.Forgejo);
        statuses.Provider.Returns(ScmProvider.Forgejo);
        var target = Target(ScmProvider.Forgejo);
        threads.FetchThreadsAsync(
                target.ProviderScopePath, target.ProviderProjectKey, target.Key.RepositoryId, 1, target.Key.ClientId, Arg.Any<CancellationToken>())
            .Returns([new PrCommentThread("human", "file.cs", 2, [new PrThreadComment("Human", "Check this operation.")], "Fixed")]);
        if (reviewerFailure)
        {
            statuses.GetReviewerThreadStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("unavailable"));
        }
        else
        {
            statuses.GetReviewerThreadStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        }

        var reader = new CodeInsightProviderFeedbackReader([threads], [statuses]);
        var feedback = await reader.ReadAsync(target, ThreadOwnershipResolver.None, mode: CodeInsightObservationMode.Current);

        Assert.Single(feedback.Threads);
        Assert.Equal(!reviewerFailure, feedback.ReviewerEnumerationComplete);
        await threads.DidNotReceiveWithAnyArgs().FetchFeedbackThreadsAsync(default!, default!, default!, 0, default);
        await statuses.DidNotReceiveWithAnyArgs().GetFeedbackStatusesAsync(default!, default!, default!, 0, default!, default);
    }

    [Fact]
    public async Task StrictForgejoDoesNotInvokeUncorrelatedReaders()
    {
        var threads = Substitute.For<IProviderPullRequestFetcher>();
        var statuses = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        threads.Provider.Returns(ScmProvider.Forgejo);
        statuses.Provider.Returns(ScmProvider.Forgejo);
        var reader = new CodeInsightProviderFeedbackReader([threads], [statuses]);

        await Assert.ThrowsAsync<MeisterDev.ProPR.Application.Interfaces.CodeInsightProviderIdentityException>(() =>
            reader.ReadAsync(Target(ScmProvider.Forgejo), ThreadOwnershipResolver.None));

        await threads.DidNotReceiveWithAnyArgs().FetchThreadsAsync(default!, default!, default!, 0);
        await statuses.DidNotReceiveWithAnyArgs().GetReviewerThreadStatusesAsync(default!, default!, default!, 0, default!, default);
    }

    private static CodeInsightProviderEvidenceTarget Target(ScmProvider provider) =>
        new(new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1), provider, "https://forge.example.test", "project", "Forgejo:https://forge.example.test");
}
