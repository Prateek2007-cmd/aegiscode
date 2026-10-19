// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceMeasurement
{
    internal static ReviewerPerformancePoint WithoutPeriodObservation(ReviewerPerformancePoint measurement) => measurement with
    {
        Score = measurement.Score with
        {
            Scenarios = measurement.Score.Scenarios.Select(scenario => scenario with
            {
                Precision = null,
                Recall = null,
                F1 = null
            }).ToArray(),
            Summary = new(null, null, null)
        },
        OverallPrecision = measurement.OverallPrecision is { } precision
            ? precision with
            {
                Summary = null,
                Scenarios = precision.Scenarios.Select(scenario => scenario with { Precision = null }).ToArray()
            }
            : null,
        UnavailableReasons = measurement.UnavailableReasons.Concat(["no-series-observation-in-period"]).ToArray(),
    };

    internal static ReviewerPerformancePoint Measure(
        DateOnly date, IReadOnlyList<ReviewerPerformanceDailyCount> evidence, IReadOnlyList<ReviewerPerformanceDailyCount> population,
        ReviewerPerformanceViewQuery scope, string grouping, ReviewerPerformanceCoverageContext? coverageContext = null,
        ReviewerPerformanceContributions? contributions = null, DateOnly? from = null, DateOnly? to = null)
    {
        coverageContext ??= new(new(80, 1));
        var assessment = coverageContext.Assess(population, scope, grouping, to);
        var (counts, coveredCounts) = (contributions ?? new(evidence)).Sum(from ?? DateOnly.MinValue, to ?? DateOnly.MaxValue, assessment.CoveredSources);
        if (scope.Models is not null && coverageContext.HasModelEvidence)
        {
            coveredCounts = coveredCounts with { ActedMisses = 0, UnactedMisses = 0, ProvisionalMisses = 0, FailedMissJudgements = 0, HarvestedThreads = 0 };
            coveredCounts += coverageContext.ModelMisses(scope, assessment.CoveredSources, from ?? DateOnly.MinValue, to ?? DateOnly.MaxValue);
        }

        var reasons = assessment.Reasons.ToList();
        if (population.Any(row => row.Outcome is "collectionProvisional" or "missProvisional"))
        {
            reasons.Add("miss-observation-provisional");
        }

        if (counts.PublicationUnknown > 0)
        {
            reasons.Add("publication-identity-unavailable");
        }

        if (counts.DuplicateUnknown > 0 || counts.DuplicateSuspected > 0)
        {
            reasons.Add("duplicate-verification-incomplete");
        }

        if (counts.Unclassified > 0)
        {
            reasons.Add("finding-classification-incomplete");
        }

        return new(
            date, ReviewerPerformanceScoreCalculator.Compute(coveredCounts, assessment.Coverage.CoveredPullRequests > 0 && assessment.Coverage.RecallSupported),
            reasons,
            OverallPrecision: ReviewerPerformanceScoreCalculator.ComputePrecision(counts), Coverage: assessment.Coverage);
    }

    internal static ReviewerPerformanceCounts Vector(ReviewerPerformanceDailyCount row)
    {
        var n = row.Count;
        if (row.IsMiss)
        {
            return new(
                default, default, ActedMisses: row.Outcome == "missActed" ? n : 0,
                UnactedMisses: row.Outcome == "missUnacted" ? n : 0, ProvisionalMisses: row.Outcome == "missProvisional" ? n : 0,
                HarvestedThreads: n, FailedMissJudgements: row.Outcome == "missFailed" ? n : 0);
        }

        var published = row.PublicationState == CodeInsightPublicationState.Published;
        var outcomes = !published
            ? default
            : row.Outcome switch
            {
                "positive" => new ReviewerPerformanceOutcomeCounts(Positive: n),
                "wrong" => new(Wrong: n),
                "dismissed" => new(Dismissed: n),
                "wontFix" => new(WontFix: n),
                "byDesign" => new(ByDesign: n),
                "unresolved" => new(Unresolved: n),
                _ => new(Unknown: n),
            };
        return new(
            outcomes, row.DuplicateState == CodeInsightDuplicateState.ConfirmedDuplicate ? outcomes : default,
            Generated: n, SuppressedRepeats: row.PublicationState == CodeInsightPublicationState.SuppressedRepeat ? n : 0,
            Withheld: row.PublicationState is CodeInsightPublicationState.PolicyWithheld or CodeInsightPublicationState.Shadow
                or CodeInsightPublicationState.PostingDisabled or CodeInsightPublicationState.Failed
                ? n
                : 0,
            PublicationUnknown: row.PublicationState == CodeInsightPublicationState.Unknown ? n : 0,
            DuplicateChecked: published && row.DuplicateState is CodeInsightDuplicateState.CheckedNonduplicate or CodeInsightDuplicateState.ConfirmedDuplicate
                ? n
                : 0,
            DuplicateSuspected: published && row.DuplicateState == CodeInsightDuplicateState.Suspected ? n : 0,
            DuplicateUnknown: published && row.DuplicateState == CodeInsightDuplicateState.Unknown ? n : 0,
            Unclassified: !row.IsClassified ? n : 0, Unattributed: row.ModelId.Length == 0 ? n : 0);
    }

    internal static bool IsCoverage(ReviewerPerformanceDailyCount row) => row.Outcome.StartsWith("collection", StringComparison.Ordinal);
}
