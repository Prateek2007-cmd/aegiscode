// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Renders the AI-produced pull-request overview (PR summary, change statistics, file
///     walkthrough, change diagram) at the top of a review summary, shared by the plain-markdown
///     providers. Every section is optional and fail-closed: a section the AI did not produce, or
///     that fails validation here, is omitted silently — never invented, never posted as filler.
///     The diagram is re-validated with <see cref="ChangeDiagramGate" /> at render time even though
///     orchestration gates it first, because a <see cref="ReviewResult" /> can be constructed
///     anywhere and the publisher must not trust that the earlier gate already ran.
/// </summary>
public static class PrOverviewSummarySections
{
    /// <summary>
    ///     Appends the overview sections to <paramref name="builder" /> when the result carries any:
    ///     the PR summary first, then the change statistics counted from the actual diff, then the
    ///     file walkthrough, then the AI-generated Mermaid diagram. No output when the result
    ///     carries no usable overview.
    /// </summary>
    /// <param name="builder">The summary builder to append to.</param>
    /// <param name="result">The review result carrying the overview, if any.</param>
    public static void Append(StringBuilder builder, ReviewResult result)
    {
        var hasPrSummary = !string.IsNullOrWhiteSpace(result.PrSummary);
        var hasChangeStats = result.ChangeStats is not null;
        var walkthrough = UsableWalkthrough(result.Walkthrough);
        var hasDiagram = ChangeDiagramGate.TryNormalize(result.ChangeDiagram, out var diagram, out _);

        if (!hasPrSummary && !hasChangeStats && walkthrough.Count == 0 && !hasDiagram)
        {
            return;
        }

        // Sections are separated by one blank line; no leading blank line — the caller ends
        // the header with one.
        var firstSection = true;

        if (hasPrSummary)
        {
            builder.AppendLine("### What this PR does");
            builder.AppendLine();
            builder.AppendLine(result.PrSummary!.Trim());
            firstSection = false;
        }

        if (hasChangeStats)
        {
            if (!firstSection)
            {
                builder.AppendLine();
            }

            builder.AppendLine(FormatChangeStats(result.ChangeStats!));
            firstSection = false;
        }

        if (walkthrough.Count > 0)
        {
            if (!firstSection)
            {
                builder.AppendLine();
            }

            builder.AppendLine("### Walkthrough");
            builder.AppendLine();
            foreach (var entry in walkthrough)
            {
                builder.AppendLine($"- **{entry.FilePath}:** {entry.Description}");
            }

            firstSection = false;
        }

        if (hasDiagram)
        {
            if (!firstSection)
            {
                builder.AppendLine();
            }

            builder.AppendLine("### Change diagram");
            builder.AppendLine();
            builder.AppendLine("_AI-generated diagram of how the changed pieces connect:_");
            builder.AppendLine();
            builder.AppendLine("```mermaid");
            builder.AppendLine(diagram);
            builder.AppendLine("```");
        }
    }

    /// <summary>
    ///     Formats the one-line change-statistics note. The numbers come from the actual diffs, so
    ///     the line is only ever rendered from counted data, never from the AI's output.
    /// </summary>
    public static string FormatChangeStats(PrChangeStats stats)
    {
        var fileWord = stats.FilesChanged == 1 ? "file" : "files";
        return $"**Changes:** {stats.FilesChanged} {fileWord} changed, +{stats.LinesAdded} additions, -{stats.LinesRemoved} deletions";
    }

    private static IReadOnlyList<PrWalkthroughEntry> UsableWalkthrough(IReadOnlyList<PrWalkthroughEntry>? walkthrough)
    {
        if (walkthrough is null || walkthrough.Count == 0)
        {
            return [];
        }

        return walkthrough
            .Where(entry => entry is not null
                && !string.IsNullOrWhiteSpace(entry.FilePath)
                && !string.IsNullOrWhiteSpace(entry.Description))
            .Select(entry => new PrWalkthroughEntry(entry.FilePath.Trim(), entry.Description.Trim()))
            .ToList();
    }
}
