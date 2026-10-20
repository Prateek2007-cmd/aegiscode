// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>Asks the registered execution authority whether an execution may start.</summary>
public interface IExecutionAdmission
{
    /// <summary>Whether an execution authority is registered. Without one, every execution is allowed.</summary>
    bool IsActive { get; }

    /// <summary>
    ///     Asks whether a queued review job may be claimed. A pull request author the provider reports is recorded on
    ///     the job through <paramref name="jobs" />, so later admissions of the same job do not query the provider.
    /// </summary>
    /// <param name="job">The queued review job.</param>
    /// <param name="jobs">The store the caller works with, used to record the pull request author.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ExecutionAdmissionResult> AdmitReviewAsync(ReviewJob job, IReviewJobExecutionStore jobs, CancellationToken ct = default);

    /// <summary>Asks whether a pending thread pass may begin its next attempt.</summary>
    /// <param name="job">The pending thread pass.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ExecutionAdmissionResult> AdmitThreadPassAsync(ThreadPassJob job, CancellationToken ct = default);

    /// <summary>Asks whether a claimed mention reply may be answered.</summary>
    /// <param name="job">The claimed mention reply.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ExecutionAdmissionResult> AdmitMentionReplyAsync(MentionReplyJob job, CancellationToken ct = default);
}
