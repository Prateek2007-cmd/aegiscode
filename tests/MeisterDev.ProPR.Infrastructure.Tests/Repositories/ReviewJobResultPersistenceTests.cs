// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration1")]
public sealed class ReviewJobResultPersistenceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _db = null!;
    private JobRepository _repository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, builder => builder.UseVector()).Options;
        this._db = new MeisterProPRDbContext(options);
        await this._db.ReviewJobs.ExecuteDeleteAsync();
        this._repository = new JobRepository(this._db, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompletedJob_RoundTripsFinalWithheldEvidenceAndAllPublicAnnotationsThroughPostgres()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var withheld = new WithheldReviewFinding(
            new ReviewComment("src/Withheld.cs", 17, CommentSeverity.Warning, "Potential defect.")
            {
                OriginModelId = "review-model", OriginLogicalModelName = "reviewer", OriginPassKind = "MultiPassUnion",
                OriginPassIndex = 2, OriginPassLens = "security", OriginPassShadow = true,
                OriginSymbolName = "Execute", OriginSymbolKind = "Method", ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
                SourceReadGrounding = ReviewCommentReadGrounding.Covered,
            }, "judge_output_limit");
        var result = new ReviewResult("Summary", [new ReviewComment("src/Published.cs", 12, CommentSeverity.Warning, "Published finding.")])
        {
            WithheldFindings = [withheld],
            CarriedForwardFilePaths = ["src/Carried.cs"], CarriedForwardCandidatesSkipped = 3,
            ContextDegradedFilePaths = ["src/Degraded.cs"], ContextSkippedFilePaths = ["src/Skipped.cs"],
            BudgetSoftCapped = true, BudgetSoftCapThresholdUsd = 12.5m, BudgetSoftCapSpentUsd = 13.25m,
            BudgetSoftCapSkippedFilePaths = ["src/BudgetSkipped.cs"],
        };

        await this._repository.SetResultAsync(job.Id, result);
        this._db.ChangeTracker.Clear();
        var loaded = await this._repository.GetByIdWithFileResultsAsync(job.Id);

        var persisted = Assert.IsType<ReviewResult>(loaded!.Result);
        Assert.Equal(withheld, Assert.Single(persisted.WithheldFindings));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(persisted));
        Assert.Single(persisted.Comments);
        Assert.DoesNotContain(persisted.Comments, comment => comment.FilePath == withheld.Comment.FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderJobResult_MissingOrNullWithheldEvidenceRestoresAllPublicAnnotations(bool includeNullField)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var result = new ReviewResult("Legacy summary", [new ReviewComment("src/Reviewed.cs", 7, CommentSeverity.Info, "Original finding.")])
        {
            CarriedForwardFilePaths = ["src/Carried.cs"], CarriedForwardCandidatesSkipped = 2,
            ContextDegradedFilePaths = ["src/Degraded.cs"], ContextSkippedFilePaths = ["src/Skipped.cs"],
            BudgetSoftCapped = true, BudgetSoftCapThresholdUsd = 5.5m, BudgetSoftCapSpentUsd = 6.25m,
            BudgetSoftCapSkippedFilePaths = ["src/BudgetSkipped.cs"],
        };
        await this._repository.SetResultAsync(job.Id, result);
        var legacyPayload = JsonSerializer.SerializeToNode(result)!.AsObject();
        if (includeNullField)
        {
            legacyPayload[nameof(ReviewResult.WithheldFindings)] = null;
        }

        var json = legacyPayload.ToJsonString();
        await this._db.Database.ExecuteSqlInterpolatedAsync($"UPDATE review_jobs SET result_json = {json}::jsonb WHERE id = {job.Id}");
        this._db.ChangeTracker.Clear();

        var loaded = await this._repository.GetByIdWithFileResultsAsync(job.Id);

        var persisted = Assert.IsType<ReviewResult>(loaded!.Result);
        Assert.Empty(persisted.WithheldFindings);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(persisted));
    }
}
