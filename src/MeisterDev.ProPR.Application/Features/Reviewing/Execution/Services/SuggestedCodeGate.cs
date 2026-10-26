// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Why the suggested-code gate dropped a suggestion. Every reason is a fail-closed verdict: the
///     finding still publishes as plain text, only the unverified suggestion block is withheld.
/// </summary>
internal enum SuggestedCodeGateFailureReason
{
    /// <summary>The comment carries no suggestion at all; not a failure, only a pass-through.</summary>
    NoSuggestion = 0,

    /// <summary>The comment has no file path, so no head file can be looked up.</summary>
    NoFilePath,

    /// <summary>The file is not among the pull request's changed files, so no head content is available.</summary>
    FileNotInPullRequest,

    /// <summary>The changed file carries no head content to verify against.</summary>
    NoHeadContent,

    /// <summary>The suggestion range has no anchor line to default to.</summary>
    NoAnchorLine,

    /// <summary>The range is empty, inverted, or reaches past the end of the head file.</summary>
    InvalidRange,

    /// <summary>The reviewer supplied no original text, so the replacement cannot be verified.</summary>
    MissingOriginalCode,

    /// <summary>The head file's lines in the range do not equal the supplied original text.</summary>
    OriginalCodeMismatch,

    /// <summary>The replacement is identical to the original lines; it suggests nothing.</summary>
    NoOpReplacement,

    /// <summary>The replacement contains a markdown code fence, which would break the suggestion block.</summary>
    UnsafeCodeFence,

    /// <summary>The range touches no changed line of the pull request diff for this file.</summary>
    OutsideDiff,
}

/// <summary>
///     The outcome of running the suggested-code gate over one finding.
/// </summary>
/// <param name="Passed"><see langword="true" /> when the suggestion may be published as a suggestion block.</param>
/// <param name="FailureReason">
///     Why the suggestion was dropped, or <see cref="SuggestedCodeGateFailureReason.NoSuggestion" /> when the
///     comment carries no suggestion. <see langword="null" /> only when <paramref name="Passed" /> is true.
/// </param>
internal sealed record SuggestedCodeGateResult(bool Passed, SuggestedCodeGateFailureReason? FailureReason);

/// <summary>
///     Fail-closed verification for CodeRabbit-style one-click code suggestions. A suggestion is published
///     as a suggestion block only when every check below holds against the pull request head; any failure
///     drops the suggestion fields and the finding publishes as plain text. The gate is pure and
///     synchronous: it reads only the comment, the head file content, and the diff's changed ranges, so it
///     is unit-testable without a provider, a workspace, or the network.
///     <para>
///         Head content and diff ranges come from the pull request already in hand at publication time:
///         <see cref="ChangedFile.FullContent" /> (the head version, populated by the provider fetchers)
///         and <see cref="ReviewDiffProcessor.ExtractChangedNewLineRanges" /> over
///         <see cref="ChangedFile.UnifiedDiff" />. When that content is missing — a file outside the
///         changed set, a binary file, a deleted file, or a fetcher that left the content empty — the gate
///         fails closed rather than guessing.
///     </para>
/// </summary>
internal static class SuggestedCodeGate
{
    /// <summary>
    ///     Whether the comment carries a suggestion at all. A suggestion is present only when
    ///     <see cref="ReviewComment.SuggestedCode" /> is non-whitespace.
    /// </summary>
    public static bool HasSuggestion(ReviewComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return !string.IsNullOrWhiteSpace(comment.SuggestedCode);
    }

    /// <summary>
    ///     Verifies one finding's suggestion against the head version of its file and the pull request
    ///     diff's changed ranges for that file. Checks, in order: the suggestion is present; the
    ///     replacement contains no markdown code fence; the range is valid (start &lt;= end, both inside
    ///     the file, defaulting to the anchor line); the head lines in the range equal the supplied
    ///     original text exactly (line endings normalized); the replacement differs from the original;
    ///     and the range intersects a changed range of the diff.
    /// </summary>
    /// <param name="comment">The finding carrying the suggestion.</param>
    /// <param name="headFileContent">
    ///     The head version of the file's full content, or <see langword="null" /> when it could not be
    ///     obtained. A missing head file fails the gate; it is never treated as an empty file.
    /// </param>
    /// <param name="changedRanges">
    ///     The file's merged changed-line ranges from the pull request diff, or <see langword="null" />
    ///     when none could be resolved. No resolvable ranges fails the gate.
    /// </param>
    public static SuggestedCodeGateResult Evaluate(
        ReviewComment comment,
        string? headFileContent,
        IReadOnlyList<(int Start, int End)>? changedRanges)
    {
        ArgumentNullException.ThrowIfNull(comment);

        // (a) A suggestion is present only when the replacement code is non-whitespace.
        if (string.IsNullOrWhiteSpace(comment.SuggestedCode))
        {
            return new SuggestedCodeGateResult(true, SuggestedCodeGateFailureReason.NoSuggestion);
        }

        // A triple-backtick fence inside the replacement would terminate the suggestion block
        // the publisher renders around it, so such a snippet is never publishable as a suggestion.
        if (comment.SuggestedCode.Contains("```", StringComparison.Ordinal))
        {
            return Fail(SuggestedCodeGateFailureReason.UnsafeCodeFence);
        }

        // The range defaults to the anchor line; without an anchor there is nothing to default to.
        var start = comment.SuggestionStartLine ?? comment.LineNumber;
        var end = comment.SuggestionEndLine ?? comment.LineNumber;
        if (start is null || end is null)
        {
            return Fail(SuggestedCodeGateFailureReason.NoAnchorLine);
        }

        // Head content that could not be obtained fails closed: an unverifiable suggestion is never
        // published, and a missing file must not be mistaken for an empty one.
        if (headFileContent is null)
        {
            return Fail(SuggestedCodeGateFailureReason.NoHeadContent);
        }

        var headLines = SplitHeadLines(headFileContent);

        // (b) The range must be well-formed and lie inside the head file.
        if (start.Value < 1 || end.Value < start.Value || end.Value > headLines.Count)
        {
            return Fail(SuggestedCodeGateFailureReason.InvalidRange);
        }

        // (c) The original text is required: without it the replacement cannot be verified against the
        // head file, and guessing which lines it replaces would risk overwriting the wrong code.
        if (comment.SuggestedOriginalCode is null)
        {
            return Fail(SuggestedCodeGateFailureReason.MissingOriginalCode);
        }

        var originalLines = string.Join("\n", headLines.GetRange(start.Value - 1, end.Value - start.Value + 1));
        if (!string.Equals(originalLines, NormalizeLineEndings(comment.SuggestedOriginalCode), StringComparison.Ordinal))
        {
            return Fail(SuggestedCodeGateFailureReason.OriginalCodeMismatch);
        }

        // (d) A replacement identical to the original lines suggests nothing.
        if (string.Equals(NormalizeLineEndings(comment.SuggestedCode), originalLines, StringComparison.Ordinal))
        {
            return Fail(SuggestedCodeGateFailureReason.NoOpReplacement);
        }

        // (e) GitHub only applies suggestions on diff lines, so the range must intersect the diff's
        // changed ranges for this file. A missing or empty range set cannot prove that intersection.
        if (changedRanges is null || changedRanges.Count == 0
            || !changedRanges.Any(range => start.Value <= range.End && end.Value >= range.Start))
        {
            return Fail(SuggestedCodeGateFailureReason.OutsideDiff);
        }

        return new SuggestedCodeGateResult(true, null);
    }

