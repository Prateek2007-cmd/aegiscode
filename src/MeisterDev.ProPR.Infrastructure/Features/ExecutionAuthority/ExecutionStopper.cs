// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.ExecutionAuthority;

/// <summary>
///     Records a stop of the execution authority on a review job. The job repository runs in a scope of its own,
///     because the stop arrives from an AI call that may complete on one of several concurrent tasks of the same
///     review, and the review's own database context is not safe to share between them.
/// </summary>
public sealed class ExecutionStopper(IServiceScopeFactory scopes) : IExecutionStopper
{
    /// <inheritdoc />
    public async Task StopAsync(ExecutionIdentity execution, string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (execution.Kind != ExecutionKind.Review)
        {
            // Thread passes and mention replies run in this process; the services that run them record the stop
            // when the next AI call is refused.
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        await jobs.SetStoppedAsync(execution.Id, message, ct).ConfigureAwait(false);
    }
}
