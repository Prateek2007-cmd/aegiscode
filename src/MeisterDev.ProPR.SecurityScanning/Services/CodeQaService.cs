// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Answers a question about a client's codebase, grounded in retrieved code.</summary>
public interface ICodeQaService
{
    /// <summary>
    ///     Asks the codebase a question. Returns an unanswered response — never an invented
    ///     answer — when ProCursor is unavailable, nothing relevant was retrieved, no AI model
    ///     is configured, the AI call failed, or the model answered without citing the
    ///     retrieved snippets. Throws <see cref="ArgumentException"/> on an empty question.
    /// </summary>
    Task<AskCodeQuestionResponse> AskAsync(Guid clientId, AskCodeQuestionRequest request, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CodeQaService(
    IProCursorGateway proCursorGateway,
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<CodeQaService> logger) : ICodeQaService
{
    private const int MaxRetrievedSnippets = 8;

    private const string DontKnowAnswer =
        "I don't know from this codebase — no indexed code matched your question.";

    /// <inheritdoc />
    public async Task<AskCodeQuestionResponse> AskAsync(Guid clientId, AskCodeQuestionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new ArgumentException("Question is required.", nameof(request));
        }

        var question = request.Question.Trim();

        ProCursorKnowledgeAnswerDto knowledge;
        try
        {
            knowledge = await proCursorGateway.AskKnowledgeAsync(
                new ProCursorKnowledgeQueryRequest(
                    clientId,
                    question,
                    request.KnowledgeSourceIds,
                    request.RepositoryContext is null
                        ? null
                        : new ProCursorRepositoryContextDto(
                            request.RepositoryContext.ProviderScopePath,
                            request.RepositoryContext.ProviderProjectKey,
                            request.RepositoryContext.RepositoryId,
                            request.RepositoryContext.Branch),
                    MaxRetrievedSnippets),
                ct);
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            // The remote gateway raises when it cannot reach ProCursor; the disabled gateway
            // answers "unavailable" instead. Either way the user gets the honest reason.
            logger.LogWarning(ex, "Code Q&A unavailable for client {ClientId}: ProCursor cannot be reached.", clientId);
            return new AskCodeQuestionResponse(
                false,
                "I can't answer from this codebase — ProCursor is not available. " + ex.Message,
                [],
                "unavailable",
                ex.Message,
                null);
        }

        if (!string.Equals(knowledge.Status, "complete", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(knowledge.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Code Q&A for client {ClientId} retrieved nothing (status {Status}).",
                clientId, knowledge.Status);
            return new AskCodeQuestionResponse(
                false,
                string.IsNullOrWhiteSpace(knowledge.NoResultReason)
                    ? DontKnowAnswer
                    : knowledge.NoResultReason,
                [],
                knowledge.Status,
                knowledge.NoResultReason,
                null);
        }

        if (knowledge.Results.Count == 0)
        {
            return new AskCodeQuestionResponse(
                false,
                DontKnowAnswer,
                [],
                "noResult",
                "No indexed knowledge matched the requested question.",
                null);
        }

        var citations = knowledge.Results
            .Select(match => new CodeQaCitationResponse(
                match.ContentPath,
                match.Title,
                match.Excerpt,
                match.Score,
                match.SourceKind.ToString(),
                match.Branch,
                match.CommitSha,
                match.FreshnessStatus))
            .ToList();

        IResolvedAiChatRuntime runtime;
        try
        {
            // No dedicated Q&A purpose exists, so questions use the chat model the client
            // already configured for reviews (ReviewDefault). The model id is reported on
            // every answer, so the user can see exactly what answered.
            runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewDefault, ct);
        }
        catch (AiPurposeBindingNotConfiguredException ex)
        {
            logger.LogWarning(ex, "Code Q&A for client {ClientId} has no configured chat model.", clientId);
            return new AskCodeQuestionResponse(
                false,
                "I can't answer yet — no AI model is configured for this client. Bind a chat model to the ReviewDefault purpose first.",
                citations,
                knowledge.Status,
                ex.Message,
                null);
        }

        string answer;
        try
        {
            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new(ChatRole.System, BuildSystemPrompt()),
                    new(ChatRole.User, BuildUserMessage(question, citations)),
                ],
                new ChatOptions { ModelId = runtime.Model.RemoteModelId },
                ct);
            answer = response.Text ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The retrieval is real and shown; only the generation failed. Never present a
            // failure as an answer.
            logger.LogWarning(ex, "Code Q&A AI call failed for client {ClientId}.", clientId);
            return new AskCodeQuestionResponse(
                false,
                "The AI call failed, so there is no answer. The snippets below are what the codebase actually matched — read those instead.",
                citations,
                knowledge.Status,
                ex.Message,
                runtime.Model.RemoteModelId);
        }

        var check = CodeQaCitationGate.Validate(answer, citations.Count);
        if (!check.IsGrounded)
        {
            logger.LogWarning(
                "Code Q&A answer withheld for client {ClientId}: {Problem}",
                clientId, check.Problem);
            return new AskCodeQuestionResponse(
                false,
                (check.Problem ?? "The answer could not be grounded in the retrieved code.")
                + " The snippets below are what the codebase actually matched — read those instead.",
                citations,
                knowledge.Status,
                check.Problem,
                runtime.Model.RemoteModelId);
        }

        return new AskCodeQuestionResponse(
            true,
            answer,
            citations,
            knowledge.Status,
            null,
            runtime.Model.RemoteModelId);
    }

    private static string BuildSystemPrompt()
    {
        return """
               You answer questions about a codebase using ONLY the numbered code excerpts provided in the user message.
               Rules you must follow:
               - Answer only from the excerpts. If they do not contain the answer, say "I don't know from the provided excerpts."
               - Cite every factual claim with the excerpt number in square brackets, like [1] or [2]. Do not cite a number that was not provided.
               - Never invent a file path, line number, symbol, or behaviour that is not in the excerpts.
               - Keep the answer short and direct. Quote the relevant code briefly where it helps.
               """;
    }

    private static string BuildUserMessage(string question, IReadOnlyList<CodeQaCitationResponse> citations)
    {
        var builder = new StringBuilder();
        builder.Append("Question: ").AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Retrieved code excerpts (cite these by number):");
        for (var index = 0; index < citations.Count; index++)
        {
            var citation = citations[index];
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"[{index + 1}] {citation.ContentPath} (branch {citation.Branch}, commit {citation.CommitSha})");
            if (!string.IsNullOrWhiteSpace(citation.Title))
            {
                builder.Append(CultureInfo.InvariantCulture, $" — {citation.Title}");
            }

            builder.AppendLine();
            builder.AppendLine(citation.Excerpt);
        }

        return builder.ToString();
    }
}
