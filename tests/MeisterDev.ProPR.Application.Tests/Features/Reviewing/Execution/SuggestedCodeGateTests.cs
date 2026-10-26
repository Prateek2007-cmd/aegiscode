// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the <see cref="SuggestedCodeGate" /> fail-closed verification of CodeRabbit-style
///     one-click code suggestions: a suggestion publishes only when its original text matches the head
///     file exactly, the replacement differs, the range is valid, and the range intersects the diff.
///     Every failure drops the suggestion fields and leaves the finding as plain text.
/// </summary>
public sealed class SuggestedCodeGateTests
{
    private const string HeadContent = "using System;\n\npublic sealed class Foo\n{\n    public void Bar()\n    {\n        var x = 1;\n        var y = 2;\n    }\n}";

    // Hunk 1 covers new lines 1-2, hunk 2 covers new lines 10-11; lines 3-9 are untouched.
    private const string TwoHunkDiff =
        "@@ -1,2 +1,2 @@\n using System;\n-\n+\n// added\n@@ -10,2 +10,2 @@\n     }\n-}\n+}";

    private static ReviewComment CommentWithSuggestion(
        int line,
        string suggestedCode,
        string? originalCode,
        int? start = null,
        int? end = null,
        string filePath = "src/Foo.cs") =>
        new(filePath, line, CommentSeverity.Suggestion, "Consider this change.")
        {
            SuggestedCode = suggestedCode,
            SuggestedOriginalCode = originalCode,
            SuggestionStartLine = start,
            SuggestionEndLine = end,
        };

    private static ChangedFile HeadFile(string content = HeadContent, string diff = TwoHunkDiff) =>
        new("src/Foo.cs", ChangeType.Edit, content, diff);

