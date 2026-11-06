// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the reviewer-memory rule vocabulary: how a finding becomes a learnable
///     rule key, and when repeated dismissals mute that rule. Keys are heuristics — a review
///     rule is its message's normalized first line — so the tests pin the normalization
///     exactly: the recording path and the publication filter must derive identical keys.
/// </summary>
public sealed class ReviewerMemoryRulesTests
{
    [Fact]
    public void NormalizeTitle_LowercasesCollapsesWhitespaceAndTrimsPunctuation()
    {
        Assert.Equal(
            "avoid console.writeline in production code",
            ReviewerMemoryRules.NormalizeTitle("  Avoid   Console.WriteLine  in production code.  "));
    }

    [Fact]
    public void NormalizeTitle_UsesFirstLineOnly()
    {
        Assert.Equal(
            "consider using stringbuilder here",
            ReviewerMemoryRules.NormalizeTitle("Consider using StringBuilder here\nThis loop builds a large string."));
    }

    [Fact]
    public void NormalizeTitle_TruncatesToMaxLabelLength()
    {
        var longTitle = new string('a', 200);
        var normalized = ReviewerMemoryRules.NormalizeTitle(longTitle);
        Assert.NotNull(normalized);
        Assert.Equal(ReviewerMemoryRules.MaxLabelLength, normalized!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void NormalizeTitle_NothingUsable_ReturnsNull(string? title)
    {
        Assert.Null(ReviewerMemoryRules.NormalizeTitle(title));
    }

    [Fact]
    public void KeyForReviewFinding_PrefixesReviewAndNormalizes()
    {
        Assert.Equal(
            "review:avoid console.writeline in production code",
            ReviewerMemoryRules.KeyForReviewFinding("Avoid Console.WriteLine in production code."));
    }

    [Fact]
    public void KeyForScanFinding_PrefixesScanAndNormalizes()
    {
        Assert.Equal(
            "scan:cve-2024-1234 in lodash",
            ReviewerMemoryRules.KeyForScanFinding("CVE-2024-1234 in lodash"));
    }

    [Fact]
    public void KeyForReviewFinding_EmptyMessage_ReturnsNull()
    {
        Assert.Null(ReviewerMemoryRules.KeyForReviewFinding("   "));
        Assert.Null(ReviewerMemoryRules.KeyForReviewFinding(null));
    }

    [Fact]
    public void KeyForFixProposal_IgnoresFindingRef_UsesTitle()
    {
        var fromFirst = ReviewerMemoryRules.KeyForFixProposal("review", "review:job-1:1:src/Foo.cs:10", "Avoid Console.WriteLine.");
        var fromSecond = ReviewerMemoryRules.KeyForFixProposal("review", "review:job-2:3:src/Bar.cs:44", "Avoid Console.WriteLine.");
        Assert.Equal("review:avoid console.writeline", fromFirst);
        Assert.Equal(fromFirst, fromSecond);
    }

    [Fact]
    public void KeyForFixProposal_UnknownSource_ReturnsNull()
    {
        Assert.Null(ReviewerMemoryRules.KeyForFixProposal("other", "x", "Some title"));
        Assert.Null(ReviewerMemoryRules.KeyForFixProposal(null, "x", "Some title"));
    }

    [Theory]
    [InlineData("review:avoid x", "review:avoid x")]
    [InlineData("  REVIEW:Avoid X  ", "review:avoid x")]
    [InlineData("scan:cve-1", "scan:cve-1")]
    public void NormalizeRuleKey_ValidKey_Normalizes(string input, string expected)
    {
        Assert.Equal(expected, ReviewerMemoryRules.NormalizeRuleKey(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-colon")]
    [InlineData(":empty-source")]
    [InlineData("review:")]
    [InlineData("other:rule")]
    public void NormalizeRuleKey_Malformed_ReturnsNull(string? input)
    {
        Assert.Null(ReviewerMemoryRules.NormalizeRuleKey(input));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 4, false)]
    [InlineData(0, 5, true)]
    [InlineData(0, 12, true)]
    [InlineData(1, 5, false)]
    [InlineData(2, 50, false)]
    public void ShouldMute_OnlyFiveDismissalsWithNoAccepts(int accepts, int dismissals, bool expected)
    {
        Assert.Equal(expected, ReviewerMemoryRules.ShouldMute(accepts, dismissals));
    }

    [Fact]
    public void DisplayLabel_PreservesCaseAndTruncatesWithEllipsis()
    {
        Assert.Equal("Avoid Console.WriteLine.", ReviewerMemoryRules.DisplayLabel("Avoid Console.WriteLine."));
        var longTitle = new string('b', 200);
        var label = ReviewerMemoryRules.DisplayLabel(longTitle);
        Assert.NotNull(label);
        Assert.EndsWith("…", label);
        Assert.Equal(ReviewerMemoryRules.MaxLabelLength + 1, label!.Length);
    }

    [Fact]
    public void GuidanceLine_NamesLabelAndDismissalCount()
    {
        var line = ReviewerMemoryRules.GuidanceLine("Avoid Console.WriteLine.", 5);
        Assert.Contains("Avoid Console.WriteLine.", line, StringComparison.Ordinal);
        Assert.Contains("5", line, StringComparison.Ordinal);
        Assert.Contains("muted by reviewer memory", line, StringComparison.Ordinal);
    }
}
