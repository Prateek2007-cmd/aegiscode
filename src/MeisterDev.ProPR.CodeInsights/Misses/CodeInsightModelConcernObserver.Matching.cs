// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Misses;

public sealed partial class CodeInsightModelConcernObserver
{
    internal static bool Retryable(string? reason) => reason is "model-concern-judgement-failed" or "model-concern-match-unavailable";

    private async Task<FindingTypeVerdict?> ClassifyDimensionsAsync(
        ThreadUpdatedEvent observation, CodeInsightHumanConcern? current,
        bool same, HumanMissJudgement? judgement, string discussion, CancellationToken ct)
    {
        if (judgement is not { IsSubstantive: true, IsHumanConfirmed: true, IsSingleConcern: true } || discussion.Length > 6000
                                                                                                    || typeClassifier is null || taxonomy is null || same &&
                                                                                                    current is { DimensionJudgementFailed: false }
                                                                                                    && current.DimensionClassifierVersion ==
                                                                                                    typeClassifier.ClassifierVersion
                                                                                                    || same && current?.FailedDimensionAttempts >= 3)
        {
            return null;
        }

        var vocabulary = await taxonomy.GetAssignableTaxonomyAsync(observation.ClientId, ct);
        var result = await typeClassifier.ClassifyAsync(
            new(
                observation.ClientId, current?.Id ?? Guid.NewGuid(), discussion,
                observation.ConcernAnchor?.FilePath ?? observation.FilePath, observation.ConcernAnchor?.StartLine ?? observation.Line,
                CommentSeverity.Warning, null, vocabulary), ct);
        return result.Verdict;
    }