    /// <summary>
    ///     Runs the gate over every comment in <paramref name="result" /> against the pull request's
    ///     changed files, stripping the suggestion fields from every finding that fails. Findings with no
    ///     suggestion, and findings whose suggestion passes, keep their exact instance. When nothing is
    ///     stripped the input result is returned untouched, so a suggestion-free review passes through
    ///     byte-identical in behaviour.
    /// </summary>
    /// <param name="result">The review result to gate.</param>
    /// <param name="changedFiles">
    ///     The pull request's changed files, carrying head content in
    ///     <see cref="ChangedFile.FullContent" /> and the diff in <see cref="ChangedFile.UnifiedDiff" />.
    /// </param>
    /// <returns>The gated result and how many suggestions were stripped from it.</returns>
    public static (ReviewResult Result, int StrippedCount) GateSuggestions(
        ReviewResult result,
        IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var changedByPath = new Dictionary<string, ChangedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            changedByPath[ReviewDiffProcessor.NormalizeReviewPath(file.Path)] = file;
        }

        var gated = new List<ReviewComment>(result.Comments.Count);
        var stripped = 0;
        foreach (var comment in result.Comments)
        {
            if (!HasSuggestion(comment))
            {
                gated.Add(comment);
                continue;
            }

            var outcome = EvaluateAgainstChangedFiles(comment, changedByPath);
            if (outcome.Passed)
            {
                gated.Add(comment);
            }
            else
            {
                gated.Add(StripSuggestion(comment));
                stripped++;
            }
        }

        // Returning the input itself when nothing changed is what keeps a suggestion-free review
        // byte-identical: same instance, same ordinals, same downstream reference pairing.
        return stripped == 0 ? (result, 0) : (result with { Comments = gated }, stripped);
    }

    /// <summary>
    ///     Returns a copy of <paramref name="comment" /> with the suggestion fields removed and every
    ///     other property — message, anchor, severity, and all provenance — carried over unchanged, so
    ///     the finding publishes as plain text.
    /// </summary>
    public static ReviewComment StripSuggestion(ReviewComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        return new ReviewComment(comment.FilePath, comment.LineNumber, comment.Severity, comment.Message)
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
        };
    }

    private static SuggestedCodeGateResult EvaluateAgainstChangedFiles(
        ReviewComment comment,
        IReadOnlyDictionary<string, ChangedFile> changedByPath)
    {
        if (comment.FilePath is null)
        {
            return Fail(SuggestedCodeGateFailureReason.NoFilePath);
        }

        if (!changedByPath.TryGetValue(ReviewDiffProcessor.NormalizeReviewPath(comment.FilePath), out var file))
        {
            return Fail(SuggestedCodeGateFailureReason.FileNotInPullRequest);
        }

        // A binary file carries no head content and no resolvable diff ranges; a deleted file's head
        // content is empty. Both are unverifiable, so both fail closed here rather than in Evaluate.
        if (file.IsBinary || string.IsNullOrEmpty(file.FullContent))
        {
            return Fail(SuggestedCodeGateFailureReason.NoHeadContent);
        }

        return Evaluate(comment, file.FullContent, ReviewDiffProcessor.ExtractChangedNewLineRanges(file.UnifiedDiff));
    }

    private static SuggestedCodeGateResult Fail(SuggestedCodeGateFailureReason reason) =>
        new(false, reason);

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    ///     Splits head content into its 1-based lines. A trailing newline terminates the last line rather
    ///     than starting an empty one, so the line count is the count of real lines.
    /// </summary>
    private static List<string> SplitHeadLines(string headFileContent)
    {
        var lines = NormalizeLineEndings(headFileContent).Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
