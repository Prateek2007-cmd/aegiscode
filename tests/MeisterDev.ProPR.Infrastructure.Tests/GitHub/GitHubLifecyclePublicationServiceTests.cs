// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubLifecyclePublicationServiceTests
{
    [Fact]
    public void BuildPayload_SingleLineSuggestion_RendersSuggestionBlockWithNoStartLine()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Suggestion, "Use the helper.")
        {
            SuggestedCode = "return x + 1;",
            SuggestionStartLine = 18,
            SuggestionEndLine = 18,
        };

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal("src/file.ts", inline.Path);
        Assert.Equal(18, inline.Line);
        Assert.Equal("RIGHT", inline.Side);
        Assert.Null(inline.StartLine);
        Assert.Null(inline.StartSide);
        Assert.Equal(
            "Suggestion: Use the helper.\n\n```suggestion\nreturn x + 1;\n```\n\n" + TestPostedCommentComposer.DistinctiveMarker,
            inline.Body);
        TestPostedCommentComposer.AssertMarkedOnce(inline.Body, TestPostedCommentComposer.DistinctiveMarker);

        // The wire shape must stay exactly the single-line shape: no start_line/start_side keys.
        var json = JsonSerializer.Serialize(payload);
        Assert.DoesNotContain("start_line", json, StringComparison.Ordinal);
        Assert.DoesNotContain("start_side", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_SuggestionWithoutExplicitLines_FallsBackToLineNumber()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Suggestion, "Use the helper.")
        {
            SuggestedCode = "return x + 1;",
        };

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal(18, inline.Line);
        Assert.Null(inline.StartLine);
        Assert.Null(inline.StartSide);
        Assert.Contains("```suggestion\nreturn x + 1;\n```", inline.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_MultiLineSuggestion_SendsStartLineAndStartSide()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Suggestion, "Inline the total.")
        {
            SuggestedCode = "const total = a + b;\nreturn total;",
            SuggestionStartLine = 18,
            SuggestionEndLine = 21,
        };

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal("src/file.ts", inline.Path);
        Assert.Equal(21, inline.Line);
        Assert.Equal("RIGHT", inline.Side);
        Assert.Equal(18, inline.StartLine);
        Assert.Equal("RIGHT", inline.StartSide);
        Assert.Equal(
            "Suggestion: Inline the total.\n\n```suggestion\nconst total = a + b;\nreturn total;\n```\n\n" + TestPostedCommentComposer.DistinctiveMarker,
            inline.Body);
        TestPostedCommentComposer.AssertMarkedOnce(inline.Body, TestPostedCommentComposer.DistinctiveMarker);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var jsonComment = document.RootElement.GetProperty("comments")[0];
        Assert.Equal(18, jsonComment.GetProperty("start_line").GetInt32());
        Assert.Equal("RIGHT", jsonComment.GetProperty("start_side").GetString());
        Assert.Equal(21, jsonComment.GetProperty("line").GetInt32());
    }

    [Fact]
    public void BuildPayload_MultiLineSuggestionWithOnlyEndLine_FallsBackStartToLineNumber()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Suggestion, "Inline the total.")
        {
            SuggestedCode = "const total = a + b;\nreturn total;",
            SuggestionEndLine = 21,
        };

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal(21, inline.Line);
        Assert.Equal(18, inline.StartLine);
        Assert.Equal("RIGHT", inline.StartSide);
    }

    [Fact]
    public void BuildPayload_FindingWithoutSuggestion_RendersExactlyAsBefore()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.");

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal("src/file.ts", inline.Path);
        Assert.Equal(18, inline.Line);
        Assert.Equal("RIGHT", inline.Side);
        Assert.Null(inline.StartLine);
        Assert.Null(inline.StartSide);
        Assert.Equal(
            "Warning: Guard this null case.\n\n" + TestPostedCommentComposer.DistinctiveMarker,
            inline.Body);
        Assert.DoesNotContain("```suggestion", inline.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_WhitespaceOnlySuggestedCode_RendersNoSuggestionBlock()
    {
        var comment = new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")
        {
            SuggestedCode = "   ",
            SuggestionStartLine = 18,
            SuggestionEndLine = 21,
        };

        var payload = BuildPayload([comment]);

        var inline = Assert.Single(payload.Comments);
        Assert.Equal(18, inline.Line);
        Assert.Null(inline.StartLine);
        Assert.DoesNotContain("```suggestion", inline.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_PullRequestLevelFinding_StaysInSummaryWithoutInlineComment()
    {
        var comment = new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found.");

        var payload = BuildPayload([comment]);

        Assert.Empty(payload.Comments);
        Assert.Contains("No blocking issues found.", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_Overview_RendersAtTopBeforeReviewSummary()
    {
        var result = new ReviewResult("Looks solid overall.", [])
        {
            PrSummary = "Adds a login cache so repeat visits skip the database.",
            ChangeStats = new PrChangeStats(2, 5, 1),
            Walkthrough =
            [
                new PrWalkthroughEntry("src/Cache.cs", "Adds the cache itself."),
                new PrWalkthroughEntry("src/Login.cs", "Reads from the cache first."),
            ],
            ChangeDiagram = "flowchart TD\n    A[Login] --> B[Cache]",
        };

        var payload = BuildPayload([], result);

        Assert.Contains("## Meister Review Bot Review", payload.Body, StringComparison.Ordinal);
        Assert.Contains("### What this PR does", payload.Body, StringComparison.Ordinal);
        Assert.Contains("Adds a login cache so repeat visits skip the database.", payload.Body, StringComparison.Ordinal);
        Assert.Contains("**Changes:** 2 files changed, +5 additions, -1 deletions", payload.Body, StringComparison.Ordinal);
        Assert.Contains("- **src/Cache.cs:** Adds the cache itself.", payload.Body, StringComparison.Ordinal);
        Assert.Contains("### Change diagram", payload.Body, StringComparison.Ordinal);
        Assert.Contains("```mermaid\nflowchart TD\n    A[Login] --> B[Cache]\n```", payload.Body, StringComparison.Ordinal);
        Assert.Contains("Looks solid overall.", payload.Body, StringComparison.Ordinal);

        // The overview sits at the top: before the review-findings summary.
        Assert.True(
            payload.Body.IndexOf("### What this PR does", StringComparison.Ordinal)
            < payload.Body.IndexOf("Looks solid overall.", StringComparison.Ordinal));
        Assert.True(
            payload.Body.IndexOf("### Change diagram", StringComparison.Ordinal)
            < payload.Body.IndexOf("Looks solid overall.", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPayload_NoOverview_BodyIsUnchanged()
    {
        var payload = BuildPayload([]);

        Assert.DoesNotContain("### What this PR does", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("**Changes:**", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Walkthrough", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Change diagram", payload.Body, StringComparison.Ordinal);
        Assert.Contains("Looks solid overall.", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_InvalidDiagram_IsOmittedFromBody()
    {
        var result = new ReviewResult("Looks solid overall.", [])
        {
            PrSummary = "Does the thing.",
            ChangeDiagram = "This is not a diagram at all.",
        };

        var payload = BuildPayload([], result);

        Assert.Contains("### What this PR does", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Change diagram", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("mermaid", payload.Body, StringComparison.Ordinal);
    }

    private static GitHubLifecyclePublicationService.GitHubReviewRequest BuildPayload(
        IReadOnlyList<ReviewComment> comments)
    {
        return BuildPayload(comments, new ReviewResult("Looks solid overall.", comments));
    }

    private static GitHubLifecyclePublicationService.GitHubReviewRequest BuildPayload(
        IReadOnlyList<ReviewComment> comments,
        ReviewResult result)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);

        return GitHubLifecyclePublicationService.BuildPayload(
            review,
            revision,
            result,
            reviewer,
            TestPostedCommentComposer.Distinctive);
    }
}
