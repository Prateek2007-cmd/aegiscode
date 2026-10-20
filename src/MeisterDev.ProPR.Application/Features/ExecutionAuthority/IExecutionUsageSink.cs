// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>Receives the usage of every completed AI call for the registered execution authority.</summary>
public interface IExecutionUsageSink
{
    /// <summary>Whether an execution authority is registered. Without one, the AI runtime does not call the sink.</summary>
    bool IsActive { get; }

    /// <summary>Stores the usage and passes it to the execution authority, applying a stop to the current execution.</summary>
    /// <param name="usage">The usage of the completed call.</param>
    /// <param name="ct">The cancellation token.</param>
    Task RecordAsync(AiCallUsage usage, CancellationToken ct = default);
}
