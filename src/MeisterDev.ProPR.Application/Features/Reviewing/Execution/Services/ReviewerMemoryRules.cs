// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The shared vocabulary of reviewer memory: how a finding becomes a learnable rule key,
///     and when repeated dismissals mute that rule for a client.
///     <para>
///         A rule key is a heuristic, not an identity. Review findings carry no rule id, so a
///         review rule is keyed by the normalized first line of the finding message, and a scan
///         rule by the normalized finding title. Two findings with the same first line share a
///         rule; that is the documented approximation, and the memory panel always shows the
///         label so a human can see exactly what was learned. Nothing here is ever invented:
///         keys and labels come only from findings and decisions the user actually made.
///     </para>
/// </summary>
public static class ReviewerMemoryRules
{
    /// <summary>Dismissals with no accepts after which a rule mutes itself for the client.</summary>
    public const int MuteAfterDismissals = 5;

    /// <summary>Most learned-guidance lines a review prompt ever carries.</summary>
    public const int MaxPromptGuidanceLines = 10;

    /// <summary>Longest label stored or rendered for a rule.</summary>
    public const int MaxLabelLength = 120;

    /// <summary>Rule-key source for AI review findings.</summary>
    public const string ReviewSource = "review";

    /// <summary>Rule-key source for security scan findings.</summary>
    public const string ScanSource = "scan";

    /// <summary>Derives the rule key for a review finding from its message. <c>null</c> when the message carries no usable text.</summary>
    public static string? KeyForReviewFinding(string? message) => KeyFor(ReviewSource, message);

    /// <summary>Derives the rule key for a scan finding from its title. <c>null</c> when the title carries no usable text.</summary>
    public static string? KeyForScanFinding(string? title) => KeyFor(ScanSource, title);

    /// <summary>
    ///     Derives the rule key for a fix proposal from its source and title. The finding ref
    ///     identifies one finding instance (job + line), not the rule, so it is deliberately
    ///     ignored; the title carries the rule. <c>null</c> for an unknown source or an unusable title.
    /// </summary>
    public static string? KeyForFixProposal(string? source, string? findingRef, string? title)
    {
        _ = findingRef;
        return source?.Trim().ToLowerInvariant() switch
        {
            ScanSource => KeyForScanFinding(title),
            ReviewSource => KeyForReviewFinding(title),
            _ => null,
        };
    }

    /// <summary>Whether a rule with these counts mutes itself: dismissed repeatedly, never accepted.</summary>
    public static bool ShouldMute(int accepts, int dismissals) =>
        dismissals >= MuteAfterDismissals && accepts == 0;

    /// <summary>
    ///     Normalizes a caller-supplied rule key ("source:normalized-title"), or <c>null</c> when it
    ///     is not a well-formed key for a known source.
    /// </summary>
    public static string? NormalizeRuleKey(string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey))
        {
            return null;
        }

        var key = ruleKey.Trim().ToLowerInvariant();
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon == key.Length - 1)
        {
            return null;
        }

        var source = key[..colon];
        if (source != ReviewSource && source != ScanSource)
        {
            return null;
        }

        var rest = NormalizeTitle(key[(colon + 1)..]);
        return rest is null ? null : source + ":" + rest;
    }

    /// <summary>
    ///     Normalizes a finding title to its learnable form: first line, lowercased, whitespace
    ///     collapsed, surrounding punctuation stripped, capped at <see cref="MaxLabelLength" />.
    ///     <c>null</c> when nothing usable remains.
    /// </summary>
    public static string? NormalizeTitle(string? title)
    {
        var first = FirstLine(title);
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var collapsed = string.Join(
            " ",
            first.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmed = collapsed.Trim(' ', '"', '\'', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '`');
        if (trimmed.Length > MaxLabelLength)
        {
            trimmed = trimmed[..MaxLabelLength].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    ///     The human-readable label for a rule: the finding's first line in its original case,
    ///     trimmed and capped at <see cref="MaxLabelLength" />. <c>null</c> when nothing usable remains.
    /// </summary>
    public static string? DisplayLabel(string? title)
    {
        var first = FirstLine(title);
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var trimmed = first.Trim();
        if (trimmed.Length > MaxLabelLength)
        {
            trimmed = trimmed[..MaxLabelLength].TrimEnd() + "…";
        }

        return trimmed;
    }

    /// <summary>First non-empty line of a message, or <c>null</c> when there is none.</summary>
    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }

    /// <summary>
    ///     One prompt-guidance line for a muted rule. The dismissed-patterns template already
    ///     carries the "do not report" instruction, so the line names the pattern and states
    ///     exactly why it was muted — the evidence, not a claim.
    /// </summary>
    public static string GuidanceLine(string label, int dismissals) =>
        $"\"{label}\" (dismissed {dismissals} times, never accepted — muted by reviewer memory)";

    private static string? KeyFor(string source, string? title)
    {
        var normalized = NormalizeTitle(title);
        return normalized is null ? null : source + ":" + normalized;
    }
}