    private async Task<List<CodeInsightModelConcernEvidence>> MatchModelsAsync(
        MeisterProPRDbContext read, ThreadUpdatedEvent observation,
        Guid concernId, string sourceFingerprint, string discussion, CancellationToken ct)
    {
        var source = new ModelReviewSource(
            observation.ClientId, observation.RepositoryId, observation.PullRequestId,
            observation.ProviderScope ?? "", observation.ConnectionId);
        var models = this._executions.Executions.Where(job => ModelOpportunityEvaluator.SameSource(job.Source, source))
            .SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? []))
            .SelectMany(file => file.Passes).Where(pass => !string.IsNullOrWhiteSpace(pass.ModelId))
            .Select(pass => (pass.ModelId, pass.LogicalModelName)).Distinct().Take(129).ToArray();
        if (models.Length > 128)
        {
            return [];
        }

        var previous = await read.CodeInsightModelConcernEvidence.AsNoTracking().Where(row => row.HumanConcernId == concernId).ToListAsync(ct);
        var related = await (from other in read.CodeInsightHumanConcerns.AsNoTracking()
            join aggregate in read.CodeInsightPullRequests.AsNoTracking() on other.CodeInsightPullRequestId equals aggregate.Id
            where aggregate.ClientId == observation.ClientId && aggregate.RepositoryId == observation.RepositoryId &&
                  aggregate.PullRequestId == observation.PullRequestId
                  && other.ProviderScope == observation.ProviderScope && other.ConnectionId == observation.ConnectionId && other.Id != concernId
                  && !other.IsDeleted && !other.JudgementFailed && other.IsHumanConfirmed == true && other.IsSingleConcern == true
            orderby other.HarvestedAt, other.Id
            select other).Take(1001).ToListAsync(ct);
        var equivalents = related.Where(other => other.Anchor is { IsKnown: true } anchor && anchor.CommitSha == observation.ConcernAnchor?.CommitSha
                                                                                          && anchor.NativeRevisionId ==
                                                                                          observation.ConcernAnchor?.NativeRevisionId &&
                                                                                          anchor.FilePath == observation.ConcernAnchor?.FilePath)
            .Take(16).Select(other => new HumanConcernReference(other.Id.ToString(), this.ReadDiscussion(other.EncryptedDiscussion))).ToArray();
        var decisions = new List<CodeInsightModelConcernEvidence>();
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            var assessment = ModelOpportunityEvaluator.Assess(this._executions, source, observation.ConcernAnchor, model.ModelId, model.LogicalModelName);
            var old = previous.FirstOrDefault(row => row.ModelId == model.ModelId && row.LogicalModelName == model.LogicalModelName);
            if (old is not null && old.SourceFingerprint == sourceFingerprint && old.CandidateSetFingerprint == assessment.Fingerprint
                && old.ClassifierVersion == matcher.ClassifierVersion
                && (old.Decision != "unknown" || old.FailedAttempts >= 3 || !Retryable(old.UnavailableReason)))
            {
                decisions.Add(old);
                continue;
            }

            var result = assessment.NotApplicable
                ? new HumanConcernModelMatchResult(HumanConcernMatchState.Unknown, [], [], [], false)
                : assessment.Candidates.Count == 0 && equivalents.Length == 0
                    ? new HumanConcernModelMatchResult(
                        assessment.NegativeComplete ? HumanConcernMatchState.NoMatch : HumanConcernMatchState.Unknown, [], [], [], false)
                    : await matcher.MatchAsync(new(observation.ClientId, concernId, discussion, assessment.Candidates, equivalents), ct);
            var allowed = assessment.Candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            var equivalence = result.State == HumanConcernMatchState.Match && result.EquivalentConcernIds.Count > 0
                                                                           && result.EquivalentConcernIds.All(id => equivalents.Any(other => other.Id == id));
            var positive = result.State == HumanConcernMatchState.Match && result.MatchedFindingIds.Count > 0 && result.MatchedFindingIds.All(allowed.Contains);
            var negative = result.State == HumanConcernMatchState.NoMatch && assessment.NegativeComplete
                                                                          && result.EvaluatedCandidateIds.ToHashSet(StringComparer.Ordinal).SetEquals(allowed);
            negative |= equivalence && assessment.NegativeComplete && result.UnavailableReason is null
                        && result.EvaluatedCandidateIds.ToHashSet(StringComparer.Ordinal).SetEquals(allowed);
            var decision = assessment.NotApplicable ? "notApplicable" : positive ? "detected" : negative ? "missed" : "unknown";
            var failed = decision == "unknown" && result.ModelWasAsked;
            decisions.Add(
                new()
                {
                    Id = Guid.CreateVersion7(), HumanConcernId = concernId, ModelId = model.ModelId, LogicalModelName = model.LogicalModelName,
                    SourceFingerprint = sourceFingerprint, CandidateSetFingerprint = assessment.Fingerprint, ClassifierVersion = matcher.ClassifierVersion,
                    Decision = decision, CandidateIds = positive ? result.MatchedFindingIds : [],
                    EquivalentConcernIds = equivalence ? result.EquivalentConcernIds : [],
                    Confidence = result.Confidence, Rationale = result.Rationale is { Length: > 300 } rationale ? rationale[..300] : result.Rationale,
                    ObservedAt = observation.ObservedAt!.Value, UnavailableReason = decision == "unknown"
                        ? (result.UnavailableReason is "model-concern-input-limit-exceeded" or "model-concern-match-unavailable"
                            ? result.UnavailableReason
                            : assessment.UnavailableReason) ?? "model-concern-match-unavailable"
                        : null,
                    FailedAttempts =
                        failed
                            ? (old?.SourceFingerprint == sourceFingerprint && old.CandidateSetFingerprint == assessment.Fingerprint ? old.FailedAttempts : 0) +
                              1
                            : 0,
                });
        }

        return decisions;
    }

    private static async Task<bool> InvalidateEquivalenceAsync(MeisterProPRDbContext write, CodeInsightHumanConcern changed, CancellationToken ct)
    {
        var ids = await write.CodeInsightHumanConcerns.Where(row => row.CodeInsightPullRequestId == changed.CodeInsightPullRequestId
                                                                    && row.ProviderScope == changed.ProviderScope && row.ConnectionId == changed.ConnectionId)
            .Select(row => row.Id).Take(1001).ToArrayAsync(ct);
        var matches = await write.CodeInsightModelConcernEvidence.Where(row => ids.Contains(row.HumanConcernId)).Take(10001).ToListAsync(ct);
        if (ids.Length > 1000 || matches.Count > 10000)
        {
            return false;
        }

        var identity = changed.Id.ToString();
        foreach (var match in matches.Where(row => row.HumanConcernId != changed.Id && row.EquivalentConcernIds.Contains(identity, StringComparer.Ordinal)))
        {
            match.EquivalentConcernIds = match.EquivalentConcernIds.Where(id => id != identity).ToArray();
            match.CandidateSetFingerprint = string.Empty;
        }

        changed.CanonicalConcernId = changed.Id;
        changed.EquivalenceProof = null;
        return true;
    }

    private async Task<bool> MergeEquivalentConcernsAsync(
        MeisterProPRDbContext write, CodeInsightHumanConcern stored,
        IReadOnlyList<CodeInsightModelConcernEvidence> decisions, CancellationToken ct)
    {
        var members = await write.CodeInsightHumanConcerns.Where(row => row.CodeInsightPullRequestId == stored.CodeInsightPullRequestId
                                                                        && row.ProviderScope == stored.ProviderScope &&
                                                                        row.ConnectionId == stored.ConnectionId && !row.IsDeleted)
            .Take(1001).ToListAsync(ct);
        members.RemoveAll(row => row.IsDeleted);
        if (!stored.IsDeleted && members.All(row => row.Id != stored.Id))
        {
            members.Add(stored);
        }

        if (members.Count > 1000)
        {
            return false;
        }

        var byId = members.ToDictionary(row => row.Id);
        var ids = byId.Keys.ToArray();
        var retained = await write.CodeInsightModelConcernEvidence.Where(row => ids.Contains(row.HumanConcernId)).Take(10001).ToListAsync(ct);
        if (retained.Count > 10000)
        {
            return false;
        }

        var matches = retained.Concat(decisions).GroupBy(row => (row.HumanConcernId, row.ModelId, row.LogicalModelName))
            .Select(group => group.Last()).Where(row => byId.TryGetValue(row.HumanConcernId, out var owner)
                                                        && row.SourceFingerprint == owner.SourceFingerprint && row.CandidateSetFingerprint.Length > 0 &&
                                                        row.ClassifierVersion.Length > 0).ToArray();
        if (matches.Length > 10000)
        {
            return false;
        }

        var parent = ids.ToDictionary(id => id, id => id);

        Guid Root(Guid id)
        {
            while (parent[id] != id)
            {
                id = parent[id];
            }

            return id;
        }

        void Join(Guid left, Guid right)
        {
            if (!byId.TryGetValue(left, out var first) || !byId.TryGetValue(right, out var second)
                                                       || first.Anchor is not { IsKnown: true } a || second.Anchor is not { IsKnown: true } b
                                                       || a.CommitSha != b.CommitSha || a.NativeRevisionId != b.NativeRevisionId)
            {
                return;
            }

            parent[Root(right)] = Root(left);
        }

        foreach (var match in matches)
        {
            foreach (var equivalent in match.EquivalentConcernIds)
            {
                if (Guid.TryParse(equivalent, out var id))
                {
                    Join(match.HumanConcernId, id);
                }
            }
        }

        foreach (var candidate in matches.Where(row => row.Decision == "detected")
                     .SelectMany(row => row.CandidateIds.Select(id => (Candidate: id, Concern: row.HumanConcernId)))
                     .GroupBy(item => item.Candidate, StringComparer.Ordinal))
        {
            var first = candidate.First().Concern;
            foreach (var member in candidate.Skip(1))
            {
                Join(first, member.Concern);
            }
        }

        foreach (var group in members.GroupBy(row => Root(row.Id)))
        {
            var canonical = group.OrderBy(row => row.Anchor?.OriginAt).ThenBy(row => row.HarvestedAt).ThenBy(row => row.Id).First().Id;
            var proven = group.Count() > 1;
            foreach (var member in group)
            {
                member.CanonicalConcernId = canonical;
                member.EquivalenceProof = proven ? "explicit-candidate-or-concern-match" : null;
            }
        }

        return true;
    }
}
