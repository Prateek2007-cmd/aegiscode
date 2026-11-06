// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for grounded code answers: no retrieval means an honest "don't know" and no
///     AI call, an uncited or mis-cited model answer is withheld with the snippets still
///     shown, and a cited answer is returned with its citations and model id.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class CodeQaServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    [Fact]
    public async Task EmptyQuestionIsRejected()
    {
        var service = CreateService(new ProCursorKnowledgeAnswerDto("complete", []), "ignored");

        await Assert.ThrowsAsync<ArgumentException>(() => service.AskAsync(ClientId, new AskCodeQuestionRequest("  "), CancellationToken.None));
    }

    [Fact]
    public async Task UnavailableProCursorAnswersHonestlyWithoutCallingTheModel()
    {
        var chat = new ScriptedChatClient("ignored");
        var service = CreateService(
            new ProCursorKnowledgeAnswerDto("unavailable", [], "ProCursor is not configured for this environment."),
            "ignored",
            chat);

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("not configured", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unavailable", response.RetrievalStatus);
        Assert.Empty(response.Citations);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task NoMatchingCodeAnswersDontKnowWithoutCallingTheModel()
    {
        var chat = new ScriptedChatClient("ignored");
        var service = CreateService(
            new ProCursorKnowledgeAnswerDto("noResult", [], "No indexed knowledge matched the requested question."),
            "ignored",
            chat);

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is the flux capacitor?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("don't know", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("noResult", response.RetrievalStatus);
        Assert.Empty(response.Citations);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task CitedAnswerIsReturnedWithCitationsAndModelId()
    {
        var service = CreateService(TwoMatches(), "The handler lives in [1] and is called from [2].");

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.True(response.Answered);
        Assert.Contains("[1]", response.Answer, StringComparison.Ordinal);
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal("src/Auth/Handler.cs", response.Citations[0].ContentPath);
        Assert.Equal("complete", response.RetrievalStatus);
        Assert.Equal("test-model", response.ModelId);
    }

    [Fact]
    public async Task AnswerCitingAnUnretrievedSourceIsWithheldButSnippetsAreShown()
    {
        var service = CreateService(TwoMatches(), "See the handler in [1] and the config in [9].");

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("[9]", response.Answer, StringComparison.Ordinal);
        Assert.Contains("withheld", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, response.Citations.Count);
    }

    [Fact]
    public async Task AnswerWithoutAnyCitationIsWithheld()
    {
        var service = CreateService(TwoMatches(), "The handler validates the token before calling the store.");

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("without citing", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, response.Citations.Count);
    }

    [Fact]
    public async Task MissingChatModelBindingIsReportedHonestly()
    {
        var chat = new ScriptedChatClient("ignored");
        var service = CreateService(TwoMatches(), "ignored", chat, throwOnResolve: true);

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("No AI model is configured", response.Answer, StringComparison.Ordinal);
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task FailingAiCallIsReportedHonestlyWithSnippetsShown()
    {
        var chat = new ScriptedChatClient("ignored", new InvalidOperationException("provider is down"));
        var service = CreateService(TwoMatches(), "ignored", chat);

        var response = await service.AskAsync(ClientId, new AskCodeQuestionRequest("Where is auth handled?"), CancellationToken.None);

        Assert.False(response.Answered);
        Assert.Contains("AI call failed", response.Answer, StringComparison.Ordinal);
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal("test-model", response.ModelId);
    }

    private static ProCursorKnowledgeAnswerDto TwoMatches()
    {
        return new ProCursorKnowledgeAnswerDto(
            "complete",
            [
                new ProCursorKnowledgeAnswerMatchDto(
                    Guid.NewGuid(),
                    ProCursorSourceKind.Repository,
                    Guid.NewGuid(),
                    "main",
                    "abc123",
                    "src/Auth/Handler.cs",
                    "Auth handler",
                    "public sealed class AuthHandler { }",
                    "keyword",
                    0.9,
                    "fresh"),
                new ProCursorKnowledgeAnswerMatchDto(
                    Guid.NewGuid(),
                    ProCursorSourceKind.Repository,
                    Guid.NewGuid(),
                    "main",
                    "abc123",
                    "src/Auth/Calls.cs",
                    "Caller",
                    "var handler = new AuthHandler();",
                    "keyword",
                    0.7,
                    "fresh"),
            ]);
    }

    private static CodeQaService CreateService(
        ProCursorKnowledgeAnswerDto knowledge,
        string modelAnswer,
        ScriptedChatClient? chat = null,
        bool throwOnResolve = false)
    {
        chat ??= new ScriptedChatClient(modelAnswer);
        var gateway = new FakeProCursorGateway(knowledge);
        var resolver = new FakeAiRuntimeResolver(chat, throwOnResolve);
        return new CodeQaService(gateway, resolver, NullLogger<CodeQaService>.Instance);
    }

    private sealed class FakeProCursorGateway(ProCursorKnowledgeAnswerDto answer) : IProCursorGateway
    {
        public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(ProCursorKnowledgeQueryRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(answer);
        }

        public Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
            Guid clientId, ProCursorKnowledgeSourceRegistrationRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<ProCursorIndexJobDto> QueueRefreshAsync(Guid clientId, Guid sourceId, ProCursorRefreshRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(Guid clientId, Guid sourceId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
            Guid clientId, Guid sourceId, ProCursorTrackedBranchCreateRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
            Guid clientId, Guid sourceId, Guid trackedBranchId, ProCursorTrackedBranchUpdateRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> RemoveTrackedBranchAsync(Guid clientId, Guid sourceId, Guid trackedBranchId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(ProCursorSymbolQueryRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeAiRuntimeResolver(ScriptedChatClient chat, bool throwOnResolve) : IAiRuntimeResolver
    {
        public Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(Guid clientId, AiPurpose purpose, CancellationToken ct = default)
        {
            if (throwOnResolve)
            {
                throw new AiPurposeBindingNotConfiguredException(purpose);
            }

            IResolvedAiChatRuntime runtime = new FakeChatRuntime(chat, clientId);
            return Task.FromResult(runtime);
        }

        public Task<IResolvedAiChatRuntime> ResolveChatRuntimeForModelAsync(Guid clientId, Guid configuredModelId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
            Guid clientId, AiPurpose purpose, int? expectedDimensions = null, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeChatRuntime(ScriptedChatClient chat, Guid clientId) : IResolvedAiChatRuntime
    {
        public AiConnectionDto Connection { get; } = new(
            Guid.NewGuid(),
            clientId,
            "Test",
            "test-kind",
            "https://example.test",
            "none",
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        public AiConfiguredModelDto Model { get; } = new(
            Guid.NewGuid(),
            "test-model",
            "Test Model",
            [AiOperationKind.Chat],
            ["auto"]);

        public AiPurposeBindingDto Binding { get; } = new(Guid.NewGuid(), AiPurpose.ReviewDefault);

        public IChatClient ChatClient => chat;

        public AgentReviewRuntimeCapabilities Capabilities { get; } = new(false, false, false, false);

        public string? LogicalModelName => null;
    }

    private sealed class ScriptedChatClient(string answer, Exception? failure = null) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Calls++;
            return failure is not null
                ? Task.FromException<ChatResponse>(failure)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public TService? GetService<TService>(object? key = null)
            where TService : class
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
