// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Fail-closed preparation of the AI-produced pull-request overview (PR summary, file
///     walkthrough, change diagram) plus the deterministically counted change statistics, applied in
///     orchestration just before publication — next to <see cref="SuggestedCodeGate" />. Nothing here
///     invents content: a missing summary, walkthrough, or diagram stays missing; a walkthrough entry
///     for a file that is not part of the pull request is dropped; a diagram that fails
///     <see cref="ChangeDiagramGate" /> is dropped; change statistics are counted from the actual
///     diffs, never taken from the AI. When nothing is present and nothing changes, the input result
///     is returned untouched.
/// </summary>
internal static class PrOverviewSanitizer
{
    /// <summary>Maximum accepted PR-summary length, in characters.</summary>
    public const int MaxPrSummaryLength = 2000;

    /// <summary>Maximum accepted walkthrough-entry description length, in characters.</summary>
    public const int MaxWalkthroughDescriptionLength = 500;

    /// <summary>
    ///     Sanitizes the overview fields of <paramref name="result" /> against the pull request's
    ///     changed files and attaches the change statistics counted from their diffs.
    /// </summary>
    /// <param name="result">The review result carrying the AI-produced overview, if any.</param>
    /// <param name="changedFiles">The pull request's changed files, carrying the diffs.</param>
    /// <returns>The sanitized result; the input instance when nothing changed.</returns>
    public static ReviewResult Sanitize(ReviewResult result, IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var prSummary = SanitizePrSummary(result.PrSummary);
        var walkthrough = SanitizeWalkthrough(result.Walkthrough, changedFiles);
        var changeDiagram = SanitizeDiagram(result.ChangeDiagram);
        // With no changed files there is nothing to count; keep whatever stats the result
        // already carries so a result with no overview at all passes through untouched.
        var changeStats = changedFiles.Count == 0 ? result.ChangeStats : CountChangeStats(changedFiles);

        if (prSummary == result.PrSummary
            && WalkthroughEquals(walkthrough, result.Walkthrough)
            && changeDiagram == result.ChangeDiagram
            && Equals(changeStats, result.ChangeStats))
        {
            return result;
        }

        return result with
        {
            PrSummary = prSummary,
            Walkthrough = walkthrough,
            ChangeDiagram = changeDiagram,
            ChangeStats = changeStats,
        };
    }

    /// <summary>
    ///     Counts change statistics from the actual diffs: every changed file counts, added/removed
    ///     lines are summed from the unified diffs (binary files carry no diff lines).
    /// </summary>
    public static PrChangeStats CountChangeStats(IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        var added = 0;
        var removed = 0;
        foreach (var file in changedFiles)
        {
            var (fileAdded, fileRemoved) = ReviewDiffProcessor.CountAddedAndRemovedLines(file.UnifiedDiff);
            added += fileAdded;
            removed += fileRemoved;
        }

        return new PrChangeStats(changedFiles.Count, added, removed);
    }

    private static string? SanitizePrSummary(string? prSummary)
    {
        if (string.IsNullOrWhiteSpace(prSummary))
        {
            return null;
        }

        var text = prSummary.Trim();
        return text.Length > MaxPrSummaryLength ? text[..MaxPrSummaryLength].TrimEnd() : text;
    }

    private static IReadOnlyList<PrWalkthroughEntry>? SanitizeWalkthrough(
        IReadOnlyList<PrWalkthroughEntry>? walkthrough,
        IReadOnlyList<ChangedFile> changedFiles)
    {
        if (walkthrough is null || walkthrough.Count == 0)
        {
            return null;
        }

        var changedPaths = new HashSet<string>(
            changedFiles.Select(file => ReviewDiffProcessor.NormalizeReviewPath(file.Path)),
            StringComparer.OrdinalIgnoreCase);

        var kept = new List<PrWalkthroughEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in walkthrough)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath) || string.IsNullOrWhiteSpace(entry.Description))
            {
                continue;
            }

            var path = ReviewDiffProcessor.NormalizeReviewPath(entry.FilePath);
            // An entry for a file that is not in the pull request cannot describe this change:
            // drop it rather than publish an invented file.
            if (!changedPaths.Contains(path) || !seen.Add(path))
            {
                continue;
            }

            var description = CollapseWhitespace(entry.Description);
            if (description.Length > MaxWalkthroughDescriptionLength)
            {
                description = description[..MaxWalkthroughDescriptionLength].TrimEnd();
            }

            kept.Add(new PrWalkthroughEntry(path, description));
        }

        return kept.Count == 0 ? null : kept.AsReadOnly();
    }

    private static string? SanitizeDiagram(string? diagram)
    {
        return ChangeDiagramGate.TryNormalize(diagram, out var normalized, out _)
            ? normalized
            : null;
    }

    private static bool WalkthroughEquals(
        IReadOnlyList<PrWalkthroughEntry>? left,
        IReadOnlyList<PrWalkthroughEntry>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.SequenceEqual(right);
    }

    /// <summary>
    ///     Flattens line breaks and runs of whitespace to single spaces, so a walkthrough entry
    ///     always renders as one bullet line in the published comment.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var parts = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }
}
