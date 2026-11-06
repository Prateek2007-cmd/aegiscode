// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for explicit custom review rules: a rule never invents a finding
///     (a match is always a literal pattern hit on a real path or real content), it
///     never downgrades a severity, it cites itself by name, and an invalid regex is
///     rejected at validation time with the real error.
/// </summary>
public sealed class CustomReviewRulesTests
{
    private static CustomReviewRule Rule(
        string name = "No Console.WriteLine",
        string pattern = "Console.WriteLine",
        bool isRegex = false,
        string severity = "Error",
        string message = "Do not write to the console in production code.",
        bool enabled = true) =>
        new(Guid.NewGuid(), name, pattern, isRegex, severity, message, enabled);

    private static ReviewComment Comment(string message, string file = "src/Foo.cs", CommentSeverity severity = CommentSeverity.Info) =>
        new(file, 10, severity, message);

    private static ReviewResult Result(params ReviewComment[] comments) =>
        new("Review summary.", comments);

    private static ChangedFile File(string path, string content, bool isBinary = false) =>
        new(path, ChangeType.Add, content, string.Empty, isBinary);

    [Fact]
    public void TryValidatePattern_EmptyPattern_Rejected()
    {
        Assert.False(CustomReviewRules.TryValidatePattern("   ", isRegex: false, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidatePattern_InvalidRegex_RejectedWithRealError()
    {
        Assert.False(CustomReviewRules.TryValidatePattern("([unclosed", isRegex: true, out var error));
        Assert.NotNull(error);
        Assert.Contains("(", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidatePattern_ValidRegex_Accepted()
    {
        Assert.True(CustomReviewRules.TryValidatePattern(@"TODO\s*:", isRegex: true, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryParseSeverity_Unknown_Rejected()
    {
        Assert.False(CustomReviewRules.TryParseSeverity("Critical", out _));
        Assert.True(CustomReviewRules.TryParseSeverity("warning", out var parsed));
        Assert.Equal(CommentSeverity.Warning, parsed);
    }

    [Fact]
    public void Matches_SubstringHitsPathAndContent_CaseInsensitive()
    {
        var rule = Rule(pattern: "generated");
        Assert.True(CustomReviewRules.Matches(rule, "src/Foo.Generated.cs", null, out var pathHit));
        Assert.Contains("Generated", pathHit, StringComparison.Ordinal);
        Assert.True(CustomReviewRules.Matches(rule, "src/Foo.cs", "var x = generatedCode;", out _));
        Assert.False(CustomReviewRules.Matches(rule, "src/Foo.cs", "nothing here", out _));
    }

    [Fact]
    public void Matches_DisabledRule_NeverMatches()
    {
        var rule = Rule(enabled: false);
        Assert.False(CustomReviewRules.Matches(rule, "src/Foo.cs", "Console.WriteLine(x);", out _));
    }

    [Fact]
    public void Matches_RegexHitsContent_AndInvalidRegexNeverMatches()
    {
        var rule = Rule(pattern: @"TODO\s*:", isRegex: true);
        Assert.True(CustomReviewRules.Matches(rule, "src/Foo.cs", "// TODO: fix this", out var hit));
        Assert.Contains("TODO", hit, StringComparison.Ordinal);
        Assert.False(CustomReviewRules.Matches(rule, "src/Foo.cs", "no marker here", out _));
    }

    [Fact]
    public void Apply_NoRules_ReturnsSameInstance()
    {
        var result = Result(Comment("Something."));
        var (filtered, upgraded, added) = CustomReviewRules.Apply(result, [], []);
        Assert.Same(result, filtered);
        Assert.Equal(0, upgraded);
        Assert.Equal(0, added);
    }

    [Fact]
    public void Apply_MatchingFinding_UpgradedAndCited()
    {
        var result = Result(Comment("Avoid Console.WriteLine here.", severity: CommentSeverity.Info));
        var (filtered, upgraded, added) = CustomReviewRules.Apply(result, [Rule()], [File("src/Foo.cs", "class Foo {}")]);

        Assert.Equal(1, upgraded);
        Assert.Equal(0, added);
        var comment = Assert.Single(filtered.Comments);
        Assert.Equal(CommentSeverity.Error, comment.Severity);
        Assert.Contains("[Custom rule \"No Console.WriteLine\"", comment.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MatchingFinding_NeverDowngrades()
    {
        var result = Result(Comment("Avoid Console.WriteLine here.", severity: CommentSeverity.Error));
        var (filtered, upgraded, _) = CustomReviewRules.Apply(result, [Rule(severity: "Info")], [File("src/Foo.cs", "class Foo {}")]);

        Assert.Equal(0, upgraded);
        Assert.Equal(CommentSeverity.Error, Assert.Single(filtered.Comments).Severity);
        Assert.Contains("[Custom rule", Assert.Single(filtered.Comments).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RuleMatchesFileContentWithNoFinding_AddsOneFindingWithEvidence()
    {
        var result = Result(Comment("Unrelated note.", file: "src/Other.cs"));
        var (filtered, upgraded, added) = CustomReviewRules.Apply(
            result, [Rule()], [File("src/Foo.cs", "class Foo {\n  void M() { Console.WriteLine(1); }\n}")]);

        Assert.Equal(0, upgraded);
        Assert.Equal(1, added);
        Assert.Equal(2, filtered.Comments.Count);
        var addedComment = filtered.Comments[1];
        Assert.Equal("src/Foo.cs", addedComment.FilePath);
        Assert.Equal(2, addedComment.LineNumber);
        Assert.Equal(CommentSeverity.Error, addedComment.Severity);
        Assert.Contains("[Custom rule \"No Console.WriteLine\"", addedComment.Message, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine", addedComment.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RuleMatchesFileContentButFindingAlreadyMatches_NoDuplicateAdded()
    {
        var result = Result(Comment("Console.WriteLine is used here.", file: "src/Foo.cs"));
        var (_, _, added) = CustomReviewRules.Apply(result, [Rule()], [File("src/Foo.cs", "Console.WriteLine(1);")]);

        Assert.Equal(0, added);
    }

    [Fact]
    public void Apply_NoMatch_AddsNothing()
    {
        var result = Result(Comment("Unrelated note."));
        var (filtered, upgraded, added) = CustomReviewRules.Apply(result, [Rule()], [File("src/Foo.cs", "class Foo {}")]);

        Assert.Same(result, filtered);
        Assert.Equal(0, upgraded);
        Assert.Equal(0, added);
    }

    [Fact]
    public void UpgradeScanSeverity_OnlyEverUpgrades()
    {
        Assert.Equal("Critical", CustomReviewRules.UpgradeScanSeverity("Error", "Low"));
        Assert.Equal("High", CustomReviewRules.UpgradeScanSeverity("Warning", "Medium"));
        Assert.Equal("Critical", CustomReviewRules.UpgradeScanSeverity("Warning", "Critical"));
        Assert.Equal("Info", CustomReviewRules.UpgradeScanSeverity("Info", "Info"));
    }
}
