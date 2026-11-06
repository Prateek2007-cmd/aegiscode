// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Fail-closed publication filter for muted rules. A finding whose rule the client muted
///     never reaches the pull request: it is withheld here, counted, and named in the summary,
///     while the persisted result keeps every finding — a muted finding is hidden from the
///     pull request, never deleted from the review record. The filter is pure and synchronous:
///     it reads only the result and the muted keys, so it is unit-testable without a provider,
///     a workspace, or the network. It runs after the suggested-code gate and the overview
///     sanitizer, and before the publication policy, so the policy's withheld account and this
///     muted account never double-count a finding.
/// </summary>
internal static class ReviewerMemoryFilter
{
    /// <summary>
    ///     Withholds findings whose rule is muted for this client. Returns the input untouched
    ///     (same instance, count 0) when nothing is muted or nothing matches, so the default
    ///     configuration publishes exactly what the reviewer produced.
    /// </summary>
    /// <param name="result">The full review result, as prepared for publication.</param>
    /// <param name="mutedRuleKeys">Rule keys muted for this client; empty means nothing is muted.</param>
    public static (ReviewResult Filtered, int MutedCount) Apply(
        ReviewResult result,
        IReadOnlySet<string> mutedRuleKeys)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mutedRuleKeys);

        if (mutedRuleKeys.Count == 0 || result.Comments.Count == 0)
        {
            return (result, 0);
        }

        var kept = new List<ReviewComment>(result.Comments.Count);
        var mutedCount = 0;

        foreach (var comment in result.Comments)
        {
            // The key is derived from the message the same way recording derives it, so a
            // rule muted from a dismissal matches the findings it was muted for. A comment
            // with no derivable key can never match a muted rule and always publishes.
            var key = ReviewerMemoryRules.KeyForReviewFinding(comment.Message);
            if (key is not null && mutedRuleKeys.Contains(key))
            {
                mutedCount++;
                continue;
            }

            kept.Add(comment);
        }

        if (mutedCount == 0)
        {
            return (result, 0);
        }

        var filtered = result with
        {
            Comments = kept.AsReadOnly(),
            Summary = AppendMutedNote(result.Summary, mutedCount),
        };

        return (filtered, mutedCount);
    }

    /// <summary>
    ///     Appends the muted-findings account to a review summary. A blank summary is replaced by
    ///     the account rather than left blank, so a review whose every finding was muted still says
    ///     something true on the pull request instead of reading as a review that found nothing.
    /// </summary>
    private static string AppendMutedNote(string summary, int mutedCount)
    {
        var subject = mutedCount == 1
            ? "1 finding was"
            : $"{mutedCount.ToString(CultureInfo.InvariantCulture)} findings were";

        var note = $"{subject} muted by reviewer memory — the client dismissed this kind of finding " +
                   "repeatedly — and kept in the review record. See the Reviewer memory panel on the " +
                   "client dashboard for the muted rules.";

        return string.IsNullOrWhiteSpace(summary)
            ? note
            : summary.TrimEnd() + "\n\n" + note;
    }
}
