// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.Domain.ValueObjects;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal sealed record ModelSourceQualification(bool Member, IReadOnlyList<string> Reasons);

internal sealed record ModelEvidenceEvaluationBudget(int Evaluations = 8192, long CandidateReferences = 1_000_000, long PassReferences = 1_000_000);

internal sealed class ReviewerPerformanceModelEvidence(
    ModelReviewEvidenceSnapshot executions,
    IReadOnlyDictionary<Guid, CodeInsightPullRequest> aggregates,
    IReadOnlyList<CodeInsightHumanConcern> concerns,
    IReadOnlyList<CodeInsightModelConcernEvidence> decisions,
    IReadOnlyList<CodeInsightHarvestCoverage> coverage,
    IReadOnlyList<ReviewerPerformanceDailyCount>? publicationCells = null,
    ModelEvidenceEvaluationBudget? evaluationBudget = null)
{
    private readonly bool _evaluationBounded = ValidateBudget(executions, aggregates, concerns, publicationCells ?? [], evaluationBudget ?? new());
    private readonly Dictionary<string, Selection> _selections = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Concern, string Model, string Logical), ModelOpportunityAssessment> _opportunities = [];
    private readonly Dictionary<ReviewerPerformanceSourceKey, string[]> _modelMemberships = [];

    private readonly ILookup<(Guid, string, long), ModelReviewExecution> _jobs = executions.Executions.ToLookup(job =>
        (job.Source.ClientId, job.Source.RepositoryId, job.Source.PullRequestId));

    private readonly ILookup<Guid, CodeInsightHumanConcern> _concerns = concerns.ToLookup(row => row.CodeInsightPullRequestId);

    private readonly Dictionary<(Guid, string, string), CodeInsightModelConcernEvidence> _decisions = decisions
        .GroupBy(row => (row.HumanConcernId, row.ModelId, row.LogicalModelName)).ToDictionary(
            group => group.Key, group => group.OrderByDescending(row => row.ObservedAt).First());

    private readonly ILookup<ReviewerPerformanceSourceKey, ReviewerPerformanceDailyCount> _publications = (publicationCells ?? [])
        .Where(row => !row.IsMiss && !ReviewerPerformanceMeasurement.IsCoverage(row) && row.ModelId.Length != 0).ToLookup(ReviewerPerformanceSourceKey.Of);

    internal ModelSourceQualification Assess(ReviewerPerformanceSourceKey source, ReviewerPerformanceViewQuery scope, DateOnly to)
    {
        var selected = this.Select(scope);
        if (!selected.Sources.TryGetValue(source, out var qualification))
        {
            var models = this.ModelMemberships(source);
            return models.Length > 0 && !models.Any((scope.Models ?? []).Contains) ? new(false, []) : new(true, ["model-proof-unavailable"]);
        }

        var reasons = qualification.Reasons.Where(item => item.Date <= to).Select(item => item.Reason).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return new(true, reasons);
    }

    internal ReviewerPerformanceCounts Misses(
        ReviewerPerformanceViewQuery scope, HashSet<ReviewerPerformanceSourceKey> covered,
        DateOnly from, DateOnly to) => this.Select(scope).Misses.Sum(from, to, covered).Covered;

    private string[] ModelMemberships(ReviewerPerformanceSourceKey source)
    {
        if (this._modelMemberships.TryGetValue(source, out var existing))
        {
            return existing;
        }

        var aggregate = aggregates.GetValueOrDefault(source.AggregateId);
        var jobs = aggregate is null
            ? []
            : this._jobs[(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId)]
                .Where(job => job.Source.ProviderScope == source.ProviderScope).ToArray();
        var models = this._publications[source].Select(row => (row.ModelId, row.LogicalModelName))
            .Concat(
                jobs.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? []).SelectMany(file => file.Passes)
                    .Select(pass => (pass.ModelId, pass.LogicalModelName))))
            .Concat(jobs.SelectMany(job => job.Manifest?.AllowedModels ?? []).Select(binding => (binding.ModelId, binding.LogicalModelName)))
            .Distinct().Select(model => ModelKey(model.ModelId, model.LogicalModelName)).ToArray();
        this._modelMemberships[source] = models;
        return models;
    }

    internal string RecoveryAction(ReviewerPerformanceSourceKey source, ReviewerPerformanceViewQuery scope, IReadOnlyList<string> reasons)
    {
        if (!aggregates.TryGetValue(source.AggregateId, out var aggregate) || !executions.Complete)
        {
            return "none";
        }

        var jobs = this._jobs[(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId)]
            .Where(job => job.Source.ProviderScope == source.ProviderScope).ToArray();
        var selected = (scope.Models ?? []).ToHashSet(StringComparer.Ordinal);
        var files = jobs.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? [])).ToArray();
        var members = this.ModelMemberships(source).Where(selected.Contains).ToArray();
        var completeModels = files.Where(file => file.InputComplete && file.InventoryComplete && file.AssignedRanges.Count > 0)
            .SelectMany(file => file.Passes).Where(pass =>
                pass.AcceptedAt is not null && (pass.IsCertified || pass.Disposition == ModelGenerationDisposition.NotApplicable))
            .Select(pass => ModelKey(pass.ModelId, pass.LogicalModelName)).ToHashSet(StringComparer.Ordinal);
        if (members.Length == 0 || members.Any(model => !completeModels.Contains(model)))
        {
            return "none";
        }

        if (reasons.All(reason => reason == "model-concern-collection-unavailable"))
        {
            return "recollect-source";
        }

        if (reasons.All(reason => reason is "model-concern-judgement-failed" or "model-concern-match-unavailable"
                or "miss-type-attribution-unavailable" or "miss-qualifier-attribution-unavailable"))
        {
            var connection = this.RecoverySource(source).ConnectionId;
            var retained = this._concerns[source.AggregateId]
                .Where(row => !row.IsDeleted && row.ProviderScope == source.ProviderScope && row.ConnectionId == connection).ToArray();
            return retained.Length is > 0 and <= 100 && retained.All(row => row.RetainedSourceComplete) ? "retry-model-evidence" : "none";
        }

        return "none";
    }

    internal (Guid? ConnectionId, DateTimeOffset? LastRetryAt) RecoverySource(ReviewerPerformanceSourceKey source)
    {
        if (!aggregates.TryGetValue(source.AggregateId, out var aggregate))
        {
            return default;
        }

        var universe = coverage.FirstOrDefault(row => row.CodeInsightPullRequestId == aggregate.Id && row.ProviderScope == source.ProviderScope);
        var connections = universe?.ModelConcernConnectionId is { } connection && connection != Guid.Empty
            ? new[] { connection }
            : this._jobs[(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId)].Where(job => job.Source.ProviderScope == source.ProviderScope)
                .Select(job => job.Source.ConnectionId).OfType<Guid>().Where(id => id != Guid.Empty).Distinct().ToArray();
        if (connections.Length != 1)
        {
            return default;
        }

        return (connections[0], this._concerns[aggregate.Id]
            .Where(row => !row.IsDeleted && row.ProviderScope == source.ProviderScope && row.ConnectionId == connections[0])
            .Select(row => row.LastModelEvidenceRetryAt).DefaultIfEmpty().Max());
    }

    internal IEnumerable<ReviewerPerformanceDailyCount> Markers(DateOnly from, DateOnly to)
    {
        var seen = new HashSet<(Guid Aggregate, string Scope, string Model, string Logical, DateOnly Date, string Type, string Qualifier)>();
        foreach (var aggregate in aggregates.Values)
        {
            foreach (var jobs in this._jobs[(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId)]
                         .GroupBy(job => job.Source.ProviderScope, StringComparer.Ordinal))
            {
                var models = new HashSet<(string Model, string Logical)>();
                foreach (var job in jobs)
                {
                    foreach (var file in job.Files.Concat(job.Manifest?.PullRequestPasses ?? []))
                    {
                        foreach (var pass in file.Passes.Where(pass => !string.IsNullOrWhiteSpace(pass.ModelId)))
                        {
                            models.Add((pass.ModelId, pass.LogicalModelName));
                            var marker = Marker(pass.ModelId, pass.LogicalModelName, pass.AcceptedAt ?? file.AcceptedAt ?? file.CapturedAt, "", "");
                            if (marker is not null)
                            {
                                yield return marker;
                            }
                        }
                    }

                    foreach (var binding in job.Manifest?.AllowedModels ?? [])
                    {
                        models.Add((binding.ModelId, binding.LogicalModelName));
                        var marker = Marker(binding.ModelId, binding.LogicalModelName, job.Manifest!.AcceptedAt ?? job.Manifest.CapturedAt, "", "");
                        if (marker is not null)
                        {
                            yield return marker;
                        }
                    }
                }

                foreach (var concern in this._concerns[aggregate.Id].Where(concern => !concern.IsDeleted && concern.ProviderScope == jobs.Key))
                {
                    foreach (var model in models)
                    {
                        var marker = Marker(model.Model, model.Logical, concern.HarvestedAt, concern.TypeMembership, concern.Qualifier?.ToString() ?? "");
                        if (marker is not null)
                        {
                            yield return marker;
                        }
                    }
                }

                ReviewerPerformanceDailyCount? Marker(string model, string logical, DateTimeOffset stamp, string type, string qualifier)
                {
                    var date = DateOnly.FromDateTime(stamp.UtcDateTime);
                    if (date < from || date > to || !seen.Add((aggregate.Id, jobs.Key, model, logical, date, type, qualifier)))
                    {
                        return null;
                    }

                    if (seen.Count > ReviewerPerformanceRangeReader.MaximumCells)
                    {
                        throw new ArgumentException("The scope exceeds the retained-cell limit. Narrow the dates or clients.");
                    }

                    return new()
                    {
                        CodeInsightPullRequestId = aggregate.Id, ClientId = aggregate.ClientId, RepositoryId = aggregate.RepositoryId,
                        PullRequestId = aggregate.PullRequestId, ProviderScope = jobs.Key, ModelId = model, LogicalModelName = logical,
                        BucketDate = date, TypeMembership = type, Qualifier = qualifier, Outcome = "collectionModelOpportunity", Count = 0, UpdatedAt = stamp
                    };
                }
            }
        }
    }

    private Selection Select(ReviewerPerformanceViewQuery scope)
    {
        _ = this._evaluationBounded;
        var key = JsonSerializer.Serialize(
            new { scope.Models, Types = scope.Types?.Order(StringComparer.Ordinal), Qualifiers = scope.Qualifiers?.Order(StringComparer.Ordinal) });
        if (this._selections.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var selectedKeys = (scope.Models ?? []).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<ReviewerPerformanceSourceKey, SourceQualification>();
        var misses = new List<ReviewerPerformanceDailyCount>();
        foreach (var aggregate in aggregates.Values)
        {
            var jobs = this._jobs[(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId)]
                .GroupBy(job => job.Source.ProviderScope, StringComparer.Ordinal);
            foreach (var group in jobs)
            {
                var sourceKey = new ReviewerPerformanceSourceKey(aggregate.Id, group.Key);
                var models = group.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? []).SelectMany(file => file.Passes)
                        .Select(pass => (pass.ModelId, pass.LogicalModelName))
                        .Concat((job.Manifest?.AllowedModels ?? []).Select(model => (model.ModelId, model.LogicalModelName))))
                    .Concat(this._publications[sourceKey].Select(row => (row.ModelId, row.LogicalModelName)))
                    .Where(model => !string.IsNullOrWhiteSpace(model.ModelId) && selectedKeys.Contains(ModelKey(model.ModelId, model.LogicalModelName)))
                    .Distinct().ToArray();
                if (models.Length == 0)
                {
                    continue;
                }

                var reasons = new List<DatedReason>();
                sources[sourceKey] = new(reasons);
                var universe = coverage.FirstOrDefault(row => row.CodeInsightPullRequestId == aggregate.Id && row.ProviderScope == group.Key);
                if (universe is not { ModelConcernEnumerationComplete: true, ModelConcernObservedAt: { }, ModelConcernConnectionId: { } connection }
                    || connection == Guid.Empty)
                {
                    reasons.Add(new(DateOnly.MinValue, "model-concern-collection-unavailable"));
                    continue;
                }

                var source = new ModelReviewSource(aggregate.ClientId, aggregate.RepositoryId, aggregate.PullRequestId, group.Key, connection);
                var compatible = group.Where(job => ModelOpportunityEvaluator.SameSource(job.Source, source)).ToArray();
                var bindings = ModelOpportunityEvaluator.Bindings(compatible);
                var sourceConcerns = this._concerns[aggregate.Id].Where(row => !row.IsDeleted
                                                                               && (row.ProviderScope == group.Key ||
                                                                                   string.IsNullOrWhiteSpace(row.ProviderScope))).ToArray();
                var firstOrigin = sourceConcerns.Where(row => row.ThreadResolved && row.IsSubstantive && row.IsInScope && row.Anchor?.OriginAt is not null)
                    .Select(row => row.Anchor!.OriginAt!.Value).DefaultIfEmpty(universe.ModelConcernObservedAt!.Value).Min();
                foreach (var model in models)
                {
                    var passes = compatible.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? [])
                        .SelectMany(file => file.Passes
                            .Where(pass => ModelOpportunityEvaluator.CouldBeModel(pass, model.ModelId, model.LogicalModelName, bindings))
                            .Select(pass => (Job: job, File: file, Pass: pass)))).ToArray();
                    if (passes.Any(item => item.Pass.AcceptedAt <= firstOrigin && item.Pass.CompletedAt <= firstOrigin
                                                                               && (item.Pass.IsCertified || item.Pass.Disposition ==
                                                                                   ModelGenerationDisposition.NotApplicable)))
                    {
                        passes = passes.Where(item => !ModelOpportunityEvaluator.IsProvenLater(item.Pass, firstOrigin)).ToArray();
                    }

                    if (!executions.Complete || passes.Length == 0)
                    {
                        reasons.Add(new(DateOnly.MinValue, executions.UnavailableReason ?? "model-proof-unavailable"));
                    }

                    foreach (var (job, file, pass) in passes)
                    {
                        var date = DateOnly.FromDateTime((pass.AcceptedAt ?? pass.CompletedAt ?? file.CapturedAt).UtcDateTime);
                        var reason = job.Manifest is not { InventoryComplete: true } || !file.InputComplete || !file.InventoryComplete ||
                                     file.AssignedRanges.Count == 0
                            ? file.UnavailableReason ?? "model-input-scope-unavailable"
                            : pass.AcceptedAt is null || pass.CompletedAt is null || pass.CompletedAt > pass.AcceptedAt
                              || !pass.IsCertified && pass.Disposition != ModelGenerationDisposition.NotApplicable
                                ? "model-generation-incomplete"
                                : pass.AcceptedAt > universe.ModelConcernObservedAt
                                    ? "model-concern-collection-unavailable"
                                    : null;
                        if (reason is not null)
                        {
                            reasons.Add(new(date, reason));
                        }
                    }
                }

                foreach (var concern in sourceConcerns)
                {
                    var date = DateOnly.FromDateTime(concern.HarvestedAt.UtcDateTime);
                    string? reason = concern.ConnectionId != connection || string.IsNullOrWhiteSpace(concern.ProviderScope)
                        ? "model-concern-source-unavailable"
                        : concern.JudgementFailed || concern.IsSingleConcern != true || concern.IsHumanConfirmed is null
                            ? concern.UnavailableReason ?? "model-concern-judgement-failed"
                            : null;
                    if (reason is not null)
                    {
                        reasons.Add(new(date, reason));
                        continue;
                    }

                    if (!concern.ThreadResolved || !concern.IsSubstantive || !concern.IsInScope || concern.IsHumanConfirmed != true)
                    {
                        continue;
                    }

                    if (concern.Anchor is not { IsKnown: true } anchor || anchor.OriginAt > concern.ObservedAt)
                    {
                        reasons.Add(new(date, "model-concern-anchor-unavailable"));
                    }

                    if (scope.Types is not null && (concern.TypeMembership.Length == 0 || concern.DimensionJudgementFailed ||
                                                    concern.DimensionClassifierVersion is null))
                    {
                        reasons.Add(new(date, "miss-type-attribution-unavailable"));
                    }

                    if (scope.Qualifiers is not null &&
                        (concern.Qualifier is null || concern.DimensionJudgementFailed || concern.DimensionClassifierVersion is null))
                    {
                        reasons.Add(new(date, "miss-qualifier-attribution-unavailable"));
                    }
                }

                foreach (var canonical in sourceConcerns.Where(row => !row.JudgementFailed && row.ThreadResolved && row.IsHumanConfirmed == true
                                                                      && row.IsSingleConcern == true && row.IsSubstantive && row.IsInScope)
                             .GroupBy(row => row.CanonicalConcernId))
                {
                    // The first native issue origin prevents a later alias or review from retrospectively removing a miss.
                    var concern = canonical.OrderBy(row => row.Anchor?.OriginAt).ThenBy(row => row.HarvestedAt).ThenBy(row => row.Id).First();
                    var date = DateOnly.FromDateTime(canonical.Min(row => row.HarvestedAt).UtcDateTime);
                    var statuses = new List<string>();
                    foreach (var model in models)
                    {
                        var assessmentKey = (concern.Id, model.ModelId, model.LogicalModelName);
                        if (!this._opportunities.TryGetValue(assessmentKey, out var opportunity))
                        {
                            this._opportunities[assessmentKey] = opportunity = ModelOpportunityEvaluator.Assess(
                                executions, source, concern.Anchor, model.ModelId, model.LogicalModelName);
                        }

                        if (opportunity.NotApplicable)
                        {
                            statuses.Add("notApplicable");
                            continue;
                        }

                        var decision = this._decisions.GetValueOrDefault((concern.Id, model.ModelId, model.LogicalModelName));
                        if (decision?.SourceFingerprint != concern.SourceFingerprint || decision.CandidateSetFingerprint != opportunity.Fingerprint ||
                            string.IsNullOrWhiteSpace(decision.ClassifierVersion))
                        {
                            decision = null;
                        }

                        var valid = decision?.Decision == "detected" && decision.CandidateIds.Count > 0
                                                                     && decision.CandidateIds.All(id =>
                                                                         opportunity.Candidates.Any(candidate => candidate.Id == id));
                        var status = valid ? "detected" : decision?.Decision == "missed" && opportunity.NegativeComplete ? "missed" : "unknown";
                        statuses.Add(status);
                        if (status == "unknown")
                        {
                            reasons.Add(new(date, decision?.UnavailableReason ?? opportunity.UnavailableReason ?? "model-concern-match-unavailable"));
                        }
                    }

                    if (statuses.Contains("detected") || statuses.Contains("unknown") || !statuses.Contains("missed") || !MatchesDimensions(concern, scope))
                    {
                        continue;
                    }

                    misses.Add(
                        new()
                        {
                            CodeInsightPullRequestId = aggregate.Id, ClientId = aggregate.ClientId, RepositoryId = aggregate.RepositoryId,
                            PullRequestId = aggregate.PullRequestId, ProviderScope = group.Key, BucketDate = date, IsMiss = true,
                            TypeMembership = concern.TypeMembership, Qualifier = concern.Qualifier?.ToString() ?? "",
                            IsClassified = !concern.DimensionJudgementFailed,
                            Outcome = canonical.Any(row => row.WasActedOn) ? "missActed" : "missUnacted", Count = 1
                        });
                }
            }
        }

        cached = new(sources, new(misses));
        this._selections[key] = cached;
        return cached;
    }

    internal static string ModelKey(string model, string logical) =>
        ReviewerPerformanceIdentity.ModelKey(new() { ModelId = model, LogicalModelName = logical });

    private static bool MatchesDimensions(CodeInsightHumanConcern concern, ReviewerPerformanceViewQuery scope) =>
        (scope.Types is null || concern.TypeMembership.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(scope.Types.Contains))
        && (scope.Qualifiers is null || scope.Qualifiers.Contains(concern.Qualifier?.ToString() ?? ""));

    private sealed record DatedReason(DateOnly Date, string Reason);

    private sealed record SourceQualification(List<DatedReason> Reasons);

    private sealed record Selection(Dictionary<ReviewerPerformanceSourceKey, SourceQualification> Sources, ReviewerPerformanceContributions Misses);

    private static bool ValidateBudget(
        ModelReviewEvidenceSnapshot snapshot, IReadOnlyDictionary<Guid, CodeInsightPullRequest> aggregates,
        IReadOnlyList<CodeInsightHumanConcern> concerns, IReadOnlyList<ReviewerPerformanceDailyCount> publications, ModelEvidenceEvaluationBudget budget)
    {
        var identities = aggregates.Values.ToDictionary(row => (row.ClientId, row.RepositoryId, row.PullRequestId), row => row.Id);
        var byAggregate = concerns.Where(row => !row.IsDeleted && row.ThreadResolved && row.IsSubstantive && row.IsInScope
                                                && row.IsHumanConfirmed == true && row.IsSingleConcern == true).ToLookup(row => row.CodeInsightPullRequestId);
        long evaluations = 0, candidateReferences = 0, passReferences = 0;
        var publishedModels = publications.Where(row => !row.IsMiss && !ReviewerPerformanceMeasurement.IsCoverage(row) && row.ModelId.Length != 0)
            .ToLookup(row => (row.CodeInsightPullRequestId, row.ProviderScope));
        foreach (var source in snapshot.Executions.GroupBy(job =>
                     (job.Source.ClientId, job.Source.RepositoryId, job.Source.PullRequestId, job.Source.ProviderScope)))
        {
            if (!identities.TryGetValue((source.Key.ClientId, source.Key.RepositoryId, source.Key.PullRequestId), out var aggregateId))
            {
                continue;
            }

            var count = byAggregate[aggregateId]
                .LongCount(row => row.ProviderScope == source.Key.ProviderScope || string.IsNullOrWhiteSpace(row.ProviderScope));
            var passes = source.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? [])).SelectMany(file => file.Passes).ToArray();
            var models = passes.Select(pass => (pass.ModelId, pass.LogicalModelName))
                .Concat(source.SelectMany(job => job.Manifest?.AllowedModels ?? []).Select(model => (model.ModelId, model.LogicalModelName)))
                .Concat(publishedModels[(aggregateId, source.Key.ProviderScope)].Select(row => (row.ModelId, row.LogicalModelName)))
                .Where(model => !string.IsNullOrWhiteSpace(model.ModelId)).Distinct().LongCount();
            evaluations += count * models;
            candidateReferences += count * passes.Sum(pass => (long)pass.Candidates.Count);
            passReferences += count * passes.LongLength;
            if (evaluations > budget.Evaluations || candidateReferences > budget.CandidateReferences || passReferences > budget.PassReferences)
            {
                throw new ArgumentException("The model evidence exceeds the evaluation limit. Narrow the dates, repositories or clients.");
            }
        }

        return true;
    }
}
