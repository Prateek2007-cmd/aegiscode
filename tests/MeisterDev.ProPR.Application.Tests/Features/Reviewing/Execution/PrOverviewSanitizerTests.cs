// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for <see cref="PrOverviewSanitizer" />: the AI-produced PR summary, file
///     walkthrough, and change diagram are prepared for publication without inventing anything, and
///     the change statistics are counted from the actual diffs. A missing section stays missing, a
///     walkthrough entry for a file outside the pull request is dropped, and a diagram that fails
///     the gate is dropped.
/// </summary>
public sealed class PrOverviewSanitizerTests
{
    private static ChangedFile File(string path, string diff, bool binary = false) =>
        new(path, ChangeType.Edit, "content", diff, binary);

    private static ReviewResult Result(
        string? prSummary = null,
        IReadOnlyList<PrWalkthroughEntry>? walkthrough = null,
        string? diagram = null) =>
        new("Review findings summary.", [])
        {
            PrSummary = prSummary,
            Walkthrough = walkthrough,
            ChangeDiagram = diagram,
        };

    [Fact]
    public void Sanitize_CountsChangeStatsFromDiffs()
    {
        var files = new List<ChangedFile>
        {
            File("src/A.cs", "@@ -1,2 +1,3 @@\n ctx\n-old\n+new1\n+new2\n"),
            File("src/B.cs", "@@ -1,1 +1,1 @@\n-old\n+new\n"),
        };

        var sanitized = PrOverviewSanitizer.Sanitize(Result(), files);

        Assert.Equal(new PrChangeStats(2, 3, 2), sanitized.ChangeStats);
    }

    [Fact]
    public void Sanitize_BinaryFileCountsAsChangedButAddsNoLines()
    {
        var files = new List<ChangedFile>
        {
            File("assets/logo.png", string.Empty, binary: true),
            File("src/A.cs", "@@ -1,1 +1,2 @@\n ctx\n+new\n"),
        };

        var sanitized = PrOverviewSanitizer.Sanitize(Result(), files);

        Assert.Equal(new PrChangeStats(2, 1, 0), sanitized.ChangeStats);
    }

    [Fact]
    public void Sanitize_DiffsWithOnlyHeaders_CountZeroLines()
    {
        var files = new List<ChangedFile>
        {
            File("src/A.cs", "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n ctx\n"),
        };

        var sanitized = PrOverviewSanitizer.Sanitize(Result(), files);

        Assert.Equal(new PrChangeStats(1, 0, 0), sanitized.ChangeStats);
    }

    [Fact]
    public void Sanitize_WalkthroughEntryForUnknownFile_IsDropped()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };
        var result = Result(walkthrough:
        [
            new PrWalkthroughEntry("src/A.cs", "Adds the helper."),
            new PrWalkthroughEntry("src/Invented.cs", "Does something the PR never touched."),
        ]);

        var sanitized = PrOverviewSanitizer.Sanitize(result, files);

        Assert.NotNull(sanitized.Walkthrough);
        var entry = Assert.Single(sanitized.Walkthrough!);
        Assert.Equal("src/A.cs", entry.FilePath);
    }

    [Fact]
    public void Sanitize_WalkthroughDeduplicatesByPathKeepingFirst()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };
        var result = Result(walkthrough:
        [
            new PrWalkthroughEntry("src/A.cs", "First description."),
            new PrWalkthroughEntry("src/A.cs", "Second description."),
        ]);

        var sanitized = PrOverviewSanitizer.Sanitize(result, files);

        var entry = Assert.Single(sanitized.Walkthrough!);
        Assert.Equal("First description.", entry.Description);
    }

    [Fact]
    public void Sanitize_WalkthroughDescription_IsFlattenedAndCapped()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };
        var result = Result(walkthrough:
        [
            new PrWalkthroughEntry("src/A.cs", "Adds\nthe helper\n  across lines."),
        ]);

        var sanitized = PrOverviewSanitizer.Sanitize(result, files);

        Assert.Equal("Adds the helper across lines.", sanitized.Walkthrough![0].Description);
    }

    [Fact]
    public void Sanitize_EmptyWalkthroughEntries_AreDropped()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };
        var result = Result(walkthrough:
        [
            new PrWalkthroughEntry("src/A.cs", "   "),
            new PrWalkthroughEntry("  ", "Has no path."),
        ]);

        var sanitized = PrOverviewSanitizer.Sanitize(result, files);

        Assert.Null(sanitized.Walkthrough);
    }

    [Fact]
    public void Sanitize_PrSummary_IsTrimmedAndCapped()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };
        var longSummary = "  " + new string('s', PrOverviewSanitizer.MaxPrSummaryLength + 100) + "  ";

        var sanitized = PrOverviewSanitizer.Sanitize(Result(prSummary: longSummary), files);

        Assert.NotNull(sanitized.PrSummary);
        Assert.Equal(PrOverviewSanitizer.MaxPrSummaryLength, sanitized.PrSummary!.Length);
        Assert.DoesNotContain(" ", sanitized.PrSummary);
    }

    [Fact]
    public void Sanitize_WhitespacePrSummary_BecomesNull()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };

        var sanitized = PrOverviewSanitizer.Sanitize(Result(prSummary: "   "), files);

        Assert.Null(sanitized.PrSummary);
    }

    [Fact]
    public void Sanitize_InvalidDiagram_IsDropped()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };

        var sanitized = PrOverviewSanitizer.Sanitize(
            Result(diagram: "This is not a diagram at all."),
            files);

        Assert.Null(sanitized.ChangeDiagram);
    }

    [Fact]
    public void Sanitize_FencedDiagram_IsNormalized()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+x\n") };

        var sanitized = PrOverviewSanitizer.Sanitize(
            Result(diagram: "```mermaid\ngraph TD\n    A --> B\n```"),
            files);

        Assert.Equal("graph TD\n    A --> B", sanitized.ChangeDiagram);
    }

    [Fact]
    public void Sanitize_NoOverviewAndNoChangedFiles_ReturnsSameInstance()
    {
        var result = Result();

        var sanitized = PrOverviewSanitizer.Sanitize(result, []);

        Assert.Same(result, sanitized);
        Assert.Null(sanitized.ChangeStats);
    }

    [Fact]
    public void Sanitize_AttachesStatsEvenWithoutAiOverview()
    {
        var files = new List<ChangedFile> { File("src/A.cs", "+a\n-b\n") };

        var sanitized = PrOverviewSanitizer.Sanitize(Result(), files);

        Assert.Equal(new PrChangeStats(1, 1, 1), sanitized.ChangeStats);
        Assert.Null(sanitized.PrSummary);
        Assert.Null(sanitized.Walkthrough);
        Assert.Null(sanitized.ChangeDiagram);
    }

    [Fact]
    public void CountChangeStats_SumsAcrossFiles()
    {
        var files = new List<ChangedFile>
        {
            File("src/A.cs", "+a\n+b\n-c\n"),
            File("src/B.cs", "+d\n"),
            File("src/C.cs", string.Empty),
        };

        var stats = PrOverviewSanitizer.CountChangeStats(files);

        Assert.Equal(new PrChangeStats(3, 3, 1), stats);
    }
}
