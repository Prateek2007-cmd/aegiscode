// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     One learned rule as persisted in the per-client JSON store. Kept separate from the
///     API-facing <c>ReviewerMemoryEntry</c> so storage can evolve without changing the API
///     contract. Counts come only from human decisions actually recorded; nothing is inferred.
/// </summary>
public sealed record ReviewerMemoryRecord(
    Guid ClientId,
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
