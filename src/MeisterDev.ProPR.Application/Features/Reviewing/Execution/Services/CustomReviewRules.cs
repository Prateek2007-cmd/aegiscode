// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Pure enforcement for explicit per-client review rules (see
///     <see cref="ICustomReviewRuleService" />). Everything here is a pure function of
///     its inputs — no provider, workspace, or network — so it is unit-testable on its
///     own. Two guarantees hold throughout:
///     <list type="bullet">
///         <item>A rule never invents a finding: a match is always a literal pattern hit on a real file path, real file content, or real finding text.</item>
///         <item>A rule never downgrades a finding: severity only ever moves up, to the rule's severity.</item>
///     </list>
///     Enforcement runs at publication, after the reviewer-memory filter and before
///     the publication policy, so an upgraded severity is what the policy and the
///     pull request actually see.
/// </summary>
public static class CustomReviewRules
{
    /// <summary>Longest pattern a rule may carry.</summary>
    public const int MaxPatternLength = 500;

    /// <summary>Longest rule name or message.</summary>
    public const int MaxNameLength = 120;

    /// <summary>Longest rule message.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>Longest matched-text snippet quoted as evidence on an added finding.</summary>
    public const int MaxEvidenceLength = 200;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Validates a rule pattern without storing anything. A substring pattern must be
    ///     non-empty; a regex must compile. Returns <c>false</c> with the real error
    ///     message when invalid — callers surface that message as the 400, never a guess.
    /// </summary>
    public static bool TryValidatePattern(string? pattern, bool isRegex, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "pattern is required.";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = $"pattern must be at most {MaxPatternLength.ToString(CultureInfo.InvariantCulture)} characters.";
            return false;
        }

        if (!isRegex)
        {
            error = null;
            return true;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Parses a rule severity ("Error", "Warning", "Suggestion", "Info", case-insensitive). <c>false</c> for anything else.</summary>
    public static bool TryParseSeverity(string? severity, out CommentSeverity parsed)
    {
        if (Enum.TryParse<CommentSeverity>(severity?.Trim(), ignoreCase: true, out var value)
            && Enum.IsDefined(value))
        {
            parsed = value;
            return true;
        }

        parsed = CommentSeverity.Info;
        return false;
    }

    /// <summary>
    ///     Whether the rule's pattern literally hits the file path or the content. A regex
    ///     that times out never matches; a disabled rule never matches. When it matches,
    ///     <paramref name="matchedText" /> carries the literal evidence: the matched path
    ///     for a path hit, or the first matching line (trimmed, capped) for a content hit.
    /// </summary>
    public static bool Matches(CustomReviewRule rule, string? filePath, string? content, out string? matchedText)
    {
        matchedText = null;
        if (rule is null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filePath) && TryMatchOne(rule, filePath, out var pathHit))
        {
            matchedText = pathHit;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(content) && TryMatchOne(rule, content, out var contentHit))
        {
            matchedText = contentHit;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     The scan severity a rule severity maps to when a scan finding is upgraded:
    ///     Error → Critical, Warning → High, Suggestion → Medium, Info → Info. A scan
    ///     finding is only ever upgraded, never downgraded.
    /// </summary>
    public static string ScanSeverityForRule(CommentSeverity ruleSeverity) => ruleSeverity switch
    {
        CommentSeverity.Error => "Critical",
        CommentSeverity.Warning => "High",
        CommentSeverity.Suggestion => "Medium",
        _ => "Info",
    };

    /// <summary>
    ///     Upgrades a scan finding's severity string to the rule's mapped severity when
    ///     the rule is more severe; otherwise returns the finding's severity unchanged.
    ///     Scan ranks: Critical (4) &gt; High (3) &gt; Medium (2) &gt; Low (1) &gt; Info (0).
    /// </summary>
    public static string UpgradeScanSeverity(string ruleSeverity, string findingSeverity)
    {
        if (!TryParseSeverity(ruleSeverity, out var rule))
        {
            return findingSeverity;
        }

        var ruleScan = ScanSeverityForRule(rule);
        return ScanRank(ruleScan) > ScanRank(findingSeverity) ? ruleScan : findingSeverity;
    }

    /// <summary>One-line citation naming the rule that matched, appended to a finding's message or title.</summary>
    public static string Citation(CustomReviewRule rule) =>
        $"[Custom rule \"{rule.Name}\": {rule.Message}]";

    /// <summary>
    ///     Applies the client's enabled rules to a review result at publication:
    ///     <list type="bullet">
    ///         <item>An existing finding whose file path or message literally matches a rule is cited with the rule name and upgraded to the rule's severity when that is higher.</item>
    ///         <item>A rule that literally matches a changed file's path or full content, with no existing finding on that file matching the same rule, adds one finding for that file — the match is real code, quoted as evidence, never invented.</item>
    ///     </list>
    ///     Returns the input untouched (same instance, counts 0) when there are no enabled
    ///     rules or nothing matches, so the default configuration publishes exactly what
    ///     the reviewer produced.
    /// </summary>
    public static (ReviewResult Result, int UpgradedCount, int AddedCount) Apply(
        ReviewResult result,
        IReadOnlyList<CustomReviewRule> rules,
        IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var enabled = rules.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0 || (result.Comments.Count == 0 && changedFiles.Count == 0))
        {
            return (result, 0, 0);
        }

        var comments = result.Comments.ToList();
        var upgraded = 0;

        for (var i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];
            var rule = FirstMatchingRule(enabled, comment.FilePath, comment.Message);
            if (rule is null)
            {
                continue;
            }

            if (!TryParseSeverity(rule.Severity, out var ruleSeverity))
            {
                continue;
            }

            var newSeverity = ruleSeverity.Rank() > comment.Severity.Rank() ? ruleSeverity : comment.Severity;
            if (newSeverity != comment.Severity)
            {
                upgraded++;
            }

            comments[i] = WithSeverityAndMessage(comment, newSeverity, comment.Message + " " + Citation(rule));
        }

        // Added findings: one per (rule, changed file) where the rule literally hits
        // the path or the full content and no finding on that file already matches the
        // same rule. Binary files carry no content and are listed by name only, so a
        // path hit on one still counts — the path itself is real.
        var added = 0;
        var addedComments = new List<ReviewComment>();
        foreach (var file in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            foreach (var rule in enabled)
            {
                if (!TryParseSeverity(rule.Severity, out var ruleSeverity))
                {
                    continue;
                }

                if (comments.Concat(addedComments).Any(c =>
                        string.Equals(c.FilePath, file.Path, StringComparison.OrdinalIgnoreCase)
                        && Matches(rule, c.FilePath, c.Message, out _)))
                {
                    continue;
                }

                if (!Matches(rule, file.Path, file.IsBinary ? null : file.FullContent, out var matchedText))
                {
                    continue;
                }

                var line = FindMatchLine(file.IsBinary ? null : file.FullContent, rule, matchedText);
                var message = rule.Message + " " + Citation(rule)
                              + (matchedText is null ? string.Empty : $" Matched: {matchedText}");
                addedComments.Add(new ReviewComment(file.Path, line, ruleSeverity, message));
                added++;
            }
        }

        if (upgraded == 0 && added == 0)
        {
            return (result, 0, 0);
        }

        comments.AddRange(addedComments);
        var updated = result with { Comments = comments.AsReadOnly() };
        return (updated, upgraded, added);
    }

