// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the diff-free core of the suggested-code gate,
///     <see cref="SuggestedCodeGate.VerifyReplacement" />, which draft-PR creation
///     shares with review publication: the replacement must match the head file's
///     lines byte-for-byte, the range must be valid, and the replacement must differ.
///     The diff-intersection check is deliberately NOT part of this core — a draft PR
///     lands on a fresh branch, not as a suggestion block on a diff line.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class DraftPullRequestGateTests
{
    private const string HeadContent =
        "using System;\n\npublic sealed class Foo\n{\n    public void Bar()\n    {\n        var x = 1;\n        var y = 2;\n    }\n}";

    [Fact]
    public void VerifyReplacement_MatchingLines_Passes()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 7, "        var x = 1;", "        var x = 2;");

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_MultiLineRange_PassesWhenAllLinesMatch()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(
            HeadContent, 7, 8, "        var x = 1;\n        var y = 2;", "        var x = 2;\n        var y = 3;");

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_OriginalMismatch_Refuses()
    {
        // Line 7 changed since the proposal was written: the gate must refuse, never force-apply.
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 7, "        var x = 99;", "        var x = 2;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.OriginalCodeMismatch, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_EndBeyondFile_RefusesAsInvalidRange()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 99, "        var x = 1;", "        var x = 2;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_StartAfterEnd_RefusesAsInvalidRange()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 8, 7, "        var y = 2;", "        var y = 3;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_StartBelowOne_RefusesAsInvalidRange()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 0, 1, "using System;", "using System;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.InvalidRange, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_NullHeadContent_Refuses()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(null, 7, 7, "        var x = 1;", "        var x = 2;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoHeadContent, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_NullOriginalCode_Refuses()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 7, null, "        var x = 2;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.MissingOriginalCode, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_NoopReplacement_Refuses()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 7, "        var x = 1;", "        var x = 1;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoOpReplacement, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_NullRange_RefusesAsNoAnchorLine()
    {
        var outcome = SuggestedCodeGate.VerifyReplacement(HeadContent, null, null, "        var x = 1;", "        var x = 2;");

        Assert.False(outcome.Passed);
        Assert.Equal(SuggestedCodeGateFailureReason.NoAnchorLine, outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_CrlfHeadContent_MatchesNormalizedOriginal()
    {
        // The head file uses CRLF; the stored original uses LF. Line endings are
        // normalized before comparison, so the match still holds.
        const string crlfHead =
            "using System;\r\n\r\npublic sealed class Foo\r\n{\r\n    public void Bar()\r\n    {\r\n        var x = 1;\r\n        var y = 2;\r\n    }\r\n}";

        var outcome = SuggestedCodeGate.VerifyReplacement(crlfHead, 7, 7, "        var x = 1;", "        var x = 2;");

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void VerifyReplacement_WhitespaceSuggestedCode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            SuggestedCodeGate.VerifyReplacement(HeadContent, 7, 7, "        var x = 1;", "   "));
    }
}