    [Fact]
    public void Evaluate_MatchingSnippetOnChangedLine_Passes()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 7, end: 7);

        // Line 7 sits inside the supplied changed range (5-9).
        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_MultiLineRangePassesWhenLinesMatch()
    {
        var comment = CommentWithSuggestion(
            7,
            "        var x = 2;\n        var y = 3;",
            "        var x = 1;\n        var y = 2;",
            start: 7,
            end: 8);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_MismatchedOriginal_Fails()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 99;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.OriginalCodeMismatch, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_MissingOriginal_Fails()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", null, start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.MissingOriginalCode, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_IdenticalReplacement_Fails()
    {
        var comment = CommentWithSuggestion(7, "        var x = 1;", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoOpReplacement, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_SnippetContainingCodeFence_Fails()
    {
        // A fence inside the replacement would terminate the suggestion block the publisher
        // renders, so the gate drops it and the finding publishes as plain text.
        var comment = CommentWithSuggestion(7, "```csharp\n        var x = 2;\n```", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.UnsafeCodeFence, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_RangeOutsideDiff_Fails()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(1, 2), (10, 11)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.OutsideDiff, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_RangePartiallyOverlappingDiff_Passes()
    {
        var comment = CommentWithSuggestion(
            7,
            "        var x = 2;\n        var y = 3;",
            "        var x = 1;\n        var y = 2;",
            start: 7,
            end: 8);

        // The range [7..8] intersects the changed range [8..11] on line 8.
        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(8, 11)]);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Evaluate_NoChangedRanges_Fails()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, []);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.OutsideDiff, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_WhitespaceSuggestion_IsNotASuggestion()
    {
        var comment = CommentWithSuggestion(7, "   \n  ", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.True(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoSuggestion, outcome.FailureReason);
        Assert.False(SuggestedCodeGate.HasSuggestion(comment));
    }

    [Fact]
    public void Evaluate_RangeDefaultsToAnchorLine()
    {
        // No explicit start/end: the single anchor line 7 is the range.
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;");

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Evaluate_NoAnchorLine_Fails()
    {
        var comment = new ReviewComment("src/Foo.cs", null, CommentSeverity.Suggestion, "Consider this change.")
        {
            SuggestedCode = "        var x = 2;",
            SuggestedOriginalCode = "        var x = 1;",
        };

        var outcome = SuggestedCodeGate.Evaluate(comment, HeadContent, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoAnchorLine, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_InvertedOrOutOfFileRange_Fails()
    {
        var inverted = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 8, end: 7);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, SuggestedCodeGate.Evaluate(inverted, HeadContent, [(5, 9)]).FailureReason);

        var pastEnd = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 10, end: 12);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, SuggestedCodeGate.Evaluate(pastEnd, HeadContent, [(5, 12)]).FailureReason);

        var zeroStart = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 0, end: 1);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, SuggestedCodeGate.Evaluate(zeroStart, HeadContent, [(1, 9)]).FailureReason);
    }

    [Fact]
    public void Evaluate_NullHeadContent_FailsClosed()
    {
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, null, [(5, 9)]);

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoHeadContent, outcome.FailureReason);
    }

    [Fact]
    public void Evaluate_CrlfHeadContentMatchesLfOriginal()
    {
        var crlfHead = HeadContent.Replace("\n", "\r\n", StringComparison.Ordinal);
        var comment = CommentWithSuggestion(7, "        var x = 2;", "        var x = 1;", start: 7, end: 7);

        var outcome = SuggestedCodeGate.Evaluate(comment, crlfHead, [(5, 9)]);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void GateSuggestions_PassingSuggestion_KeepsInstance()
    {
        // Line 1 is inside the first hunk's changed range (1-2).
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System;", start: 1, end: 1);
        var result = new ReviewResult("Summary", [comment]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile()]);

        Assert.Equal(0, stripped);
        Assert.Same(result, gated);
        Assert.Same(comment, gated.Comments[0]);
        Assert.Equal("using System.Text;", gated.Comments[0].SuggestedCode);
    }

    [Fact]
    public void GateSuggestions_MismatchedOriginal_StripsSuggestionButKeepsFinding()
    {
        // Line 1 is in the diff, but the original text does not match the head file.
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System.IO;", start: 1, end: 1);
        var result = new ReviewResult("Summary", [comment]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile()]);

        Assert.Equal(1, stripped);
        Assert.NotSame(result, gated);
        Assert.Single(gated.Comments);
        var strippedComment = gated.Comments[0];
        Assert.Null(strippedComment.SuggestedCode);
        Assert.Null(strippedComment.SuggestionStartLine);
        Assert.Null(strippedComment.SuggestionEndLine);
        Assert.Null(strippedComment.SuggestedOriginalCode);
        Assert.Equal("src/Foo.cs", strippedComment.FilePath);
        Assert.Equal(1, strippedComment.LineNumber);
        Assert.Equal(CommentSeverity.Suggestion, strippedComment.Severity);
        Assert.Equal("Consider this change.", strippedComment.Message);
    }

    [Fact]
    public void GateSuggestions_RangeOutsideDiff_StripsSuggestion()
    {
        // Lines 7-8 match the head file exactly but sit between the two hunks, outside the diff.
        var comment = CommentWithSuggestion(
            7,
            "        var x = 2;",
            "        var x = 1;",
            start: 7,
            end: 7);
        var result = new ReviewResult("Summary", [comment]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile()]);

        Assert.Equal(1, stripped);
        Assert.Null(gated.Comments[0].SuggestedCode);
    }

    [Fact]
    public void GateSuggestions_NoSnippetFinding_Unchanged()
    {
        var plain = new ReviewComment("src/Foo.cs", 3, CommentSeverity.Warning, "Plain finding.");
        var result = new ReviewResult("Summary", [plain]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile()]);

        Assert.Equal(0, stripped);
        Assert.Same(result, gated);
        Assert.Same(plain, gated.Comments[0]);
    }

    [Fact]
    public void GateSuggestions_FileNotInPullRequest_StripsSuggestion()
    {
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System;", start: 1, end: 1);
        var result = new ReviewResult("Summary", [comment]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile() with { Path = "src/Other.cs" }]);

        Assert.Equal(1, stripped);
        Assert.Null(gated.Comments[0].SuggestedCode);
    }

    [Fact]
    public void GateSuggestions_EmptyHeadContent_FailsClosed()
    {
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System;", start: 1, end: 1);
        var result = new ReviewResult("Summary", [comment]);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [HeadFile(content: string.Empty)]);

        Assert.Equal(1, stripped);
        Assert.Null(gated.Comments[0].SuggestedCode);
    }

    [Fact]
    public void GateSuggestions_BinaryFile_FailsClosed()
    {
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System;", start: 1, end: 1);
        var result = new ReviewResult("Summary", [comment]);
        var binary = new ChangedFile("src/Foo.cs", ChangeType.Edit, string.Empty, string.Empty, true);

        var (gated, stripped) = SuggestedCodeGate.GateSuggestions(result, [binary]);

        Assert.Equal(1, stripped);
        Assert.Null(gated.Comments[0].SuggestedCode);
    }

    [Fact]
    public void StripSuggestion_PreservesProvenance()
    {
        var comment = CommentWithSuggestion(1, "using System.Text;", "using System;", start: 1, end: 1)
            with
            {
                OriginPassKind = "Baseline",
                OriginPassIndex = 1,
                OriginModelId = "model-1",
                OriginSymbolName = "Foo",
                ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
            };

        var stripped = SuggestedCodeGate.StripSuggestion(comment);

        Assert.Null(stripped.SuggestedCode);
        Assert.Equal("Baseline", stripped.OriginPassKind);
        Assert.Equal(1, stripped.OriginPassIndex);
        Assert.Equal("model-1", stripped.OriginModelId);
        Assert.Equal("Foo", stripped.OriginSymbolName);
        Assert.Equal(ReviewCommentScopeRelation.OnChangedLine, stripped.ScopeRelation);
    }
}
