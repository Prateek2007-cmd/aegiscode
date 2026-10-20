// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     Applies the execution authority's decision to a queued review job before a worker or a runner claims it. The
///     in-process worker and the runner lease offer call it at the same point, after the native burst and budget
///     admission, so a review is asked about the same way wherever it runs.
/// </summary>
public static class QueuedReviewAdmission
{
    /// <summary>
    ///     Asks the execution authority about <paramref name="job" /> and records its decision. A hold uses the timed
    ///     admission hold, which the worker releases once the delay has passed, so the job is asked about again. A
    ///     refusal ends the job without an AI call and posts the authority's text on the pull request.
    /// </summary>
    /// <param name="admission">The admission service.</param>
    /// <param name="job">The queued review job.</param>
    /// <param name="jobs">The store the caller works with.</param>
    /// <param name="notice">
    ///     Resolves what posts a refusal on the pull request. It is resolved only when a refusal is posted, because a
    ///     host composed without source control registers it with dependencies it cannot satisfy.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The decision that was applied. Only an allowed job may be claimed.</returns>
    public static async Task<ExecutionAdmissionResult> AdmitQueuedReviewAsync(
        this IExecutionAdmission admission,
        ReviewJob job,
        IReviewJobExecutionStore jobs,
        Func<IReviewAdmissionNotice?> notice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(notice);
        if (!admission.IsActive)
        {
            return ExecutionAdmissionResult.Allowed;
        }

        var result = await admission.AdmitReviewAsync(job, jobs, ct);
        switch (result.Outcome)
        {
            case AdmissionOutcome.Allow:
                break;

            case AdmissionOutcome.Hold:
                await jobs.SetAdmissionHeldAsync(job.Id, result.RetryAt!.Value, ct);
                break;

            default:
                // Only a job no caller has claimed is refused, so a refusal that arrives after another worker or a
                // runner claimed the job leaves the running review alone. The statement also decides which caller
                // ended the job, so a refusal is posted once even when a runner offer and the worker weigh the same
                // job at the same time.
                if (await jobs.SetQueuedAdmissionRefusedAsync(job.Id, result.Message!, ct) && notice() is { } poster)
                {
                    await poster.PostAsync(job, result.Message!, null, ct);
                }

                break;
        }

        return result;
    }
}
