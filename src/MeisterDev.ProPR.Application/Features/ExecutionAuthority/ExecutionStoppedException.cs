// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     Thrown before an AI call when the execution authority has stopped the execution the call belongs to. Derives
///     from <see cref="OperationCanceledException" /> for the reason <c>BudgetHardCapReachedException</c> does: the
///     review code paths re-throw cancellation instead of degrading a failed call into a partial result, and the
///     services that run an execution recognize this type to record the stop.
/// </summary>
public sealed class ExecutionStoppedException : OperationCanceledException
{
    /// <summary>Initializes a new instance of the <see cref="ExecutionStoppedException" /> class.</summary>
    /// <param name="execution">The execution that was stopped.</param>
    /// <param name="stopMessage">The text the execution authority gave with the stop.</param>
    public ExecutionStoppedException(ExecutionIdentity execution, string stopMessage)
        : base($"The execution authority stopped this execution: {stopMessage}")
    {
        this.Execution = execution;
        this.StopMessage = stopMessage;
    }

    /// <summary>The execution that was stopped.</summary>
    public ExecutionIdentity Execution { get; }

    /// <summary>The text the execution authority gave with the stop.</summary>
    public string StopMessage { get; }
}
