// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     A chat-client decorator that reports the usage of each call to the registered execution authority. Before a
///     call it refuses to start when the authority has stopped the current execution; after a call it hands the
///     normalized usage to the sink, which stores it, reports it and applies a stop.
/// </summary>
public sealed class ExecutionAuthorityChatClient(
    IChatClient innerClient,
    IExecutionScopeAccessor scopes,
    IExecutionUsageSink sink,
    ExecutionAuthorityConnection connection,
    TimeProvider timeProvider) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        scopes.Current?.ThrowIfStopped();
        var started = timeProvider.GetTimestamp();

        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        await ExecutionUsageRecording.RecordAsync(
                sink,
                connection.Usage(
                    AiOperation.Chat,
                    AiTokenUsageExtractor.FromResponse(response),
                    timeProvider.GetElapsedTime(started),
                    timeProvider.GetUtcNow()))
            .ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        scopes.Current?.ThrowIfStopped();
        var started = timeProvider.GetTimestamp();

        UsageDetails? usage = null;
        var served = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                served = true;
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        usage = usageContent.Details;
                    }
                }

                yield return update;
            }

            served = true;
        }
        finally
        {
            // Recorded once the provider delivered output or finished the stream, also when the consumer stops
            // reading early or the stream fails part way, because the provider served the call either way. A stream
            // without a usage update is reported as a call whose usage is missing, not left out.
            if (served)
            {
                await ExecutionUsageRecording.RecordAsync(
                        sink,
                        connection.Usage(
                            AiOperation.Chat,
                            AiTokenUsageExtractor.FromUsage(usage),
                            timeProvider.GetElapsedTime(started),
                            timeProvider.GetUtcNow()))
                    .ConfigureAwait(false);
            }
        }
    }
}
