// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class PrOverviewSummarySectionsTests
{
    private static ReviewResult Result(
        string? prSummary = null,
        PrChangeStats? stats = null,
        IReadOnlyList<PrWalkthroughEntry>? walkthrough = null,
        string? diagram = null) =>
        new("Review findings summary.", [])
        {
            PrSummary = prSummary,
            ChangeStats = stats,
            Walkthrough = walkthrough,
            ChangeDiagram = diagram,
        };

    [Fact]
    public void Append_NoOverview_AppendsNothing()
    {
        var builder = new StringBuilder();

        PrOverviewSummarySections.Append(builder, Result());

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_FullOverview_RendersSummaryStatsWalkthroughAndDiagramInOrder()
    {
        var result = Result(
            prSummary: "Adds a login cache so repeat visits skip the database.",
            stats: new PrChangeStats(2, 5, 1),
            walkthrough:
            [
                new PrWalkthroughEntry("src/Cache.cs", "Adds the cache itself."),
                new PrWalkthroughEntry("src/Login.cs", "Reads from the cache first."),
            ],
            diagram: "flowchart TD\n    A[Login] --> B[Cache]");

        var builder = new StringBuilder();
        PrOverviewSummarySections.Append(builder, result);
        var output = builder.ToString();

        Assert.Contains("### What this PR does", output, StringComparison.Ordinal);
        Assert.Contains("Adds a login cache so repeat visits skip the database.", output, StringComparison.Ordinal);
        Assert.Contains("**Changes:** 2 files changed, +5 additions, -1 deletions", output, StringComparison.Ordinal);
        Assert.Contains("### Walkthrough", output, StringComparison.Ordinal);
        Assert.Contains("- **src/Cache.cs:** Adds the cache itself.", output, StringComparison.Ordinal);
        Assert.Contains("- **src/Login.cs:** Reads from the cache first.", output, StringComparison.Ordinal);
        Assert.Contains("### Change diagram", output, StringComparison.Ordinal);
        Assert.Contains("AI-generated diagram", output, StringComparison.Ordinal);
        Assert.Contains("```mermaid\nflowchart TD\n    A[Login] --> B[Cache]\n```", output, StringComparison.Ordinal);

        // Summary first, then stats, then walkthrough, then diagram.
        Assert.True(
            output.IndexOf("### What this PR does", StringComparison.Ordinal)
            < output.IndexOf("**Changes:**", StringComparison.Ordinal));
        Assert.True(
            output.IndexOf("**Changes:**", StringComparison.Ordinal)
            < output.IndexOf("### Walkthrough", StringComparison.Ordinal));
        Assert.True(
            output.IndexOf("### Walkthrough", StringComparison.Ordinal)
            < output.IndexOf("### Change diagram", StringComparison.Ordinal));
    }

    [Fact]
    public void Append_StatsOnly_RendersStatsLine()
    {
        var builder = new StringBuilder();
        PrOverviewSummarySections.Append(builder, Result(stats: new PrChangeStats(1, 3, 0)));
        var output = builder.ToString();

        Assert.Contains("**Changes:** 1 file changed, +3 additions, -0 deletions", output, StringComparison.Ordinal);
        Assert.DoesNotContain("### What this PR does", output, StringComparison.Ordinal);
        Assert.DoesNotContain("### Walkthrough", output, StringComparison.Ordinal);
        Assert.DoesNotContain("### Change diagram", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_InvalidDiagram_IsOmittedSilently()
    {
        var builder = new StringBuilder();
        PrOverviewSummarySections.Append(
            builder,
            Result(prSummary: "Does the thing.", diagram: "This is not a diagram."));
        var output = builder.ToString();

        Assert.Contains("### What this PR does", output, StringComparison.Ordinal);
        Assert.DoesNotContain("### Change diagram", output, StringComparison.Ordinal);
        Assert.DoesNotContain("mermaid", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_FencedDiagram_IsRenderedUnfenced()
    {
        var builder = new StringBuilder();
        PrOverviewSummarySections.Append(
            builder,
            Result(diagram: "```mermaid\ngraph TD\n    A --> B\n```"));
        var output = builder.ToString();

        Assert.Contains("### Change diagram", output, StringComparison.Ordinal);
        Assert.Contains("```mermaid\ngraph TD\n    A --> B\n```", output, StringComparison.Ordinal);
        // Exactly one mermaid fence pair: the renderer must not nest the AI's fences.
        Assert.Equal(2, output.Split("```", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Append_WhitespaceSummaryAndEmptyWalkthrough_AreOmitted()
    {
        var builder = new StringBuilder();
        PrOverviewSummarySections.Append(
            builder,
            Result(prSummary: "   ", walkthrough: [new PrWalkthroughEntry(" ", "   ")]));
        var output = builder.ToString();

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void FormatChangeStats_UsesSingularFileWordForOneFile()
    {
        Assert.Equal(
            "**Changes:** 1 file changed, +0 additions, -2 deletions",
            PrOverviewSummarySections.FormatChangeStats(new PrChangeStats(1, 0, 2)));
    }
}
