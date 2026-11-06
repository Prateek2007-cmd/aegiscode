// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     Request to record one human outcome for a rule. <c>RuleKey</c> may be omitted, in
///     which case it is derived from <c>Label</c> and <c>Source</c> the same way the
///     recording paths derive it; an unusable label or source is a 400, never a guess.
/// </summary>
public sealed record RecordReviewerMemoryOutcomeRequest(
    string? RuleKey,
    string? Label,
    string Source,
    string Outcome);

/// <summary>Request identifying one learned rule, for un-mute and reset.</summary>
public sealed record ReviewerMemoryRuleRequest(string RuleKey);

/// <summary>List envelope for learned rules, most recently active first.</summary>
public sealed record ReviewerMemoryListResponse(IReadOnlyList<ReviewerMemoryEntry> Items);

/// <summary>The learned "do not flag" guidance lines for the client's next review, possibly empty.</summary>
public sealed record ReviewerMemoryGuidanceResponse(IReadOnlyList<string> Guidance);
