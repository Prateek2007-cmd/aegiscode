// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class ThreadUpdatedEventFactoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeConcernOriginRemainsNullableAndIndependentOfTheObservationTime(bool published)
    {
        var origin = published ? (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-3) : null;
        var anchor = new ReviewConcernAnchor
        {
            RootCommentId = "1", OriginAt = origin, FilePath = "original.cs", Side = "RIGHT",
            StartLine = 12, EndLine = 12, CommitSha = "original-head", OriginalScopeProven = published
        };
        var thread = new PrCommentThread(
            "thread", "tracked.cs", 99, [new("human", "Concern", PublishedAt: origin)],
            IsDeleted: true, ConcernAnchor: anchor);
        var result = ThreadUpdatedEventFactory.Build(
            Guid.NewGuid(), Guid.NewGuid(), "repo", 1, thread,
            ThreadOwnershipResolver.None, DateTimeOffset.UtcNow, "GitHub:https://github.com");
        Assert.Equal(anchor, result.ConcernAnchor);
        Assert.Equal(origin, result.ConcernAnchor!.OriginAt);
        Assert.True(result.IsDeleted);
    }
}
