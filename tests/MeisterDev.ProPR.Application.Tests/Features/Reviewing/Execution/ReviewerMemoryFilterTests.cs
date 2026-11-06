// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the reviewer-memory publication filter: findings whose rule the client
///     muted never reach the pull request, are counted, and are named in the summary — while
///     the persisted result keeps every finding. The filter must never hide a finding it
///     cannot attribute to a muted rule.
/// </summary>
public sealed class ReviewerMemoryFilterTests
{
    private static ReviewComment Comment(string message, string file = "src/Foo.cs", int line = 10) =>
        new(file, line, CommentSeverity.Warning, message);

    private static ReviewResult Result(params ReviewComment[] comments) =>
        new("Review summary.", comments);

    [Fact]
    public void Apply_NoMutedKeys_ReturnsSameInstanceWithZeroCount()
    {
        var result = Result(Comment("Avoid Console.WriteLine."));
        var (filtered, count) = ReviewerMemoryFilter.Apply(result, new HashSet<string>(StringComparer.Ordinal));

        Assert.Same(result, filtered);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Apply_NoComments_ReturnsSameInstanceWithZeroCount()
    {
        var result = Result();
        var muted = new HashSet<string>(StringComparer.Ordinal) { "review:avoid console.writeline" };
        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Same(result, filtered);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Apply_NothingMatches_ReturnsSameInstanceWithZeroCount()
    {
        var result = Result(Comment("Use StringBuilder here."));
        var muted = new HashSet<string>(StringComparer.Ordinal) { "review:avoid console.writeline" };
        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Same(result, filtered);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Apply_MutedFinding_WithheldCountedAndNamedInSummary()
    {
        var result = Result(
            Comment("Avoid Console.WriteLine in production code."),
            Comment("Use StringBuilder here."));
        var muted = new HashSet<string>(StringComparer.Ordinal) { "review:avoid console.writeline in production code" };

        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Equal(1, count);
        Assert.Single(filtered.Comments);
        Assert.Equal("Use StringBuilder here.", filtered.Comments[0].Message);
        Assert.Contains("muted by reviewer memory", filtered.Summary, StringComparison.Ordinal);
        Assert.Contains("Reviewer memory panel", filtered.Summary, StringComparison.Ordinal);

        // The input result is untouched: the persisted record keeps the muted finding.
        Assert.Equal(2, result.Comments.Count);
    }

    [Fact]
    public void Apply_MatchingIgnoresCaseAndSurroundingPunctuation()
    {
        var result = Result(Comment("AVOID Console.WriteLine!!!"));
        var muted = new HashSet<string>(StringComparer.Ordinal) { "review:avoid console.writeline" };

        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Equal(1, count);
        Assert.Empty(filtered.Comments);
    }

    [Fact]
    public void Apply_MultipleMuted_CountsAllAndKeepsOrderOfSurvivors()
    {
        var result = Result(
            Comment("First kept finding."),
            Comment("Avoid Console.WriteLine."),
            Comment("Second kept finding."),
            Comment("Avoid Thread.Sleep."));
        var muted = new HashSet<string>(StringComparer.Ordinal)
        {
            "review:avoid console.writeline",
            "review:avoid thread.sleep",
        };

        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Equal(2, count);
        Assert.Equal(
            new[] { "First kept finding.", "Second kept finding." },
            filtered.Comments.Select(c => c.Message).ToArray());
        Assert.Contains("2 findings were muted", filtered.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AllMuted_BlankSummaryBecomesTheMutedNote()
    {
        var result = new ReviewResult(string.Empty, new[] { Comment("Avoid Console.WriteLine.") });
        var muted = new HashSet<string>(StringComparer.Ordinal) { "review:avoid console.writeline" };

        var (filtered, count) = ReviewerMemoryFilter.Apply(result, muted);

        Assert.Equal(1, count);
        Assert.Empty(filtered.Comments);
        Assert.Contains("muted by reviewer memory", filtered.Summary, StringComparison.Ordinal);
    }
}
