// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>A human outcome on a finding: the reviewer applied the suggestion, or dismissed it.</summary>
public enum ReviewerMemoryOutcome
{
    Accepted = 0,
    Dismissed = 1,
}

/// <summary>
///     What the reviewer has learned about one rule for one client: how often the human
///     accepted and dismissed it, and whether it is currently muted. Counts come only from
///     decisions the user actually made — nothing here is inferred or invented.
/// </summary>
public sealed record ReviewerMemoryEntry(
    string RuleKey,
    string Label,
    string Source,
    int Accepts,
    int Dismissals,
    bool Muted,
    DateTimeOffset? MutedAt,
    string? MutedReason,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastOutcomeAt);

/// <summary>
///     Reviewer memory: the learning side of review. Outcomes recorded here drive the
///     auto-mute threshold, the per-client prompt guidance, and the publication filter
///     that withholds muted-rule findings. Implementations are client-scoped: a client
///     can only ever see its own learned rules.
/// </summary>
public interface IReviewerMemoryService
{
    /// <summary>
    ///     Records one human outcome for a rule, creating the rule's memory on first sight.
    ///     Auto-mutes the rule when it reaches the dismissal threshold with no accepts.
    ///     Throws <see cref="ArgumentException" /> on an invalid rule key, label, or source.
    /// </summary>
    Task<ReviewerMemoryEntry> RecordOutcomeAsync(
        Guid clientId,
        string ruleKey,
        string label,
        string source,
        ReviewerMemoryOutcome outcome,
        CancellationToken ct);

    /// <summary>Lists the client's learned rules, most recently active first. Never returns another client's rules.</summary>
    Task<IReadOnlyList<ReviewerMemoryEntry>> ListAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Un-mutes a muted rule, keeping its counts: the reviewer will flag it again, and it
    ///     can mute again if dismissals keep coming. Returns <c>null</c> when the rule is unknown for this client.
    /// </summary>
    Task<ReviewerMemoryEntry?> UnmuteAsync(Guid clientId, string ruleKey, CancellationToken ct);

    /// <summary>
    ///     Forgets a rule entirely — counts and mute state are deleted, so the reviewer starts
    ///     from zero on it. Returns <c>false</c> when the rule was unknown for this client.
    /// </summary>
    Task<bool> ResetAsync(Guid clientId, string ruleKey, CancellationToken ct);

    /// <summary>Rule keys currently muted for the client. Empty when the reviewer has muted nothing.</summary>
    Task<IReadOnlySet<string>> GetMutedRuleKeysAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     The learned "do not flag" guidance for the client's next review: one labelled line
    ///     per muted rule, capped. Empty when nothing is muted — never a fabricated line.
    /// </summary>
    Task<IReadOnlyList<string>> BuildPromptGuidanceAsync(Guid clientId, CancellationToken ct);
}
