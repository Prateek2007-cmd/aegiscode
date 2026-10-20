// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     The execution the AI calls on the current call context belong to, and whether the execution authority has
///     stopped it. One instance is shared by every call of the execution, including the per-file tasks a review
///     starts, so the stop flag is written and read under a lock.
/// </summary>
/// <param name="identity">The execution and its attempt.</param>
/// <param name="tenantId">The tenant the execution belongs to.</param>
/// <param name="clientId">The client the execution belongs to.</param>
public sealed class ExecutionScope(ExecutionIdentity identity, Guid tenantId, Guid clientId)
{
    private readonly object _gate = new();
    private string? _stopMessage;
    private bool _stopRequested;

    /// <summary>The execution and its attempt.</summary>
    public ExecutionIdentity Identity { get; } = identity;

    /// <summary>The tenant the execution belongs to.</summary>
    public Guid TenantId { get; } = tenantId;

    /// <summary>The client the execution belongs to.</summary>
    public Guid ClientId { get; } = clientId;

    /// <summary>Whether the execution authority stopped this execution.</summary>
    public bool StopRequested
    {
        get
        {
            lock (this._gate)
            {
                return this._stopRequested;
            }
        }
    }

    /// <summary>The text the execution authority gave with its stop, or null when it has not stopped the execution.</summary>
    public string? StopMessage
    {
        get
        {
            lock (this._gate)
            {
                return this._stopMessage;
            }
        }
    }

    /// <summary>
    ///     Records that the execution authority stopped this execution. The first stop is kept; a later one does not
    ///     replace its message.
    /// </summary>
    /// <param name="message">The text the execution authority gave with the stop.</param>
    /// <returns>Whether this call recorded the stop.</returns>
    public bool RequestStop(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (this._gate)
        {
            if (this._stopRequested)
            {
                return false;
            }

            this._stopRequested = true;
            this._stopMessage = message;
            return true;
        }
    }

    /// <summary>
    ///     Throws <see cref="ExecutionStoppedException" /> when the execution authority stopped this execution.
    ///     Called before each AI call, so no call starts after the stop is recorded. A call another task of the
    ///     execution started before that runs to its end, and its usage is reported.
    /// </summary>
    public void ThrowIfStopped()
    {
        lock (this._gate)
        {
            if (this._stopRequested)
            {
                throw new ExecutionStoppedException(this.Identity, this._stopMessage!);
            }
        }
    }
}
