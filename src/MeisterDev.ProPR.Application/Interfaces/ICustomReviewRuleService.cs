// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     One explicit, team-defined review rule for one client (repo). Distinct from the
///     learning reviewer, which adapts implicitly from accept/dismiss decisions: these
///     rules are written down by a human and apply exactly as written. A rule never
///     invents a finding — enforcement only ever matches its pattern literally against
///     a real file path or real file/finding content.
/// </summary>
public sealed record CustomReviewRule(
    Guid Id,
    string Name,
    string Pattern,
    bool IsRegex,
    string Severity,
    string Message,
    bool Enabled);

/// <summary>
///     Explicit per-client review rules. Implementations are client-scoped: a client
///     can only ever see its own rules. Enforcement reads only the enabled rules.
/// </summary>
public interface ICustomReviewRuleService
{
    /// <summary>
    ///     The client's enabled rules, in creation order. Empty when the client defined
    ///     none — never a fabricated rule. Implementations must degrade gracefully: a
    ///     caller that cannot load rules enforces none rather than failing the review.
    /// </summary>
    Task<IReadOnlyList<CustomReviewRule>> ListEnabledAsync(Guid clientId, CancellationToken ct);
}
