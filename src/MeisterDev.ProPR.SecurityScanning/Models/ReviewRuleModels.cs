// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     One explicit, team-defined review rule as persisted in the per-client JSON
///     store. Kept separate from the API-facing <c>ReviewRuleEntry</c> so storage can
///     evolve without changing the API contract. Rules are written down by a human and
///     apply exactly as written — unlike reviewer memory, nothing here is learned or
///     inferred. <c>Severity</c> is one of "Error", "Warning", "Suggestion", "Info".
/// </summary>
public sealed record ReviewRuleRecord(
    Guid Id,
    Guid ClientId,
    string Name,
    string Pattern,
    bool IsRegex,
    string Severity,
    string Message,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
