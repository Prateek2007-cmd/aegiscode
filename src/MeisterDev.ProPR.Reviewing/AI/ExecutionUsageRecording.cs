// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>Records the usage of an AI call the provider has served.</summary>
internal static class ExecutionUsageRecording
{
    /// <summary>How long recording may take once the provider has served the call.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Hands the usage to the sink. The provider was paid for the call when it served it, so the caller's
    ///     cancellation does not discard the usage. The recording is bounded on its own, so a stalled store cannot
    ///     hold the caller indefinitely.
    /// </summary>
    public static async Task RecordAsync(IExecutionUsageSink sink, AiCallUsage usage)
    {
        using var bounded = new CancellationTokenSource(Timeout);
        await sink.RecordAsync(usage, bounded.Token).ConfigureAwait(false);
    }
}
