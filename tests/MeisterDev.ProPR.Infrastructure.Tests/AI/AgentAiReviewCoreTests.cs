// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public class AgentAiReviewCoreTests
{
    private static ChatResponse CreateChatResponse(string content)
    {
        var message = new ChatMessage(ChatRole.Assistant, content);
        return new ChatResponse(message);
    }

    private static ReviewSystemContext EmptyContext()
    {
        return new ReviewSystemContext(null, Array.Empty<RepositoryInstruction>(), null);
    }

    private static AgentAiReviewCore CreateCore(IChatClient chatClient)
    {
        return new AgentAiReviewCore(chatClient, Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()));
    }

    private static PullRequest CreatePullRequest(IReadOnlyList<ChangedFile>? files = null)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            "desc",
            "feature/x",
            "main",
            files ?? new List<ChangedFile>().AsReadOnly());
    }

    [Fact]
    public async Task ReviewAsync_AllSeverityLevels_ParsedCorrectly()
    {
        // Arrange
        var json = """
                   {
                     "summary": "All severities.",
                     "comments": [
                       { "filePath": null, "lineNumber": null, "severity": "info", "message": "info msg" },
                       { "filePath": null, "lineNumber": null, "severity": "warning", "message": "warning msg" },
                       { "filePath": null, "lineNumber": null, "severity": "error", "message": "error msg" },
                       { "filePath": null, "lineNumber": null, "severity": "suggestion", "message": "suggestion msg" }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        Assert.Equal(CommentSeverity.Info, result.Comments[0].Severity);
        Assert.Equal(CommentSeverity.Warning, result.Comments[1].Severity);
        Assert.Equal(CommentSeverity.Error, result.Comments[2].Severity);
        Assert.Equal(CommentSeverity.Suggestion, result.Comments[3].Severity);
    }

    [Fact]
    public async Task ReviewAsync_CancellationTokenForwardedToChatClient()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Do<CancellationToken>(ct => capturedToken = ct))
            .Returns(CreateChatResponse("""{"summary":"ok","comments":[]}"""));

        var core = CreateCore(mockClient);

        // Act
        await core.ReviewAsync(CreatePullRequest(), EmptyContext(), cts.Token);

        // Assert - the cancellation token was forwarded
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task ReviewAsync_EmptyComments_ReturnsEmptyList()
    {
        // Arrange
        var json = """{"summary": "No issues found.", "comments": []}""";

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        Assert.Equal("No issues found.", result.Summary);
        Assert.Empty(result.Comments);
    }

    [Fact]
    public async Task ReviewAsync_MalformedJsonResponse_ThrowsJsonException()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse("this is not valid json {{ }}"));

        var core = CreateCore(mockClient);

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => core.ReviewAsync(CreatePullRequest(), EmptyContext()));
    }

    [Fact]
    public async Task ReviewAsync_SendsSystemAndUserMessages()
    {
        // Arrange
        List<ChatMessage>? capturedMessages = null;
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse("""{"summary":"ok","comments":[]}"""));

        var core = CreateCore(mockClient);

        // Act
        await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert - both system and user messages sent
        Assert.NotNull(capturedMessages);
        Assert.Equal(2, capturedMessages!.Count);
        Assert.Equal(ChatRole.System, capturedMessages[0].Role);
        Assert.Equal(ChatRole.User, capturedMessages[1].Role);
    }

    [Fact]
    public async Task ReviewAsync_UnknownSeverity_DefaultsToInfo()
    {
        // Arrange
        var json = """
                   {
                     "summary": "Review done.",
                     "comments": [
                       { "filePath": null, "lineNumber": null, "severity": "unknown_value", "message": "Some comment." }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert - unknown severity defaults to Info
        Assert.Equal(CommentSeverity.Info, result.Comments[0].Severity);
    }

    [Fact]
    public async Task ReviewAsync_ValidJsonResponse_ParsesReviewResult()
    {
        // Arrange
        var json = """
                   {
                     "summary": "Overall the PR looks good.",
                     "comments": [
                       { "filePath": "/src/MyFile.cs", "lineNumber": 42, "severity": "warning", "message": "Consider using var here." },
                       { "filePath": null, "lineNumber": null, "severity": "info", "message": "Good PR structure." }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        Assert.Equal("Overall the PR looks good.", result.Summary);
        Assert.Equal(2, result.Comments.Count);
        Assert.Equal("/src/MyFile.cs", result.Comments[0].FilePath);
        Assert.Equal(42, result.Comments[0].LineNumber);
        Assert.Equal(CommentSeverity.Warning, result.Comments[0].Severity);
        Assert.Equal("Consider using var here.", result.Comments[0].Message);
        Assert.Null(result.Comments[1].FilePath);
        Assert.Equal(CommentSeverity.Info, result.Comments[1].Severity);
    }

    [Fact]
    public async Task ReviewAsync_SuggestionFields_RoundTripOntoReviewComment()
    {
        // Arrange — the AI emits the optional snake_case suggestion fields from the finding schema.
        var json = """
                   {
                     "summary": "One fixable finding.",
                     "comments": [
                       {
                         "filePath": "/src/MyFile.cs",
                         "lineNumber": 42,
                         "severity": "warning",
                         "message": "Await this call.",
                         "suggested_code": "await DeleteAsync(id).ConfigureAwait(false);",
                         "suggestion_start_line": 42,
                         "suggestion_end_line": 43,
                         "original_lines": "await DeleteAsync(id);\nawait LogAsync(id);"
                       }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        var comment = Assert.Single(result.Comments);
        Assert.Equal("await DeleteAsync(id).ConfigureAwait(false);", comment.SuggestedCode);
        Assert.Equal(42, comment.SuggestionStartLine);
        Assert.Equal(43, comment.SuggestionEndLine);
        Assert.Equal("await DeleteAsync(id);\nawait LogAsync(id);", comment.SuggestedOriginalCode);
    }

    [Fact]
    public async Task ReviewAsync_SuggestionFieldsAbsent_ParseAsNull()
    {
        // Arrange — a finding without any suggestion fields must not gain an invented snippet.
        var json = """
                   {
                     "summary": "Design feedback only.",
                     "comments": [
                       { "filePath": "/src/MyFile.cs", "lineNumber": 7, "severity": "suggestion", "message": "Consider extracting this." }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert — behaves exactly as before: plain finding, no suggestion.
        var comment = Assert.Single(result.Comments);
        Assert.Null(comment.SuggestedCode);
        Assert.Null(comment.SuggestionStartLine);
        Assert.Null(comment.SuggestionEndLine);
        Assert.Null(comment.SuggestedOriginalCode);
    }

    [Fact]
    public async Task ReviewAsync_PrOverviewKeys_ParseOntoReviewResult()
    {
        // Arrange — the AI's PR summary, walkthrough, and diagram ride on the result.
        var json = """
                   {
                     "summary": "Two findings.",
                     "pr_summary": "Adds a login cache so repeat visits skip the database.",
                     "walkthrough": [
                       { "file_path": "src/Cache.cs", "description": "Adds the cache itself." },
                       { "file_path": "src/Login.cs", "description": "Reads from the cache first." }
                     ],
                     "change_diagram": "flowchart TD\n    A[Login] --> B[Cache]",
                     "comments": []
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        Assert.Equal("Adds a login cache so repeat visits skip the database.", result.PrSummary);
        Assert.NotNull(result.Walkthrough);
        Assert.Equal(2, result.Walkthrough!.Count);
        Assert.Equal("src/Cache.cs", result.Walkthrough[0].FilePath);
        Assert.Equal("Adds the cache itself.", result.Walkthrough[0].Description);
        Assert.Equal("flowchart TD\n    A[Login] --> B[Cache]", result.ChangeDiagram);
    }

    [Fact]
    public async Task ReviewAsync_PrOverviewKeysAbsent_ParseAsNull()
    {
        // Arrange — a response without any overview keys must not gain an invented overview.
        var json = """
                   {
                     "summary": "Findings only.",
                     "comments": []
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = CreateCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest(), EmptyContext());

        // Assert
        Assert.Null(result.PrSummary);
        Assert.Null(result.Walkthrough);
        Assert.Null(result.ChangeDiagram);
    }
}
