// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceCoveredPopulationTests
{
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 9, 18);

    [Fact]
    public async Task FourCoveredPullRequestsRemainMeasurableWhenOneCollectionIsUnknown()
    {
        await using var db = Db();
        for (var number = 1; number <= 5; number++)
        {
            AddPullRequest(db, number, number == 5 ? "collectionUnknown" : "collectionComplete", positive: number == 5 ? 0 : 2, wrong: number == 5 ? 20 : 0);
        }

        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client]);
        var point = Assert.Single(Assert.Single(result.Views).Series).Points[0];

        Assert.NotNull(point.Score.Summary.F1);
        Assert.Equal(1, point.Score.Summary.F1.Median);
        Assert.Equal(8, point.Score.Counts.Outcomes.Positive);
        Assert.Equal(0, point.Score.Counts.Outcomes.Wrong);
        Assert.Equal(20, point.OverallPrecision!.Counts.Outcomes.Wrong);
        Assert.Equal(8d / 28, point.OverallPrecision.Summary!.Median);
        Assert.Equal(5, point.Coverage!.CandidatePullRequests);
        Assert.Equal(4, point.Coverage.CoveredPullRequests);
        Assert.Equal(80, point.Coverage.Percentage);
        Assert.True(point.Coverage.MeetsMinimum);
        Assert.Single(Assert.Single(result.Views).ExclusionCatalog!);
    }

    [Fact]
    public async Task CoveredScenarioUsesOnePopulationForEveryConfusionCount()
    {
        await using var db = Db();
        for (var number = 1; number <= 4; number++)
        {
            var pr = AddPullRequest(db, number, "collectionComplete", positive: 4, wrong: 2);
            db.Add(
                new ReviewerPerformanceDailyCount
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = Client, RepositoryId = "repo", PullRequestId = number,
                    ProviderScope = "AzureDevOps:https://dev.azure.com/test", BucketDate = Date, Outcome = "missActed", IsMiss = true,
                    Count = 2, TypeMembership = "concurrency", Qualifier = "Missing", IsClassified = true,
                });
        }

        AddPullRequest(db, 5, "collectionUnknown", positive: 0, wrong: 80);
        await db.SaveChangesAsync();
        var point = (await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client])).Views[0].Series[0].Points[0];

        Assert.All(
            point.Score.Scenarios, scenario =>
            {
                Assert.Equal(16, scenario.TruePositives);
                Assert.Equal(8, scenario.FalsePositives);
                Assert.Equal(8, scenario.FalseNegatives);
                Assert.Equal(2d / 3, scenario.Precision);
                Assert.Equal(2d / 3, scenario.Recall);
                Assert.Equal(2d / 3, scenario.F1);
            });
        Assert.Equal(16d / 104, point.OverallPrecision!.Summary!.Median);
        Assert.NotEqual(point.OverallPrecision.Summary.Median, point.Score.Summary.Precision!.Median);
    }

    [Theory]
    [InlineData(4, 1, 80, 1, true)]
    [InlineData(4, 1, 81, 1, false)]
    [InlineData(2, 1, 67, 1, false)]
    [InlineData(2, 1, 66, 1, true)]
    [InlineData(0, 1, 0, 1, false)]
    [InlineData(1, 0, 100, 2, false)]
    [InlineData(1, 0, -5, 0, true)]
    [InlineData(1, 0, 150, 1, true)]
    public async Task CoverageMinimumsUseDistinctUnroundedPopulations(int covered, int excluded, int percentage, int minimumPrs, bool meetsMinimum)
    {
        await using var db = Db();
        for (var i = 0; i < covered + excluded; i++)
        {
            AddPullRequest(db, i + 1, i < covered ? "collectionComplete" : "collectionUnknown");
        }

        await db.SaveChangesAsync();
        var settings = new CodeInsightsOptions { MinimumCoveragePercentage = percentage, MinimumCoveredPullRequests = minimumPrs };
        var result = await Reader(db, settings).QueryAsync(Query(), [Client]);
        var point = result.Views[0].Series[0].Points[0];
        Assert.Equal(covered > 0, point.Score.Summary.F1 is not null);
        Assert.Equal(covered > 0, point.Score.Summary.Recall is not null);
        Assert.Equal(meetsMinimum, point.Coverage!.MeetsMinimum);
        Assert.Equal("reviewer-performance-v2", result.CalculationVersion);
        Assert.Equal(covered, point.Coverage.CoveredPullRequests);
        Assert.NotNull(point.OverallPrecision!.Summary);
        Assert.Equal(Math.Clamp(percentage, 0, 100), result.CoveragePolicy!.MinimumCoveragePercentage);
        Assert.Equal(Math.Max(minimumPrs, 1), result.CoveragePolicy.MinimumCoveredPullRequests);
    }

    [Fact]
    public async Task DistinctSourcesOnOneAggregateAreSeparateWhileDateAndTagMembershipsAreDeduplicated()
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        var repeated = Cell(pr, "positive", 3);
        repeated.BucketDate = Date.AddDays(1);
        repeated.TypeMembership = "concurrency|logic-error";
        var otherCoverage = Cell(pr, "collectionComplete");
        otherCoverage.ProviderScope = "GitHub:https://github.example";
        var otherFinding = Cell(pr, "positive", 5);
        otherFinding.ProviderScope = otherCoverage.ProviderScope;
        db.AddRange(repeated, otherCoverage, otherFinding);
        await db.SaveChangesAsync();

        var point = (await new ReviewerPerformanceRangeReader(db).QueryAsync(
            Query() with
            {
                Views = [Query().Views[0] with { Types = ["concurrency", "logic-error"] }]
            }, [Client])).Views[0].Series[0].Points[^1];

        Assert.Equal(2, point.Coverage!.CandidatePullRequests);
        Assert.Equal(2, point.Coverage.CoveredPullRequests);
        Assert.Equal(10, point.Score.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task UnknownNamespaceExcludesItsKnownSourceWithoutAddingAPhantomPullRequest()
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        var unknown = Cell(pr, "missActed", 2);
        unknown.IsMiss = true;
        unknown.ProviderScope = "";
        db.Add(unknown);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client]);
        var point = result.Views[0].Series[0].Points[0];

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Equal(0, point.Score.Counts.ActedMisses);
        Assert.Equal(2, point.OverallPrecision!.Counts.ActedMisses);
        var exclusion = Assert.Single(result.Views[0].ExclusionCatalog!);
        Assert.Equal(pr.Id, exclusion.AggregateId);
        Assert.Equal("AzureDevOps:https://dev.azure.com/test", exclusion.ProviderScope);
        Assert.Contains("miss-provider-scope-unavailable", exclusion.Reasons);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("qualifier")]
    public async Task IncompatibleMissAttributionIsAssessedBeforeDimensionFiltering(string dimension)
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        var unknown = Cell(pr, "missActed", 2);
        unknown.IsMiss = true;
        unknown.IsClassified = false;
        unknown.TypeMembership = "";
        unknown.Qualifier = "";
        db.Add(unknown);
        await db.SaveChangesAsync();
        var scope = Query().Views[0];
        scope = dimension == "type" ? scope with { Types = ["concurrency"] } : scope with { Qualifiers = ["Missing"] };
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query() with { Views = [scope] }, [Client]);
        var point = result.Views[0].Series[0].Points[0];
        Assert.Equal(1, point.Coverage!.ExcludedPullRequests);
        Assert.Null(point.Score.Summary.F1);
        Assert.NotNull(point.OverallPrecision!.Summary);
        Assert.Contains($"miss-{dimension}-attribution-unavailable", Assert.Single(result.Views[0].ExclusionCatalog!).Reasons);
    }

    [Theory]
    [InlineData("missProvisional", true)]
    [InlineData("missFailed", false)]
    public async Task SuccessfullyJudgedProvisionalThreadsAndFailedJudgmentsHaveDifferentEligibility(string outcome, bool available)
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionProvisional");
        var miss = Cell(pr, outcome);
        miss.IsMiss = true;
        db.Add(miss);
        await db.SaveChangesAsync();
        var point = (await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client])).Views[0].Series[0].Points[0];
        Assert.Equal(available, point.Score.Summary.F1 is not null);
        Assert.Contains("miss-observation-provisional", point.UnavailableReasons);
        Assert.Equal(available ? 1 : 0, point.Coverage!.CoveredPullRequests);
    }

    [Fact]
    public async Task FailedRefreshDoesNotInvalidateCompleteRetainedEvidenceAndCatalogCapturesBoundedMetadata()
    {
        await using var db = Db();
        db.Clients.Add(new ClientRecord { Id = Client, DisplayName = "Client", CodeInsightsCollectionEnabled = true });
        var healthy = AddPullRequest(db, 1, "collectionComplete");
        healthy.LastHistoricalRefreshReason = "ProviderReadFailed";
        var excluded = AddPullRequest(db, 2, "collectionUnknown", positive: 0);
        excluded.LastHistoricalRefreshReason = "SourceCoordinatesUnavailable";
        excluded.LastHistoricalRefreshAttemptAt = DateTimeOffset.UtcNow.AddHours(-1);
        excluded.HistoricalRefreshNotBefore = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var result = await Reader(db, new() { MinimumCoveragePercentage = 0 }).QueryAsync(Query(), [Client]);
        var view = result.Views[0];
        Assert.Equal(1, view.Series[0].Points[0].Coverage!.CoveredPullRequests);
        Assert.NotNull(view.Series[0].Points[0].Score.Summary.F1);
        var entry = Assert.Single(view.ExclusionCatalog!);
        Assert.Equal("Client", entry.ClientName);
        Assert.Equal("Repository", entry.RepositoryName);
        Assert.Equal(excluded.LastHistoricalRefreshAttemptAt, entry.LastAttemptAt);
        Assert.Equal(excluded.HistoricalRefreshNotBefore, entry.NotBefore);
        Assert.Equal("SourceCoordinatesUnavailable", entry.LastRefreshReason);
        Assert.Equal("repair-required", entry.RecoveryDisposition);
        Assert.Null(entry.SourceUrl);
        Assert.All(view.Series[0].Points, point => Assert.Equal(entry.Id, Assert.Single(point.Coverage!.ExclusionIds)));
    }

    [Fact]
    public async Task TimelineMatrixAndComparedViewsShareCoveredInputsAndPolicy()
    {
        await using var db = Db();
        for (var i = 1; i <= 5; i++)
        {
            AddPullRequest(db, i, i == 5 ? "collectionUnknown" : "collectionComplete");
        }

        await db.SaveChangesAsync();
        var monitor = Substitute.For<IOptionsMonitor<CodeInsightsOptions>>();
        monitor.CurrentValue.Returns(new CodeInsightsOptions());
        var scope = Query().Views[0];
        var result = await new ReviewerPerformanceRangeReader(db, options: monitor).QueryAsync(
            Query() with
            {
                Views = [scope, scope], Breakdown = new("type", "qualifier", Date)
            }, [Client]);
        _ = monitor.Received(1).CurrentValue;
        var first = result.Views[0].Series[0].Points[0];
        var second = result.Views[1].Series[0].Points[0];
        var matrix = Assert.Single(result.Views[0].Breakdown).Measurement;
        Assert.Equal(first.Score.Scenarios, second.Score.Scenarios);
        Assert.Equal(first.Score.Scenarios, matrix.Score.Scenarios);
        Assert.Equal(first.OverallPrecision!.Counts, matrix.OverallPrecision!.Counts);
        Assert.Equal(first.Coverage!.CandidatePullRequests, matrix.Coverage!.CandidatePullRequests);
    }

    [Theory]
    [InlineData("cumulative", "day")]
    [InlineData("cumulative", "week")]
    [InlineData("cumulative", "month")]
    [InlineData("period", "day")]
    [InlineData("period", "week")]
    [InlineData("period", "month")]
    public async Task SeriesAndMatrixUseMatchingWindowCountsCoverageAndAvailability(string aggregation, string bucket)
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionComplete");
        AddPullRequest(db, 2, "collectionUnknown", wrong: 7);
        var later = AddPullRequest(db, 3, "collectionComplete", positive: 5);
        foreach (var row in db.ChangeTracker.Entries<ReviewerPerformanceDailyCount>().Where(row => row.Entity.CodeInsightPullRequestId == later.Id))
        {
            row.Entity.BucketDate = Date.AddDays(10);
        }

        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var query = Query() with
        {
            Bucket = bucket, Aggregation = aggregation, Views = [Query().Views[0] with { To = Date.AddDays(15) }]
        };
        var series = (await reader.QueryAsync(query, [Client])).Views[0].Series[0];
        foreach (var point in series.Points)
        {
            var response = await reader.QueryAsync(query with { Breakdown = new("client", "repository", point.Date) }, [Client]);
            var cell = response.Views[0].Breakdown.SingleOrDefault();
            if (cell is null)
            {
                Assert.Null(point.Score.Summary.F1);
                Assert.Equal(0, point.Coverage!.CandidatePullRequests);
                continue;
            }

            var matrix = cell.Measurement;
            Assert.Equal(point.WindowFrom, matrix.WindowFrom);
            Assert.Equal(point.WindowTo, matrix.WindowTo);
            Assert.Equal(point.Score.Counts, matrix.Score.Counts);
            Assert.Equal(point.Score.Scenarios, matrix.Score.Scenarios);
            Assert.Equal(point.Score.Summary, matrix.Score.Summary);
            Assert.Equal(point.OverallPrecision!.Counts, matrix.OverallPrecision!.Counts);
            Assert.Equal(point.Coverage!.CandidatePullRequests, matrix.Coverage!.CandidatePullRequests);
            Assert.Equal(point.Coverage.CoveredPullRequests, matrix.Coverage.CoveredPullRequests);
            Assert.Equal(point.Coverage.ExcludedPullRequests, matrix.Coverage.ExcludedPullRequests);
            Assert.Equal(point.Coverage.MeetsMinimum, matrix.Coverage.MeetsMinimum);
            Assert.Equal(point.UnavailableReasons, matrix.UnavailableReasons);
        }
    }

    [Fact]
    public async Task MissingModelProofExcludesItsSourceAndExplicitEmptyDimensionHasNoPopulation()
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionComplete");
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var model = await reader.QueryAsync(Query() with { Grouping = "model" }, [Client]);
        var point = Assert.Single(model.Views[0].Series).Points[0];
        Assert.True(point.Coverage!.RecallSupported);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Contains(point.Coverage.Reasons, reason => reason.Reason == "model-proof-unavailable");
        Assert.Null(point.Score.Summary.F1);
        Assert.NotNull(point.OverallPrecision!.Summary);
        var empty = await reader.QueryAsync(Query() with { Views = [Query().Views[0] with { Types = [] }] }, [Client]);
        Assert.All(
            empty.Views[0].Series[0].Points, item =>
            {
                Assert.Equal(0, item.Coverage!.CandidatePullRequests);
                Assert.Null(item.OverallPrecision!.Summary);
                Assert.Null(item.Score.Summary.F1);
            });
    }

    [Fact]
    public async Task SourceRepairRestoresLiveEligibilityWithoutChangingCapturedScoresOrExclusions()
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionComplete");
        AddPullRequest(db, 2, "collectionUnknown");
        await db.SaveChangesAsync();
        var settings = new CodeInsightsOptions { MinimumCoveragePercentage = 0 };
        var reader = Reader(db, settings);
        var store = new ReviewerPerformanceReportStore(db, reader);
        var request = new ReviewerPerformanceSaveReportRequest(Guid.NewGuid(), "Captured coverage", Query());
        var saved = await store.SaveAsync(request, [Client]);
        var unknown = await db.ReviewerPerformanceDailyCounts.SingleAsync(row => row.Outcome == "collectionUnknown");
        unknown.Outcome = "collectionComplete";
        settings.MinimumCoveragePercentage = 100;
        await db.SaveChangesAsync();
        var live = await reader.QueryAsync(Query(), [Client]);
        var opened = (await store.GetAsync(request.Id, [Client]))!;
        Assert.Equal(2, live.Views[0].Series[0].Points[0].Coverage!.CoveredPullRequests);
        Assert.Equal(1, opened.Response.Views[0].Series[0].Points[0].Coverage!.CoveredPullRequests);
        Assert.Equal(0, opened.Response.CoveragePolicy!.MinimumCoveragePercentage);
        Assert.Single(opened.Response.Views[0].ExclusionCatalog!);
        Assert.Equal(
            saved.Response.Views[0].ExclusionCatalog!.Select(entry => (entry.AggregateId, entry.ProviderScope, string.Join('|', entry.Reasons))),
            opened.Response.Views[0].ExclusionCatalog!.Select(entry => (entry.AggregateId, entry.ProviderScope, string.Join('|', entry.Reasons))));
        Assert.Null(await store.GetAsync(request.Id, [Guid.NewGuid()]));
    }

    [Fact]
    public void EnvironmentSettingsBindAndClampIndependentlyFromLegacySealedSample()
    {
        var settings = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(
            settings, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CODE_INSIGHTS_MIN_COVERAGE_PERCENTAGE"] = "65",
                    ["CODE_INSIGHTS_MIN_COVERED_PULL_REQUESTS"] = "3",
                    ["CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS"] = "50",
                }).Build());
        Assert.Equal(65, settings.EffectiveMinimumCoveragePercentage);
        Assert.Equal(3, settings.EffectiveMinimumCoveredPullRequests);
        Assert.Equal(50, settings.MinimumSealedPullRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhitespaceUnknownNamespacesFormOnePopulationAndRetainEveryReason(bool hasKnownSource)
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        if (!hasKnownSource)
        {
            foreach (var row in db.ChangeTracker.Entries<ReviewerPerformanceDailyCount>())
            {
                row.Entity.ProviderScope = "";
            }
        }

        var unknown = Cell(pr, "missFailed");
        unknown.ProviderScope = " \t";
        unknown.IsMiss = true;
        var other = Cell(pr, "missActed");
        other.ProviderScope = "";
        other.IsMiss = true;
        db.AddRange(unknown, other);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client]);
        var point = result.Views[0].Series[0].Points[0];
        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        var exclusion = Assert.Single(result.Views[0].ExclusionCatalog!);
        Assert.Contains("miss-judgement-failed", exclusion.Reasons);
        Assert.Contains("miss-provider-scope-unavailable", exclusion.Reasons);
    }

    [Fact]
    public async Task EqualRepositoryAndPullRequestNumbersAcrossClientsDoNotMergeOrLeakExclusions()
    {
        await using var db = Db();
        var other = Guid.NewGuid();
        AddPullRequest(db, 1, "collectionUnknown");
        AddPullRequest(db, 1, "collectionUnknown", clientId: other);
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var combined = await reader.QueryAsync(Query(), [Client, other]);
        Assert.Equal(2, combined.Views[0].Series[0].Points[0].Coverage!.CandidatePullRequests);
        var narrowed = await reader.QueryAsync(Query() with { Views = [Query().Views[0] with { ClientIds = [Client] }] }, [Client, other]);
        Assert.Equal(Client, Assert.Single(narrowed.Views[0].ExclusionCatalog!).ClientId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.QueryAsync(
            Query() with { Views = [Query().Views[0] with { ClientIds = [other] }] }, [Client]));
    }

    [Fact]
    public async Task CompleteCollectionWithoutScoredOutcomesKeepsAnEmptyDenominatorUnavailable()
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionComplete", positive: 0);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client]);
        var point = result.Views[0].Series[0].Points[0];
        Assert.True(point.Coverage!.MeetsMinimum);
        Assert.Equal(1, point.Coverage.CoveredPullRequests);
        Assert.Null(point.Score.Summary.F1);
        Assert.Null(point.OverallPrecision!.Summary);
    }

    [Theory]
    [InlineData("collectionUnknown")]
    [InlineData("missFailed")]
    public async Task LaterBlockerDoesNotChangeAnEarlierCoveredBucket(string outcome)
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        var later = Cell(pr, outcome);
        later.IsMiss = outcome == "missFailed";
        later.BucketDate = Date.AddDays(1);
        db.Add(later);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query() with { Breakdown = new("type", "qualifier", Date) }, [Client]);
        var points = result.Views[0].Series[0].Points;
        Assert.NotNull(points[0].Score.Summary.F1);
        Assert.Equal(1, points[0].Coverage!.CoveredPullRequests);
        Assert.Null(points[1].Score.Summary.F1);
        Assert.Equal(1, points[1].Coverage!.ExcludedPullRequests);
        Assert.Null(points[2].Score.Summary.F1);
        Assert.NotNull(points[2].OverallPrecision!.Summary);
        var earlierMatrix = Assert.Single(result.Views[0].Breakdown).Measurement;
        Assert.Equal(points[0].Score.Scenarios, earlierMatrix.Score.Scenarios);
        Assert.Empty(earlierMatrix.Coverage!.ExclusionIds);
    }

    [Fact]
    public async Task ComparedViewsWithDifferentStartsDoNotShareCoveragePrefixes()
    {
        await using var db = Db();
        var pr = AddPullRequest(db, 1, "collectionComplete");
        var later = Cell(pr, "positive", 7);
        later.BucketDate = Date.AddDays(1);
        db.Add(later);
        await db.SaveChangesAsync();
        var scope = Query().Views[0];
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            Query() with
            {
                Views = [scope with { Types = ["concurrency"] }, scope with { From = Date.AddDays(1), Types = ["concurrency"] }]
            }, [Client]);
        Assert.NotNull(result.Views[0].Series[0].Points[^1].Score.Summary.F1);
        Assert.Equal(9, result.Views[0].Series[0].Points[^1].Score.Counts.Outcomes.Positive);
        var shortened = result.Views[1].Series[0].Points[^1];
        Assert.Null(shortened.Score.Summary.F1);
        Assert.Equal(0, shortened.Coverage!.CoveredPullRequests);
        Assert.Equal(7, shortened.OverallPrecision!.Counts.Outcomes.Positive);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("repository")]
    public async Task CoverageOnlyScopesRemainInspectableInGroupedTimelinesAndScopeMatrices(string dimension)
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionUnknown", positive: 0);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            Query() with
            {
                Grouping = dimension, Breakdown = new("client", "repository", Date),
            }, [Client]);
        var point = Assert.Single(result.Views[0].Series).Points[0];
        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Null(point.Score.Summary.F1);
        var matrix = Assert.Single(result.Views[0].Breakdown).Measurement;
        Assert.Equal(point.Coverage.ExclusionIds, matrix.Coverage!.ExclusionIds);
        Assert.Single(result.Views[0].ExclusionCatalog!);
    }

    [Fact]
    public async Task CumulativeEvidenceRemainsMeasurableAcrossCoverageOnlyAndInactivePeriodGaps()
    {
        await using var db = Db();
        for (var i = 1; i <= 4; i++)
        {
            AddPullRequest(db, i, "collectionComplete");
        }

        var uncollected = AddPullRequest(db, 5, "collectionUnknown", positive: 0);
        foreach (var entry in db.ChangeTracker.Entries<ReviewerPerformanceDailyCount>().Where(entry => entry.Entity.CodeInsightPullRequestId == uncollected.Id))
        {
            entry.Entity.BucketDate = Date.AddDays(1);
        }

        var late = AddPullRequest(db, 6, "collectionComplete", positive: 5);
        foreach (var entry in db.ChangeTracker.Entries<ReviewerPerformanceDailyCount>().Where(entry => entry.Entity.CodeInsightPullRequestId == late.Id))
        {
            entry.Entity.BucketDate = Date.AddDays(2);
        }

        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var cumulative = (await reader.QueryAsync(Query(), [Client])).Views[0].Series[0].Points;
        Assert.All(cumulative, point => Assert.NotNull(point.Score.Summary.F1));
        Assert.Equal(13, cumulative[^1].Score.Counts.Outcomes.Positive);
        var period = (await reader.QueryAsync(Query() with { Aggregation = "period" }, [Client])).Views[0].Series[0].Points;
        Assert.NotNull(period[0].Score.Summary.F1);
        Assert.Null(period[1].Score.Summary.F1);
        Assert.Contains("no-series-observation-in-period", period[1].UnavailableReasons);
        Assert.NotNull(period[2].Score.Summary.F1);
        Assert.Equal(5, period[2].Score.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task LegacyCaptureDeserializesWithoutInventingCoverageOrRecalculatingScores()
    {
        await using var db = Db();
        var score = ReviewerPerformanceScoreCalculator.Compute(new(new(Positive: 3, Wrong: 1), default));
        score = score with
        {
            Scenarios = score.Scenarios.Select(scenario => scenario with
            {
                Id = scenario.Id.Replace(ReviewerPerformanceScoreCalculator.Version, "reviewer-performance-v1", StringComparison.Ordinal)
            }).ToArray()
        };
        var legacy = new ReviewerPerformanceRangeResponse(
            "reviewer-performance-v1", DateTimeOffset.UtcNow, Query(),
            score.Scenarios.Select(scenario => scenario.Id).ToArray(),
            [new(Query().Views[0], new([], [], [], [], []), [new("all", "Captured", [new(Date, score, [])])], [], 1, new(null, null, 1, 0))]);
        var id = Guid.NewGuid();
        db.Add(
            new ReviewerPerformanceReport
            {
                Id = id, Name = "Legacy capture", CalculationVersion = "reviewer-performance-v1", CapturedAt = legacy.CapturedAt,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                Payload = JsonSerializer.Serialize(
                    legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
                Clients = [new() { ReportId = id, ClientId = Client }],
            });
        await db.SaveChangesAsync();
        var opened = (await new ReviewerPerformanceReportStore(db, new(db)).GetAsync(id, [Client]))!;
        Assert.False(opened.CompatibleVersion);
        Assert.Null(opened.Response.CoveragePolicy);
        Assert.Null(opened.Response.Views[0].ExclusionCatalog);
        var point = opened.Response.Views[0].Series[0].Points[0];
        Assert.Null(point.OverallPrecision);
        Assert.Null(point.Coverage);
        Assert.Equal(score.Summary.F1!.Median, point.Score.Summary.F1!.Median);
        Assert.Equal(score.Scenarios.Select(scenario => scenario.Id), point.Score.Scenarios.Select(scenario => scenario.Id));
    }

    [Fact]
    public async Task VersionTwoAdvisoryCaptureRetainsItsScoresAndPayloadAfterEvidenceAndMinimumsChange()
    {
        await using var db = Db();
        AddPullRequest(db, 1, "collectionComplete");
        AddPullRequest(db, 2, "collectionUnknown", positive: 0, wrong: 8);
        await db.SaveChangesAsync();
        var settings = new CodeInsightsOptions { MinimumCoveragePercentage = 80, MinimumCoveredPullRequests = 3 };
        var reader = Reader(db, settings);
        var store = new ReviewerPerformanceReportStore(db, reader);
        var request = new ReviewerPerformanceSaveReportRequest(Guid.NewGuid(), "Covered capture", Query());
        var saved = await store.SaveAsync(request, [Client]);
        var capturedPoint = saved.Response.Views[0].Series[0].Points[0];
        var payload = (await db.ReviewerPerformanceReports.SingleAsync()).Payload;

        var unknown = await db.ReviewerPerformanceDailyCounts.SingleAsync(row => row.Outcome == "collectionUnknown");
        unknown.Outcome = "collectionComplete";
        settings.MinimumCoveragePercentage = 100;
        settings.MinimumCoveredPullRequests = 4;
        await db.SaveChangesAsync();
        var live = await reader.QueryAsync(Query(), [Client]);
        var opened = (await store.GetAsync(request.Id, [Client]))!;

        Assert.Equal("reviewer-performance-v2", saved.Response.CalculationVersion);
        Assert.Equal("reviewer-performance-v2", saved.Report.CalculationVersion);
        Assert.True(opened.CompatibleVersion);
        Assert.Equal("reviewer-performance-v2", opened.Response.CalculationVersion);
        Assert.All(opened.Response.PremiseIds, id => Assert.StartsWith("reviewer-performance-v2/", id, StringComparison.Ordinal));
        var storedPoint = opened.Response.Views[0].Series[0].Points[0];
        Assert.All(storedPoint.Score.Scenarios, scenario => Assert.StartsWith("reviewer-performance-v2/", scenario.Id, StringComparison.Ordinal));
        Assert.All(storedPoint.OverallPrecision!.Scenarios, scenario => Assert.StartsWith("reviewer-performance-v2/", scenario.Id, StringComparison.Ordinal));
        Assert.False(storedPoint.Coverage!.MeetsMinimum);
        Assert.Equal(1, storedPoint.Coverage.CoveredPullRequests);
        Assert.Equal(1, storedPoint.Score.Summary.F1!.Median);
        Assert.Equal(0.2, storedPoint.OverallPrecision.Summary!.Median);
        Assert.Equal(capturedPoint.Score.Scenarios, storedPoint.Score.Scenarios);
        Assert.Equal(capturedPoint.Score.Counts, storedPoint.Score.Counts);
        Assert.Equal(80, opened.Response.CoveragePolicy!.MinimumCoveragePercentage);
        Assert.Equal(3, opened.Response.CoveragePolicy.MinimumCoveredPullRequests);
        var livePoint = live.Views[0].Series[0].Points[0];
        Assert.Equal(2, livePoint.Coverage!.CoveredPullRequests);
        Assert.NotEqual(storedPoint.Score.Summary.F1.Median, livePoint.Score.Summary.F1!.Median);
        Assert.Equal(100, live.CoveragePolicy!.MinimumCoveragePercentage);
        Assert.Equal(payload, (await db.ReviewerPerformanceReports.SingleAsync()).Payload);
    }

    internal static CodeInsightPullRequest AddPullRequest(
        MeisterProPRDbContext db, long number, string coverage, long positive = 2, long wrong = 0, Guid? clientId = null)
    {
        var pr = new CodeInsightPullRequest
            { Id = Guid.NewGuid(), ClientId = clientId ?? Client, RepositoryId = "repo", RepositoryName = "Repository", PullRequestId = number };
        db.Add(pr);
        foreach (var (outcome, count) in new[] { (coverage, 1L), ("positive", positive), ("wrong", wrong) })
        {
            if (count == 0)
            {
                continue;
            }

            db.Add(Cell(pr, outcome, count));
        }

        return pr;
    }

    private static ReviewerPerformanceQuery Query() => new() { Views = [new() { From = Date, To = Date.AddDays(2) }] };

    private static ReviewerPerformanceRangeReader Reader(MeisterProPRDbContext db, CodeInsightsOptions options) =>
        new(db, options: new StaticOptionsMonitor<CodeInsightsOptions>(options));

    private static ReviewerPerformanceDailyCount Cell(CodeInsightPullRequest pr, string outcome, long count = 1) => new()
    {
        Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = pr.ClientId, RepositoryId = pr.RepositoryId, PullRequestId = pr.PullRequestId,
        ProviderScope = "AzureDevOps:https://dev.azure.com/test", BucketDate = Date, Outcome = outcome, Count = count,
        PublicationState = CodeInsightPublicationState.Published, DuplicateState = CodeInsightDuplicateState.CheckedNonduplicate,
        TypeMembership = "concurrency", Qualifier = "Missing", IsClassified = true, ModelId = "model",
    };

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
