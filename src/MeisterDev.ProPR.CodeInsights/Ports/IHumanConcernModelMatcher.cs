// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Ports;

public enum HumanConcernMatchState
{
    Unknown,
    Match,
    NoMatch
}

public sealed record HumanConcernReference(string Id, string Discussion);

public sealed record HumanConcernModelMatchRequest(
    Guid ClientId,
    Guid ConcernId,
    string Discussion,
    IReadOnlyList<ModelReviewCandidate> Candidates,
    IReadOnlyList<HumanConcernReference>? OtherConcerns = null);

public sealed record HumanConcernModelMatchResult(
    HumanConcernMatchState State,
    IReadOnlyList<string> MatchedFindingIds,
    IReadOnlyList<string> EquivalentConcernIds,
    IReadOnlyList<string> EvaluatedCandidateIds,
    bool ModelWasAsked,
    string? UnavailableReason = null,
    double? Confidence = null,
    string? Rationale = null);

/// <summary>Matches a retained concern against explicit, already compatible candidate identities.</summary>
public interface IHumanConcernModelMatcher
{
    string ClassifierVersion { get; }
    Task<HumanConcernModelMatchResult> MatchAsync(HumanConcernModelMatchRequest request, CancellationToken ct = default);
}
