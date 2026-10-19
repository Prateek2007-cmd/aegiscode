// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceScopedPopulationTests
{
    private const string KnownScope = "AzureDevOps:https://dev.azure.com/test";
    private static readonly DateOnly Date = new(2026, 9, 18);

    public static TheoryData<string, string, string> UnknownEvidenceCases
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            foreach (var mode in new[] { "combined", "filter", "series", "matrix" })
            {
                foreach (var providerScope in new[] { "", " \t", "\u00A0\u2003" })
                {
                    foreach (var outcome in new[] { "missActed", "collectionUnknown", "positive" })
                    {
                        cases.Add(mode, providerScope, outcome);
                    }
                }
            }

            return cases;
        }
    }

    public static TheoryData<string, string, string> UnknownJudgmentCases
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            foreach (var mode in new[] { "combined", "filter", "series", "matrix" })
            {
                foreach (var providerScope in new[] { "", "\u00A0\u2003" })
                {
                    foreach (var outcome in new[] { "missProvisional", "missActed", "missFailed" })
                    {
                        cases.Add(mode, providerScope, outcome);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(UnknownEvidenceCases))]
    public async Task RepositoryMeasurementsRetainCompatibleUnknownSourceEvidence(string mode, string providerScope, string outcome)
    {
        await using var db = Db();
        var pr = AddCoveredSource(db, Guid.NewGuid());
        db.Add(Cell(pr, outcome, providerScope));
        await db.SaveChangesAsync();

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(mode, pr), [pr.ClientId]);
        var point = Point(result, mode, pr);

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(0, point.Coverage.CoveredPullRequests);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Null(point.Score.Summary.F1);
        var exclusionId = Assert.Single(point.Coverage.ExclusionIds);
        var exclusion = Assert.Single(result.Views[0].ExclusionCatalog!, item => item.Id == exclusionId);
        Assert.Equal(pr.Id, exclusion.AggregateId);
        Assert.Equal(KnownScope, exclusion.ProviderScope);
        Assert.Contains("miss-provider-scope-unavailable", exclusion.Reasons);
    }

    [Theory]
    [MemberData(nameof(UnknownJudgmentCases))]
    public async Task SuccessfulProvisionalJudgmentsRemainInformationalForTheirKnownCoveredSource(string mode, string providerScope, string outcome)
    {
        await using var db = Db();
        var pr = AddCoveredSource(db, Guid.NewGuid());
        db.Add(Cell(pr, outcome, providerScope));
        await db.SaveChangesAsync();

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(mode, pr), [pr.ClientId]);
        var point = Point(result, mode, pr);
        var provisional = outcome == "missProvisional";

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(provisional ? 1 : 0, point.Coverage.CoveredPullRequests);
        Assert.Equal(provisional, point.Score.Summary.F1 is not null);
        if (provisional)
        {
            Assert.Equal(1, point.Score.Summary.F1!.Median);
            Assert.Equal(0, point.Score.Counts.ActedMisses);
            Assert.Contains("miss-observation-provisional", point.UnavailableReasons);
            Assert.DoesNotContain("miss-provider-scope-unavailable", point.UnavailableReasons);
            Assert.Empty(point.Coverage.ExclusionIds);
        }
        else
        {
            Assert.Contains("miss-provider-scope-unavailable", point.UnavailableReasons);
            if (outcome == "missFailed")
            {
                Assert.Contains("miss-judgement-failed", point.UnavailableReasons);
            }
        }
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("filter")]
    [InlineData("series")]
    [InlineData("matrix")]
    public async Task CoverageOnlySourcesRetainUnknownEnumerationEvidenceInRepositoryMeasurements(string mode)
    {
        await using var db = Db();
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        db.AddRange(pr, Cell(pr, "collectionComplete", KnownScope), Cell(pr, "collectionUnknown", "\u00A0\u2003"));
        await db.SaveChangesAsync();
        var query = Query(mode, pr);
        if (mode == "matrix")
        {
            query = query with { Breakdown = new("repository", "client", Date) };
        }

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [pr.ClientId]);
        var point = Point(result, mode, pr);

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Contains("miss-collection-coverage-unavailable", point.UnavailableReasons);
        Assert.Null(point.OverallPrecision!.Summary);
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("filter")]
    [InlineData("series")]
    [InlineData("matrix")]
    public async Task ProvisionalJudgmentsDoNotReplaceIndependentUnknownCollectionEvidence(string mode)
    {
        await using var db = Db();
        var pr = AddCoveredSource(db, Guid.NewGuid());
        db.AddRange(Cell(pr, "missProvisional", "\u00A0\u2003"), Cell(pr, "collectionUnknown", "\u00A0\u2003"));
        await db.SaveChangesAsync();

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(mode, pr), [pr.ClientId]);
        var point = Point(result, mode, pr);

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(1, point.Coverage.ExcludedPullRequests);
        Assert.Null(point.Score.Summary.F1);
        Assert.Contains("miss-collection-coverage-unavailable", point.UnavailableReasons);
        Assert.Contains("miss-observation-provisional", point.UnavailableReasons);
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("filter")]
    [InlineData("series")]
    [InlineData("matrix")]
    public async Task UnknownEvidenceForAnotherClientDoesNotExcludeAKnownSourceWithTheSameRepositoryAndNumber(string mode)
    {
        await using var db = Db();
        var pr = AddCoveredSource(db, Guid.NewGuid());
        var other = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = pr.RepositoryId, PullRequestId = pr.PullRequestId,
        };
        db.AddRange(other, Cell(other, "collectionUnknown", "\u00A0\u2003"));
        await db.SaveChangesAsync();
        var query = Query(mode, pr);
        if (mode == "combined")
        {
            query = query with { Views = [query.Views[0] with { ClientIds = [pr.ClientId] }] };
        }

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [pr.ClientId, other.ClientId]);
        var point = Point(result, mode, pr);

        Assert.Equal(1, point.Coverage!.CandidatePullRequests);
        Assert.Equal(1, point.Coverage.CoveredPullRequests);
        Assert.Equal(1, point.Score.Summary.F1!.Median);
        Assert.Empty(point.Coverage.ExclusionIds);
        Assert.DoesNotContain(result.Views[0].ExclusionCatalog!, item => item.AggregateId == pr.Id);
    }

    [Fact]
    public async Task NonemptyProviderScopesRetainExactIdentityIncludingWhitespace()
    {
        await using var db = Db();
        var pr = AddCoveredSource(db, Guid.NewGuid());
        db.AddRange(Cell(pr, "collectionComplete", KnownScope + " "), Cell(pr, "positive", KnownScope + " "));
        await db.SaveChangesAsync();

        var reader = new ReviewerPerformanceRangeReader(db);
        var combined = await reader.QueryAsync(Query("combined", pr), [pr.ClientId]);
        var filtered = await reader.QueryAsync(Query("filter", pr), [pr.ClientId]);

        Assert.Equal(2, combined.Views[0].Series[0].Points[0].Coverage!.CoveredPullRequests);
        Assert.Equal(2, combined.Views[0].Facets.Repositories.Count);
        Assert.Equal(1, filtered.Views[0].Series[0].Points[0].Coverage!.CoveredPullRequests);
    }

    private static MeisterProPRDbContext Db() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CodeInsightPullRequest AddCoveredSource(MeisterProPRDbContext db, Guid client)
    {
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 1 };
        db.AddRange(pr, Cell(pr, "collectionComplete", KnownScope), Cell(pr, "positive", KnownScope));
        return pr;
    }

    private static ReviewerPerformanceDailyCount Cell(CodeInsightPullRequest pr, string outcome, string providerScope) => new()
    {
        Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = pr.ClientId, RepositoryId = pr.RepositoryId, PullRequestId = pr.PullRequestId,
        ProviderScope = providerScope, BucketDate = Date, Outcome = outcome, Count = 1,
        TypeMembership = "concurrency", Qualifier = "Missing", IsClassified = true, IsMiss = outcome.StartsWith("miss", StringComparison.Ordinal),
        PublicationState = CodeInsightPublicationState.Published, DuplicateState = CodeInsightDuplicateState.CheckedNonduplicate,
    };

    private static string RepositoryKey(CodeInsightPullRequest pr) => ReviewerPerformanceIdentity.RepositoryKey(Cell(pr, "positive", KnownScope));

    private static ReviewerPerformanceQuery Query(string mode, CodeInsightPullRequest pr) => new()
    {
        Views = [new() { From = Date, To = Date, Repositories = mode == "filter" ? [RepositoryKey(pr)] : null }],
        Grouping = mode == "series" ? "repository" : "none",
        Breakdown = mode == "matrix" ? new("repository", "type", Date) : null,
    };

    private static ReviewerPerformancePoint Point(ReviewerPerformanceRangeResponse result, string mode, CodeInsightPullRequest pr) => mode switch
    {
        "series" => Assert.Single(result.Views[0].Series, series => series.Id == RepositoryKey(pr)).Points[0],
        "matrix" => Assert.Single(result.Views[0].Breakdown, cell => cell.RowId == RepositoryKey(pr)).Measurement,
        _ => result.Views[0].Series[0].Points[0],
    };
}
