// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Extensibility.Execution;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ExecutionAuthorityChatClientTests
{
    private static readonly ExecutionAuthorityConnection Connection = new(
        Guid.NewGuid(),
        4,
        Guid.NewGuid(),
        null,
        "openai",
        "gpt-test");

    private readonly ExecutionScopeAccessor _scopes = new();
    private readonly RecordingSink _sink = new();
    private readonly IChatClient _inner = Substitute.For<IChatClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ACompletedCall_ReportsItsUsageWithTheConnectionRevision()
    {
        this._inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 },
                });

        await this.Client().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var usage = Assert.Single(this._sink.Recorded);
        Assert.Equal(Connection.ConnectionId, usage.ConnectionId);
        Assert.Equal(4, usage.ConnectionRevision);
        Assert.Equal("gpt-test", usage.ModelId);
        Assert.Equal(AiOperation.Chat, usage.Operation);
        Assert.Equal(120, usage.Usage.InputTokens);
        Assert.Equal(30, usage.Usage.OutputTokens);
        Assert.False(usage.Usage.IsEstimated);
    }

    [Fact]
    public async Task AResponseWithoutUsage_IsReportedAsMissingUsage()
    {
        this._inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await this.Client().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.True(Assert.Single(this._sink.Recorded).Usage.IsEstimated);
    }

    [Fact]
    public async Task AStoppedExecution_MakesNoFurtherCall()
    {
        var scope = new ExecutionScope(new ExecutionIdentity(ExecutionKind.Review, Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        scope.RequestStop("The plan's usage cap is reached.");
        using var _ = this._scopes.BeginScope(scope);

        await Assert.ThrowsAsync<ExecutionStoppedException>(() => this.Client().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Empty(this._inner.ReceivedCalls());
        Assert.Empty(this._sink.Recorded);
    }

    // The provider served the call before the caller cancelled, so its usage is still recorded.
    [Fact]
    public async Task ACompletedCall_IsReportedAlthoughTheCallerCancelled()
    {
        using var caller = new CancellationTokenSource();
        this._inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                caller.Cancel();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 },
                };
            });

        await this.Client().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: caller.Token);

        Assert.Equal(120, Assert.Single(this._sink.Recorded).Usage.InputTokens);
    }

    [Fact]
    public async Task AStreamTheConsumerLeavesEarly_IsReportedWithTheUsageItDelivered()
    {
        this._inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Updates(false, UsageUpdate(120, 30), new ChatResponseUpdate(ChatRole.Assistant, "more")));

        await foreach (var _ in this.Client().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            break;
        }

        Assert.Equal(120, Assert.Single(this._sink.Recorded).Usage.InputTokens);
    }

    [Fact]
    public async Task AStreamThatFailsAfterDeliveringOutput_IsReported()
    {
        this._inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Updates(true, UsageUpdate(120, 30)));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in this.Client().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });

        Assert.Equal(120, Assert.Single(this._sink.Recorded).Usage.InputTokens);
    }

    // A stream that fails before the provider delivered anything served nothing, as a call that throws does.
    [Fact]
    public async Task AStreamThatFailsBeforeAnyOutput_IsNotReported()
    {
        this._inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Updates(true));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in this.Client().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });

        Assert.Empty(this._sink.Recorded);
    }

    private static ChatResponseUpdate UsageUpdate(long inputTokens, long outputTokens)
    {
        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens })],
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(bool failAtEnd, params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }

        if (failAtEnd)
        {
            throw new HttpRequestException("The connection was reset.");
        }
    }

    private ExecutionAuthorityChatClient Client()
    {
        return new ExecutionAuthorityChatClient(this._inner, this._scopes, this._sink, Connection, this._time);
    }

    private sealed class RecordingSink : IExecutionUsageSink
    {
        public List<AiCallUsage> Recorded { get; } = [];

        public bool IsActive => true;

        // A store honors the token it is given, so a cancelled token loses the report.
        public Task RecordAsync(AiCallUsage usage, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            this.Recorded.Add(usage);
            return Task.CompletedTask;
        }
    }
}
