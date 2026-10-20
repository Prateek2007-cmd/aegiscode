// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.ExecutionAuthority;

public sealed class QueuedReviewAdmissionTests
{
    private static readonly DateTimeOffset RetryAt = new(2026, 10, 18, 12, 5, 0, TimeSpan.Zero);
    private readonly IExecutionAdmission _admission = Substitute.For<IExecutionAdmission>();
    private readonly IReviewJobExecutionStore _jobs = Substitute.For<IReviewJobExecutionStore>();
    private readonly IReviewAdmissionNotice _notice = Substitute.For<IReviewAdmissionNotice>();
    private readonly ReviewJob _job = new(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 42, 1);

    public QueuedReviewAdmissionTests()
    {
        this._admission.IsActive.Returns(true);
    }

    [Fact]
    public async Task AHeldReview_WaitsInTheTimedAdmissionHold()
    {
        this._admission.AdmitReviewAsync(this._job, this._jobs, Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionResult.HoldUntil(RetryAt, "author_limit_reached", "The plan's author limit is reached."));

        var result = await this._admission.AdmitQueuedReviewAsync(this._job, this._jobs, () => this._notice);

        Assert.Equal(AdmissionOutcome.Hold, result.Outcome);
        await this._jobs.Received(1).SetAdmissionHeldAsync(this._job.Id, RetryAt, Arg.Any<CancellationToken>());
        Assert.Empty(this._notice.ReceivedCalls());
    }

    [Fact]
    public async Task ARefusedReview_EndsAndPostsTheAuthoritysText()
    {
        this._admission.AdmitReviewAsync(this._job, this._jobs, Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionResult.Refused("repository_not_in_plan", "This repository is not part of the plan."));
        this._jobs.SetQueuedAdmissionRefusedAsync(this._job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await this._admission.AdmitQueuedReviewAsync(this._job, this._jobs, () => this._notice);

        await this._jobs.Received(1).SetQueuedAdmissionRefusedAsync(this._job.Id, "This repository is not part of the plan.", Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceiveWithAnyArgs().SetAdmissionRefusedAsync(default, default!, default, default);
        await this._notice.Received(1).PostAsync(this._job, "This repository is not part of the plan.", null, Arg.Any<CancellationToken>());
    }

    // The store refuses only a job no caller has claimed. Whether another caller recorded the refusal first or
    // claimed the job before the authority answered, this caller posts nothing.
    [Fact]
    public async Task ARefusalAnotherCallerAlreadyRecordedOrOutran_IsNotPosted()
    {
        this._admission.AdmitReviewAsync(this._job, this._jobs, Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionResult.Refused("repository_not_in_plan", "This repository is not part of the plan."));
        this._jobs.SetQueuedAdmissionRefusedAsync(this._job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await this._admission.AdmitQueuedReviewAsync(this._job, this._jobs, () => this._notice);

        Assert.Empty(this._notice.ReceivedCalls());
    }

    [Fact]
    public async Task WithoutAnAuthority_TheReviewIsClaimable()
    {
        this._admission.IsActive.Returns(false);

        var result = await this._admission.AdmitQueuedReviewAsync(this._job, this._jobs, () => this._notice);

        Assert.Equal(AdmissionOutcome.Allow, result.Outcome);
        await this._admission.DidNotReceive().AdmitReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<IReviewJobExecutionStore>(), Arg.Any<CancellationToken>());
    }
}
