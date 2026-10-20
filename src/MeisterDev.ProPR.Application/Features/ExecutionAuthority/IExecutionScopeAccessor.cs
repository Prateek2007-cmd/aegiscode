// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     Ambient access to the execution running on the current logical call context. The service that runs a review,
///     a thread pass or a mention reply opens a scope for its duration, and the AI call decorators read it on each
///     call to attribute usage and to refuse a call after a stop.
/// </summary>
public interface IExecutionScopeAccessor
{
    /// <summary>The scope of the current execution, or <see langword="null" /> outside an execution.</summary>
    ExecutionScope? Current { get; }

    /// <summary>
    ///     Sets <paramref name="scope" /> as the ambient scope for the current call context and returns a handle that
    ///     restores the previous scope on dispose.
    /// </summary>
    IDisposable BeginScope(ExecutionScope scope);
}
