// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Extensibility.Execution;

/// <summary>
///     Decides whether ProPR may run a review, a thread pass or a mention reply, and receives the usage of every AI
///     call ProPR makes. At most one implementation is registered for the whole installation; without one, ProPR
///     runs all work and reports no usage.
/// </summary>
/// <remarks>
///     <para>
///         ProPR calls the authority in the process that runs the work. An implementation that consults another
///         service decides itself how often it does so, for example by caching decisions or by forwarding usage
///         in batches.
///     </para>
///     <para>
///         ProPR resolves the authority from a dependency-injection scope, so it can be registered as a singleton
///         or as a scoped service. ProPR's native budgets keep applying: a budget hold or a budget stop takes
///         effect whatever the authority answers.
///     </para>
/// </remarks>
public interface IExecutionAuthority
{
    /// <summary>
    ///     Decides whether ProPR may start the execution the request describes. ProPR asks before it claims the
    ///     work, and asks again once the delay of a hold has passed.
    /// </summary>
    /// <param name="request">The execution and the facts ProPR knows about it.</param>
    /// <param name="cancellationToken">Cancelled when ProPR stops waiting for the answer.</param>
    /// <returns>Whether to start, hold or refuse the execution.</returns>
    /// <remarks>
    ///     An exception holds the execution for a short delay, after which ProPR asks again. ProPR does not start
    ///     work it could not get a decision for.
    /// </remarks>
    ValueTask<AdmissionDecision> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken);

    /// <summary>
    ///     Receives the usage of one completed AI call. ProPR stores the report before it calls this method, so
    ///     a report that does not arrive can be read again through <see cref="IUsageReportFeed" />.
    /// </summary>
    /// <param name="report">The usage of the call.</param>
    /// <param name="cancellationToken">Cancelled when ProPR stops waiting for the answer.</param>
    /// <returns>
    ///     Whether the execution the call belongs to may continue. A stop prevents the next AI call of that
    ///     execution and ends it; calls that are already running finish.
    /// </returns>
    /// <remarks>An exception is logged and treated as an answer to continue.</remarks>
    ValueTask<UsageAnswer> ReportUsageAsync(UsageReport report, CancellationToken cancellationToken);
}
