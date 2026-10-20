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

public sealed class ExecutionAuthorityEmbeddingGeneratorTests
{
    private static readonly ExecutionAuthorityConnection Connection = new(
        Guid.NewGuid(),
        4,
        Guid.NewGuid(),
        null,
        "openai",
        "embedding-test");

    private readonly ExecutionScopeAccessor _scopes = new();
    private readonly RecordingSink _sink = new();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ACompletedRequest_IsReportedAsAnEmbeddingCall()
    {
        this.Returns(_ => Embeddings(inputTokens: 42));

        await this.Generator().GenerateAsync(["text"]);

        var usage = Assert.Single(this._sink.Recorded);
        Assert.Equal(AiOperation.Embedding, usage.Operation);
        Assert.Equal(42, usage.Usage.InputTokens);
        Assert.Equal(Connection.ConnectionId, usage.ConnectionId);
    }

    // The provider served the request before the caller cancelled, so its usage is still recorded.
    [Fact]
    public async Task ACompletedRequest_IsReportedAlthoughTheCallerCancelled()
    {
        using var caller = new CancellationTokenSource();
        this.Returns(_ =>
        {
            caller.Cancel();
            return Embeddings(inputTokens: 42);
        });

        await this.Generator().GenerateAsync(["text"], cancellationToken: caller.Token);

        Assert.Equal(42, Assert.Single(this._sink.Recorded).Usage.InputTokens);
    }

    [Fact]
    public async Task AStoppedExecution_MakesNoFurtherRequest()
    {
        var scope = new ExecutionScope(new ExecutionIdentity(ExecutionKind.Review, Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        scope.RequestStop("The plan's usage cap is reached.");
        using var _ = this._scopes.BeginScope(scope);

        await Assert.ThrowsAsync<ExecutionStoppedException>(() => this.Generator().GenerateAsync(["text"]));

        Assert.Empty(this._inner.ReceivedCalls());
        Assert.Empty(this._sink.Recorded);
    }

    private static GeneratedEmbeddings<Embedding<float>> Embeddings(long inputTokens)
    {
        return new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f })])
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens },
        };
    }

    private void Returns(Func<NSubstitute.Core.CallInfo, GeneratedEmbeddings<Embedding<float>>> result)
    {
        this._inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(result);
    }

    private ExecutionAuthorityEmbeddingGenerator Generator()
    {
        return new ExecutionAuthorityEmbeddingGenerator(this._inner, this._scopes, this._sink, Connection, this._time);
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