    private static CustomReviewRule? FirstMatchingRule(IReadOnlyList<CustomReviewRule> rules, string? filePath, string? content)
    {
        foreach (var rule in rules)
        {
            if (Matches(rule, filePath, content, out _))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool TryMatchOne(CustomReviewRule rule, string text, out string? matchedText)
    {
        matchedText = null;
        if (rule.IsRegex)
        {
            Regex regex;
            try
            {
                regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return false;
            }

            Match match;
            try
            {
                match = regex.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            if (!match.Success)
            {
                return false;
            }

            matchedText = TruncateEvidence(FirstLineContaining(text, match.Index) ?? match.Value);
            return true;
        }

        var index = text.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        matchedText = TruncateEvidence(FirstLineContaining(text, index) ?? rule.Pattern);
        return true;
    }

    private static string? FirstLineContaining(string text, int index)
    {
        if (index < 0 || index > text.Length)
        {
            return null;
        }

        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var lineEnd = text.IndexOf('\n', index);
        var line = lineEnd < 0 ? text[lineStart..] : text[lineStart..lineEnd];
        line = line.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static int? FindMatchLine(string? content, CustomReviewRule rule, string? matchedText)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(matchedText))
        {
            return null;
        }

        // The evidence is the first matching line; its 1-based number is the anchor.
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Matches(rule, null, lines[i], out _))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static string TruncateEvidence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > MaxEvidenceLength ? trimmed[..MaxEvidenceLength].TrimEnd() + "…" : trimmed;
    }

    private static int ScanRank(string severity) => severity.Trim().ToLowerInvariant() switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    /// <summary>Rebuilds a comment with a new severity and message, preserving every piece of provenance the original holds.</summary>
    private static ReviewComment WithSeverityAndMessage(ReviewComment comment, CommentSeverity severity, string message)
    {
        return new ReviewComment(comment.FilePath, comment.LineNumber, severity, message)
        {
            OriginPassKind = comment.OriginPassKind,
            OriginPassIndex = comment.OriginPassIndex,
            OriginPassLens = comment.OriginPassLens,
            OriginPassShadow = comment.OriginPassShadow,
            ScopeRelation = comment.ScopeRelation,
            SourceReadGrounding = comment.SourceReadGrounding,
            OriginModelId = comment.OriginModelId,
            OriginLogicalModelName = comment.OriginLogicalModelName,
            OriginSymbolName = comment.OriginSymbolName,
            OriginSymbolKind = comment.OriginSymbolKind,
            SuggestedCode = comment.SuggestedCode,
            SuggestionStartLine = comment.SuggestionStartLine,
            SuggestionEndLine = comment.SuggestionEndLine,
            SuggestedOriginalCode = comment.SuggestedOriginalCode,
        };
    }
}
