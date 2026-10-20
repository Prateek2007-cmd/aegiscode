// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     <see cref="AsyncLocal{T}" />-backed <see cref="IExecutionScopeAccessor" />. The scope set where an execution
///     starts flows into the tasks the execution spawns. Registered as a singleton because the ambient value belongs
///     to the call context, not to the instance.
/// </summary>
public sealed class ExecutionScopeAccessor : IExecutionScopeAccessor
{
    private static readonly AsyncLocal<ExecutionScope?> Ambient = new();

    /// <inheritdoc />
    public ExecutionScope? Current => Ambient.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new ScopeHandle(previous);
    }

    private sealed class ScopeHandle(ExecutionScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            Ambient.Value = previous;
        }
    }
}
