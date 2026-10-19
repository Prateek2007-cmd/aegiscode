// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class WithheldFindingEvidenceTests
{
    [Fact]
    public async Task ReplayedMixedEvidence_CountsWithheldOnceWithoutPublicationOrPrecisionCredit()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var ingestion = new CodeInsightFindingIngestionService(
            new CodeInsightFindingStore(db, codec), gate,
            NullLogger<CodeInsightFindingIngestionService>.Instance);
        var evt = Event();

        await ingestion.HandleReviewFindingsProducedAsync(evt);
        var originalIds = await db.CodeInsightFindings.OrderBy(row => row.Ordinal).Select(row => row.Id).ToListAsync();
        await ingestion.HandleReviewFindingsProducedAsync(evt);
        await ingestion.HandleReviewFindingsProducedAsync(evt with { JobId = Guid.NewGuid() });

        Assert.Equal(originalIds, await db.CodeInsightFindings.OrderBy(row => row.Ordinal).Select(row => row.Id).ToListAsync());
        await ingestion.HandleReviewFindingsProducedAsync(
            evt with
            {
                JobId = Guid.NewGuid(),
                Findings =
                [
                    evt.Findings[0],
                    evt.Findings[0] with
                    {
                        Ordinal = 1,
                        LineNumber = 11,
                        Message = "Additional published finding.",
                        ProviderThreadId = "additional-thread",
                        ProviderCommentId = "additional-comment",
                    },
                    evt.Findings[1],
                ],
            });
        var replayIds = await db.CodeInsightFindings.Select(row => row.Id).ToListAsync();
        Assert.Equal(3, replayIds.Count);
        Assert.All(originalIds, id => Assert.Contains(id, replayIds));
        var withheld = await db.CodeInsightFindings.SingleAsync(row => row.PublicationState == CodeInsightPublicationState.PolicyWithheld);
        Assert.Equal("judge_output_limit", withheld.PublicationReason);
        Assert.Null(withheld.ProviderThreadId);
        Assert.Null(withheld.ProviderCommentId);
        // Even an independently attached outcome cannot give a nonpublished finding publication credit.
        foreach (var row in await db.CodeInsightFindings.ToListAsync())
        {
            row.NativeStatus = "Fixed";
            row.CurrentDisposition = CodeInsightDisposition.Addressed;
        }

        await db.SaveChangesAsync();
        var prId = withheld.CodeInsightPullRequestId;
        await new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance).ProjectPullRequestAsync(prId);
        var cells = await db.ReviewerPerformanceDailyCounts.ToListAsync();
        var measurement = ReviewerPerformanceMeasurement.Measure(
            DateOnly.FromDateTime(evt.ObservedAt.UtcDateTime), cells, cells,
            new ReviewerPerformanceViewQuery(), "overall");

        Assert.Equal(3, measurement.OverallPrecision!.Counts.Generated);
        Assert.Equal(1, measurement.OverallPrecision!.Counts.Withheld);
        Assert.Equal(2, measurement.OverallPrecision!.Counts.Outcomes.Positive);
        Assert.Equal(2, measurement.OverallPrecision!.Counts.Outcomes.Total);
        Assert.Contains("miss-collection-coverage-unavailable", measurement.UnavailableReasons);
        Assert.Null(measurement.Score.Summary.Recall);
    }

    [Fact]
    public async Task AClosedCollectionGate_CreatesNoWithheldAnalyticsRecords()
    {
        await using var db = Db();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var ingestion = new CodeInsightFindingIngestionService(
            new CodeInsightFindingStore(db, Substitute.For<ISecretProtectionCodec>()), gate,
            NullLogger<CodeInsightFindingIngestionService>.Instance);

        await ingestion.HandleReviewFindingsProducedAsync(Event());

        Assert.Empty(await db.CodeInsightFindings.ToListAsync());
        Assert.Empty(await db.CodeInsightPullRequests.ToListAsync());
    }

    private static ReviewFindingsProducedEvent Event() => new(
        Guid.NewGuid(), "repo", 1, Guid.NewGuid(), "revision", "Active", DateTimeOffset.UtcNow,
        [
            new(
                0, "src/Foo.cs", 10, CommentSeverity.Warning, "Published finding.", "Baseline", null, null, false, null, null, "thread", "comment",
                PublicationState: CodeInsightPublicationState.Published, ProviderScope: "provider"),
            new(
                -1, "src/Foo.cs", 12, CommentSeverity.Warning, "Withheld finding.", "Baseline", null, null, false, null, null, null, null,
                PublicationState: CodeInsightPublicationState.PolicyWithheld, PublicationReason: "judge_output_limit", ProviderScope: "provider"),
        ]);

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
