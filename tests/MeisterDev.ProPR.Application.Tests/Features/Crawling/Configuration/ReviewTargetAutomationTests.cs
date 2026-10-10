// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Configuration;

public sealed class ReviewTargetAutomationTests
{
    [Fact]
    public async Task Pause_RequiresSavedOwnershipAndRevisionWithoutProviderAuthority()
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var target = Target();
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>())
            .Returns(target, target with { IsActive = false, ReviewTargetRevision = 2 });
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        repository.ChangeReviewTargetAutomationUnderAdmissionAsync(target.Id, target.ClientId, 1, false, Arg.Any<CancellationToken>()).Returns(true);

        var result = await new ReviewTargetAutomationService(repository, authority).ChangeAsync(target.ClientId, target.Id, 1, false, null);

        Assert.Equal(ReviewTargetAutomationResult.Changed, result.Status);
        Assert.False(result.SavedTarget!.IsActive);
        Assert.Equal(2, result.SavedTarget.ReviewTargetRevision);
        await authority.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
    }

    [Theory]
    [InlineData(2L, 1L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task StaleOrOverflowRevision_DoesNotWriteOrReadProviderAuthority(long savedRevision, long expectedRevision)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var target = Target() with { ReviewTargetRevision = savedRevision };
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>()).Returns(target);
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());

        Assert.Equal(
            ReviewTargetAutomationResult.Conflict,
            (await new ReviewTargetAutomationService(repository, authority).ChangeAsync(target.ClientId, target.Id, expectedRevision, true, Guid.NewGuid()))
            .Status);
        await repository.DidNotReceiveWithAnyArgs().ChangeReviewTargetAutomationUnderAdmissionAsync(default, default, default, default, default);
        await authority.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
    }

    [Fact]
    public async Task Enable_AuthorityDriftUnderAdmissionRefusesWrite()
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var target = Target();
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>()).Returns(target);
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), target.ClientId, target.Provider, target.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        authority.ResolveAsync(target, connection.Id, Arg.Any<CancellationToken>()).Returns(new ScheduledReviewAuthority(connection, [], null, target));
        authority.IsCurrentAsync(Arg.Any<ScheduledReviewAuthority>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(
            ReviewTargetAutomationResult.Unavailable,
            (await new ReviewTargetAutomationService(repository, authority).ChangeAsync(target.ClientId, target.Id, 1, true, connection.Id)).Status);
        await repository.DidNotReceiveWithAnyArgs().ChangeReviewTargetAutomationUnderAdmissionAsync(default, default, default, default, default);
    }

    private static CrawlConfigurationDto Target() => new(
        Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.com", "owner", 60, true,
        DateTimeOffset.UtcNow, [new(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto("github", "123"))]);

    [Fact]
    public async Task Enable_PropagatesSelectedConnectionAndWritesAfterCurrentAuthority()
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var target = Target() with { IsActive = false };
        var selected = Guid.NewGuid();
        var connection = new ClientScmConnectionDto(
            selected, target.ClientId, target.Provider, target.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var evidence = new ScheduledReviewAuthority(connection, [], null, target);
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>())
            .Returns(target, target with { IsActive = true, ReviewTargetRevision = 2 });
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        authority.ResolveAsync(target, selected, Arg.Any<CancellationToken>()).Returns(evidence);
        authority.IsCurrentAsync(evidence, Arg.Any<CancellationToken>()).Returns(true);
        repository.ChangeReviewTargetAutomationUnderAdmissionAsync(target.Id, target.ClientId, 1, true, Arg.Any<CancellationToken>()).Returns(true);

        Assert.Equal(
            ReviewTargetAutomationResult.Changed,
            (await new ReviewTargetAutomationService(repository, authority).ChangeAsync(target.ClientId, target.Id, 1, true, selected)).Status);
        await authority.Received(1).ResolveAsync(target, selected, Arg.Any<CancellationToken>());
        await repository.Received(1).AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>());
        await repository.Received(1).ChangeReviewTargetAutomationUnderAdmissionAsync(target.Id, target.ClientId, 1, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enable_CancellationDuringAuthorityCheckDoesNotPersistActivity()
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var target = Target();
        using var cancellation = new CancellationTokenSource();
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>()).Returns(target);
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        authority.ResolveAsync(target, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns<Task<ScheduledReviewAuthority>>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReviewTargetAutomationService(repository, authority).ChangeAsync(
            target.ClientId, target.Id, 1, true, Guid.NewGuid(), cancellation.Token));
        await repository.DidNotReceiveWithAnyArgs().ChangeReviewTargetAutomationUnderAdmissionAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Pause_CapturesConfirmedSnapshotBeforeReleasingAdmission()
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var original = Target();
        var current = original;
        var admissionHeld = false;
        repository.AcquireReviewTargetAdmissionAsync(original.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            admissionHeld = true;
            return new Lease(() =>
            {
                admissionHeld = false;
                current = current with { IsActive = true, ReviewTargetRevision = 3 };
            });
        });
        repository.GetReviewTargetPolicySnapshotAsync(original.Id, original.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(admissionHeld);
            return current;
        });
        repository.ChangeReviewTargetAutomationUnderAdmissionAsync(original.Id, original.ClientId, 1, false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(admissionHeld);
            current = current with { IsActive = false, ReviewTargetRevision = 2 };
            return true;
        });

        var result = await new ReviewTargetAutomationService(repository, authority).ChangeAsync(original.ClientId, original.Id, 1, false, null);

        Assert.Equal(ReviewTargetAutomationResult.Changed, result.Status);
        Assert.Equal(2, result.SavedTarget!.ReviewTargetRevision);
        Assert.False(result.SavedTarget.IsActive);
        Assert.False(admissionHeld);
        Assert.Equal(3, current.ReviewTargetRevision);
        await repository.Received(2).GetReviewTargetPolicySnapshotAsync(original.Id, original.ClientId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revision")]
    [InlineData("activity")]
    [InlineData("client")]
    public async Task ChangedWrite_RequiresMatchingSavedSnapshot(string mismatch)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        var target = Target();
        var saved = target with { IsActive = false, ReviewTargetRevision = 2 };
        saved = mismatch switch
        {
            "missing" => null,
            "revision" => saved with { ReviewTargetRevision = 3 },
            "activity" => saved with { IsActive = true },
            _ => saved with { ClientId = Guid.NewGuid() },
        };
        repository.GetReviewTargetPolicySnapshotAsync(target.Id, target.ClientId, Arg.Any<CancellationToken>()).Returns(target, saved);
        repository.AcquireReviewTargetAdmissionAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        repository.ChangeReviewTargetAutomationUnderAdmissionAsync(target.Id, target.ClientId, 1, false, Arg.Any<CancellationToken>()).Returns(true);

        var result = await new ReviewTargetAutomationService(repository, Substitute.For<IScheduledReviewAuthorityService>())
            .ChangeAsync(target.ClientId, target.Id, 1, false, null);

        Assert.Equal(ReviewTargetAutomationResult.Conflict, result.Status);
        Assert.Null(result.SavedTarget);
    }

    private sealed class Lease(Action? released = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            released?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
