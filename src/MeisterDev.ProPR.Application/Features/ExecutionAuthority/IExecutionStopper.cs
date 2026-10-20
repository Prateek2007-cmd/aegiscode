// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>Ends an execution that the execution authority stopped.</summary>
public interface IExecutionStopper
{
    /// <summary>
    ///     Records the stop on the execution. A stopped review is marked stopped in the same way an administrator's
    ///     stop marks it, so the lease heartbeat ends the review wherever it runs, in this process or on a runner.
    ///     Thread passes and mention replies run in this process, and the services that run them record the stop
    ///     when the next AI call is refused.
    /// </summary>
    /// <param name="execution">The stopped execution.</param>
    /// <param name="message">The text the execution authority gave with the stop.</param>
    /// <param name="ct">The cancellation token.</param>
    Task StopAsync(ExecutionIdentity execution, string message, CancellationToken ct = default);
}
