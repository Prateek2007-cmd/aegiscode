// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>AI review implementation backed by an <see cref="IChatClient" />.</summary>
public sealed class AgentAiReviewCore(IChatClient chatClient, IOptions<AiReviewOptions> options) : IAiReviewCore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<ReviewResult> ReviewAsync(
        PullRequest pullRequest,
        ReviewSystemContext systemContext,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReviewPrompts.BuildGlobalSystemPrompt(systemContext)),
            new(ChatRole.User, ReviewPrompts.BuildUserMessage(pullRequest)),
        };

        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = 8192,
            ModelId = systemContext.ModelId ?? options.Value.ModelId,
        };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        var json = response.Text ?? "";

        return ParseReviewResult(json);
    }

    private static ReviewResult ParseReviewResult(string json)
    {
        var dto = JsonSerializer.Deserialize<ReviewResultDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException("AI returned null response.");

        var comments = (dto.Comments ?? []).Select(c => new ReviewComment(
                c.FilePath,
                c.LineNumber,
                Enum.TryParse<CommentSeverity>(c.Severity, true, out var sev) ? sev : CommentSeverity.Info,
                c.Message ?? "")
            {
                SuggestedCode = c.SuggestedCode,
                SuggestionStartLine = c.SuggestionStartLine,
                SuggestionEndLine = c.SuggestionEndLine,
                SuggestedOriginalCode = c.SuggestedOriginalCode,
            })
            .ToList();

        // The overview keys are optional in the AI's JSON and stay null when absent: a missing
        // summary, walkthrough, or diagram is never invented here. The orchestration-time
        // sanitizer drops walkthrough entries for files outside the pull request and gates the
        // diagram before anything is published.
        var walkthrough = (dto.Walkthrough ?? [])
            .Where(entry => entry is not null
                && !string.IsNullOrWhiteSpace(entry.FilePath)
                && !string.IsNullOrWhiteSpace(entry.Description))
            .Select(entry => new PrWalkthroughEntry(entry.FilePath!, entry.Description!))
            .ToList();

        return new ReviewResult(dto.Summary ?? "", comments.AsReadOnly())
        {
            PrSummary = string.IsNullOrWhiteSpace(dto.PrSummary) ? null : dto.PrSummary,
            Walkthrough = walkthrough.Count > 0 ? walkthrough.AsReadOnly() : null,
            ChangeDiagram = string.IsNullOrWhiteSpace(dto.ChangeDiagram) ? null : dto.ChangeDiagram,
        };
    }

    private sealed record ReviewResultDto(
        string? Summary,
        List<ReviewCommentDto>? Comments,
        [property: JsonPropertyName("pr_summary")] string? PrSummary = null,
        [property: JsonPropertyName("walkthrough")] List<WalkthroughEntryDto>? Walkthrough = null,
        [property: JsonPropertyName("change_diagram")] string? ChangeDiagram = null);

    private sealed record WalkthroughEntryDto(
        [property: JsonPropertyName("file_path")] string? FilePath,
        [property: JsonPropertyName("description")] string? Description);

    // The suggestion fields are optional in the AI's JSON and stay null when absent: a missing
    // snippet is never invented here. Wire names are snake_case, matching the finding schema in
    // the system prompt; JsonPropertyName pins them because this core's options are
    // case-insensitive only and would not bind "suggested_code" to SuggestedCode on their own.
    private sealed record ReviewCommentDto(
        string? FilePath,
        int? LineNumber,
        string? Severity,
        string? Message,
        [property: JsonPropertyName("suggested_code")] string? SuggestedCode,
        [property: JsonPropertyName("suggestion_start_line")] int? SuggestionStartLine,
        [property: JsonPropertyName("suggestion_end_line")] int? SuggestionEndLine,
        [property: JsonPropertyName("original_lines")] string? SuggestedOriginalCode);
}
