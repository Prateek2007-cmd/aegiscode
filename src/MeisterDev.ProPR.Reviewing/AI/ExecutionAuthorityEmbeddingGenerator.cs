// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     An embedding-generator decorator that reports the usage of each request to the registered execution
///     authority, and refuses to start a request after the authority stopped the current execution.
/// </summary>
public sealed class ExecutionAuthorityEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    IExecutionScopeAccessor scopes,
    IExecutionUsageSink sink,
    ExecutionAuthorityConnection connection,
    TimeProvider timeProvider) : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    /// <inheritdoc />
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        scopes.Current?.ThrowIfStopped();
        var started = timeProvider.GetTimestamp();

        var result = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);

        await ExecutionUsageRecording.RecordAsync(
                sink,
                connection.Usage(
                    AiOperation.Embedding,
                    AiTokenUsageExtractor.FromUsage(result.Usage),
                    timeProvider.GetElapsedTime(started),
                    timeProvider.GetUtcNow()))
            .ConfigureAwait(false);
        return result;
    }
}
