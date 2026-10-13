// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class ReviewerPerformancePostgresIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private MeisterProPRDbContext _db = null!;
    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options;
        this._db = new MeisterProPRDbContext(this._options);
        var tenant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        this._db.Tenants.Add(
            new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Performance test", IsActive = true, CreatedAt = now, UpdatedAt = now });
        this._db.Clients.AddRange(
            new ClientRecord { Id = this._a, TenantId = tenant, DisplayName = "Client A", IsActive = true, CreatedAt = now },
            new ClientRecord { Id = this._b, TenantId = tenant, DisplayName = "Client B", IsActive = true, CreatedAt = now });
        await this._db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task MaximumWidthExposureIdentityPersistsOnceInPostgres()
    {
        var random = new Random(173);
        var path = string.Concat(
            Enumerable.Range(0, 512).Select(index =>
                index % 50 == 49 ? "/" : char.ConvertFromUtf32(random.Next(0x20000, 0x2FFFF))));
        var model = string.Concat(Enumerable.Range(0, 200).Select(_ => (char)random.Next(0x1000, 0xD700)));
        var logical = string.Concat(Enumerable.Range(0, 100).Select(_ => (char)random.Next('a', 'z' + 1)));
        var key = new CodeInsightPullRequestKey(this._a, "wide-exposure", 1);
        var job = Guid.NewGuid();
        var collector = new CodeInsightReviewExposureCollector(this._db, OpenGate(), NullLogger<CodeInsightReviewExposureCollector>.Instance);
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await collector.RecordAsync(
                key, job, path, "head", model, logical, "AzureDevOps:https://ado.example.test/collection", "completed-file-baseline", now);
        }

        Assert.Equal(1, await this._db.CodeInsightReviewExposures.CountAsync(row => row.JobId == job));
    }

    [Fact]
    public async Task HighCardinalityRepositoryFacetsRetainAuthorizedNamesWithinFiveSeconds()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = Enumerable.Range(0, 6000).Select(index => new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = $"facet-{index}", RepositoryName = $"Repository {index}", PullRequestId = 1,
            CreatedAt = now, UpdatedAt = now, PerformanceProjectionVersion = 1, PerformanceProjectedAt = now,
        }).ToList();
        this._db.CodeInsightPullRequests.AddRange(sources);
        await this._db.SaveChangesAsync();
        await this._db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO reviewer_performance_daily_counts
             ("Id", "CellKey", "CodeInsightPullRequestId", "ClientId", "RepositoryId", "ProviderScope", "PullRequestId", "BucketDate", "ModelId", "LogicalModelName", "TypeMembership", "Qualifier", "Outcome", "PublicationState", "DuplicateState", "IsMiss", "IsClassified", "Count", "UpdatedAt")
             SELECT gen_random_uuid(), md5(pr.id::text || '-' || m::text), pr.id, pr.client_id, pr.repository_id,
                'AzureDevOps:https://dev.azure.com/test', 1, DATE '2026-09-01', 'model-' || m, 'review', 'logic-error',
                'Incorrect', 'positive', 1, 1, false, true, 1, {now}
             FROM code_insight_pull_requests pr CROSS JOIN generate_series(0,5) m WHERE pr.client_id = {this._a}
             """);
        var query = new ReviewerPerformanceQuery
            { Views = [new ReviewerPerformanceViewQuery { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 9, 1), ClientIds = [this._a] }] };
        var timer = Stopwatch.StartNew();
        var result = await new ReviewerPerformanceRangeReader(this._db).QueryAsync(query, [this._a]);
        timer.Stop();
        Assert.Equal(6000, result.Views[0].Facets.Repositories.Count);
        Assert.All(result.Views[0].Facets.Repositories, facet => Assert.StartsWith("Repository ", facet.Label, StringComparison.Ordinal));
        Assert.Equal(36_000, result.Views[0].Series[0].Points[0].Score.Counts.Outcomes.Positive);
        Console.WriteLine($"Repository facets: 6000 repositories, 36000 joint cells, {timer.Elapsed.TotalMilliseconds:F0} ms; target <5000 ms.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Repository facets took {timer.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task ExposureFingerprintPreservesTupleBoundariesAndUnicodeIdentity()
    {
        var key = new CodeInsightPullRequestKey(this._a, "identity-encoding", 1);
        var collector = new CodeInsightReviewExposureCollector(this._db, OpenGate(), NullLogger<CodeInsightReviewExposureCollector>.Instance);
        var job = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await collector.RecordAsync(key, job, "a|b", "head", "c", "", "GitHub:https://example.test", "completed-file-baseline", now);
        await collector.RecordAsync(key, job, "a", "head", "b|c", "", "GitHub:https://example.test", "completed-file-baseline", now);
        await collector.RecordAsync(key, job, "src/café.cs", "head", "model", "", "GitHub:https://example.test", "completed-file-baseline", now);
        await collector.RecordAsync(key, job, "src/cafe\u0301.cs", "head", "model", "", "GitHub:https://example.test", "completed-file-baseline", now);
        var identities = await this._db.CodeInsightReviewExposures.Where(row => row.JobId == job).Select(row => row.IdentityFingerprint).ToListAsync();
        Assert.Equal(4, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identities, identity => Assert.Equal(64, identity.Length));
    }

    [Fact]
    public async Task ExposureMigrationBackfillsExistingIdentitiesAndPreservesCaptureIdempotency()
    {
        var databaseName = $"propr_exposure_upgrade_{Guid.NewGuid():N}";
        await fixture.CreateDatabaseAtMigrationAsync(databaseName, "20261003031757_OrderReviewerPerformanceMissObservations");
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName };
        try
        {
            await using var db = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>()
                    .UseNpgsql(builder.ConnectionString, options => options.UseVector()).Options);
            var now = DateTimeOffset.UtcNow;
            var tenant = Guid.NewGuid();
            var client = Guid.NewGuid();
            var pr = Guid.NewGuid();
            var job = Guid.NewGuid();
            db.AddRange(
                new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Exposure upgrade", IsActive = true, CreatedAt = now },
                new ClientRecord { Id = client, TenantId = tenant, DisplayName = "Exposure upgrade", IsActive = true, CreatedAt = now });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO code_insight_pull_requests
                 (id, client_id, repository_id, pull_request_id, pull_request_state, latest_revision_key, last_activity_at, created_at, updated_at)
                 VALUES ({pr}, {client}, 'upgrade', 1, 'Active', '', {now}, {now}, {now})
                 """);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO code_insight_review_exposures
                 ("Id", "CodeInsightPullRequestId", "JobId", "FilePath", "RevisionKey", "ModelId", "LogicalModelName", "ProviderScope", "Source", "ObservedAt")
                 VALUES ({Guid.NewGuid()}, {pr}, {job}, 'src/café.cs', 'head', 'model', '', 'GitHub:https://example.test', 'completed-file-baseline', {now})
                 """);
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(64, (await db.CodeInsightReviewExposures.SingleAsync()).IdentityFingerprint.Length);
            var collector = new CodeInsightReviewExposureCollector(db, OpenGate(), NullLogger<CodeInsightReviewExposureCollector>.Instance);
            await collector.RecordAsync(
                new CodeInsightPullRequestKey(client, "upgrade", 1), job, "src/café.cs", "head", "model", "", "GitHub:https://example.test",
                "completed-file-baseline", now);
            Assert.Equal(1, await db.CodeInsightReviewExposures.CountAsync());
        }
        finally
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentCaptureRetriesReturnOneImmutableResponse()
    {
        var request = Request([this._a]);

        async Task<ReviewerPerformanceSavedReport> Capture()
        {
            await using var db = new MeisterProPRDbContext(this._options);
            return await new ReviewerPerformanceReportStore(db, new ReviewerPerformanceRangeReader(db)).SaveAsync(request, [this._a]);
        }

        var captures = await Task.WhenAll(Capture(), Capture(), Capture());
        Assert.All(captures, report => Assert.Equal(captures[0].Response.CapturedAt, report.Response.CapturedAt));
        Assert.NotNull(captures[0].Response.EvidenceRevision);
        Assert.Equal(1, await this._db.ReviewerPerformanceReports.CountAsync(row => row.Id == request.Id));
    }

    [Fact]
    public async Task ClientDatabaseDeletionRemovesWholeMixedReportAndPreservesOtherReports()
    {
        var store = new ReviewerPerformanceReportStore(this._db, new ReviewerPerformanceRangeReader(this._db));
        var mixed = Request([this._a, this._b]);
        var onlyB = Request([this._b]);
        await store.SaveAsync(mixed, [this._a, this._b]);
        await store.SaveAsync(onlyB, [this._a, this._b]);
        await this._db.Clients.Where(row => row.Id == this._a).ExecuteDeleteAsync();
        Assert.False(await this._db.ReviewerPerformanceReports.AnyAsync(row => row.Id == mixed.Id));
        Assert.False(await this._db.ReviewerPerformanceReportClients.AnyAsync(row => row.ReportId == mixed.Id));
        Assert.True(await this._db.ReviewerPerformanceReports.AnyAsync(row => row.Id == onlyB.Id));
    }

    [Fact]
    public async Task ReplacementKeepsDifferentThreadsWithEqualRootCommentIdsAndRefreshesNativeStatus()
    {
        var pr = new CodeInsightPullRequest
            { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "receipt-repo", PullRequestId = 1, CreatedAt = DateTimeOffset.UtcNow };
        this._db.Add(pr);
        var finding = Finding(pr.Id, "thread-a");
        this._db.AddRange(finding, Finding(pr.Id, "thread-b"), Finding(pr.Id, "thread-a"));
        await this._db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(this._db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await projector.ProjectPullRequestAsync(pr.Id);
        await projector.ProjectPullRequestAsync(pr.Id);
        Assert.Equal(
            2,
            await this._db.ReviewerPerformanceDailyCounts
                .Where(row => row.CodeInsightPullRequestId == pr.Id && row.PublicationState == CodeInsightPublicationState.Published)
                .SumAsync(row => row.Count));
        Assert.Equal(
            1,
            await this._db.ReviewerPerformanceDailyCounts
                .Where(row => row.CodeInsightPullRequestId == pr.Id && row.PublicationState == CodeInsightPublicationState.SuppressedRepeat)
                .SumAsync(row => row.Count));
        await new CodeInsightPerformanceEvidenceStore(this._db).RecordCurrentOutcomeAsync(
            finding.Id, "ByDesign",
            new CodeInsightDispositionRecord(
                CodeInsightDisposition.Acknowledged, ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unknown, null, null,
                NativeStatus: "ByDesign"), DateTimeOffset.UtcNow);
        await projector.BackfillAsync(100);
        Assert.Contains(
            await this._db.ReviewerPerformanceDailyCounts.Where(row => row.CodeInsightPullRequestId == pr.Id).ToListAsync(), row => row.Outcome == "byDesign");
    }

    [Fact]
    public async Task ScopedMissIndexRetainsSameThreadInSeparateProviderOrganizations()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(), ClientId = this._a, Provider = ScmProvider.AzureDevOps, HostBaseUrl = "https://dev.azure.com/orgA", DisplayName = "Scope A",
            CreatedAt = now, UpdatedAt = now,
        };
        var b = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(), ClientId = this._a, Provider = ScmProvider.AzureDevOps, HostBaseUrl = "https://dev.azure.com/orgB", DisplayName = "Scope B",
            CreatedAt = now, UpdatedAt = now,
        };
        this._db.AddRange(a, b);
        await this._db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected test discussion");
        var store = new CodeInsightFindingStore(this._db, codec);
        var key = new CodeInsightPullRequestKey(this._a, "1", 1);
        var miss = new CodeInsightMissRecord(
            "1", "src/Service.cs", 1, "human concern", true, true, true, .9, "test-judge", true, ConnectionId: a.Id, SourceFingerprint: "scope-a");
        Assert.True(await store.RecordMissAsync(key, miss));
        Assert.True(await store.RecordMissAsync(key, miss with { ConnectionId = b.Id, SourceFingerprint = "scope-b" }));
        Assert.False(await store.RecordMissAsync(key, miss));
        Assert.Null(await store.GetObservationAsync(key, "1"));
        Assert.True(await store.RejudgeMissAsync(key, miss with { ConnectionId = b.Id, WasActedOn = false, SourceFingerprint = "scope-b-updated" }));
        Assert.Equal("scope-a", (await store.GetObservationAsync(key, "1", connectionId: a.Id))!.SourceFingerprint);
        Assert.Equal("scope-b-updated", (await store.GetObservationAsync(key, "1", connectionId: b.Id))!.SourceFingerprint);
    }

    [Fact]
    public async Task NinetyDayJointPopulationQueryHasNoTruncationAndCompletesWithinFiveSeconds()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = Enumerable.Range(0, 30).Select(i => new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = $"benchmark-{i}", PullRequestId = i, CreatedAt = now, PerformanceProjectionVersion = 1,
            PerformanceProjectedAt = now,
        }).ToList();
        this._db.CodeInsightPullRequests.AddRange(sources);
        await this._db.SaveChangesAsync();
        await this._db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO reviewer_performance_daily_counts
             ("Id", "CellKey", "CodeInsightPullRequestId", "ClientId", "RepositoryId", "ProviderScope", "PullRequestId", "BucketDate", "ModelId", "LogicalModelName", "TypeMembership", "Qualifier", "Outcome", "PublicationState", "DuplicateState", "IsMiss", "IsClassified", "Count", "UpdatedAt")
             SELECT gen_random_uuid(), md5(pr.id::text || '-' || d::text || '-' || m::text || '-' || t::text || '-' || o::text), pr.id, pr.client_id, pr.repository_id, 'AzureDevOps:https://dev.azure.com/test', pr.pull_request_id,
                 DATE '2026-07-01' + d, 'model-' || m, 'review', CASE t WHEN 0 THEN 'logic-error' WHEN 1 THEN 'concurrency' ELSE 'logic-error|concurrency' END,
                 'Incorrect', CASE o WHEN 0 THEN 'positive' WHEN 1 THEN 'wrong' WHEN 2 THEN 'dismissed' ELSE 'wontFix' END, 1, 1, false, true, 2, {now}
             FROM code_insight_pull_requests pr CROSS JOIN generate_series(0,89) d CROSS JOIN generate_series(0,2) m CROSS JOIN generate_series(0,2) t CROSS JOIN generate_series(0,3) o
             WHERE pr.client_id = {this._a}
             """);
        var query = new ReviewerPerformanceQuery
        {
            Grouping = "model",
            Views = [new ReviewerPerformanceViewQuery { From = new DateOnly(2026, 7, 1), To = new DateOnly(2026, 9, 28), ClientIds = [this._a] }],
        };
        var timer = Stopwatch.StartNew();
        var result = await new ReviewerPerformanceRangeReader(this._db).QueryAsync(query, [this._a]);
        timer.Stop();
        Assert.Equal(97_200, result.Views[0].AggregateCells);
        Assert.Equal(194_400, result.Views[0].Series.Sum(series => series.Points[^1].Score.Counts.Outcomes.Total));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"90-day query over 97,200 joint cells took {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Console.WriteLine(
            $"Reviewer performance benchmark: 97,200 retained cells, 90 days, 3 series, {timer.Elapsed.TotalMilliseconds:F0} ms, target <5000 ms.");
    }

    [Fact]
    public async Task SourceCommitAfterProjectionReadRemainsPendingEvenWithAnOlderWriterTimestamp()
    {
        var pr = new CodeInsightPullRequest
            { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "commit-race", PullRequestId = 1, CreatedAt = DateTimeOffset.UtcNow };
        this._db.Add(pr);
        await this._db.SaveChangesAsync();
        var barrier = new CommandBarrier(
            command => command.StartsWith("SELECT", StringComparison.Ordinal) && command.Contains("FROM code_insight_findings", StringComparison.Ordinal),
            true);
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(barrier).Options;
        await using var projectionDb = new MeisterProPRDbContext(options);
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(projectionDb, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var projection = projector.ProjectPullRequestAsync(pr.Id);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var writer = new MeisterProPRDbContext(this._options);
        var finding = Finding(pr.Id, "late-commit");
        writer.Add(finding);
        writer.Entry(finding).Property(row => row.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-5);
        var write = writer.SaveChangesAsync();
        await Task.Delay(100);
        barrier.Release.TrySetResult();
        await projection;
        await write;
        await using var repairDb = new MeisterProPRDbContext(this._options);
        var repair = new ReviewerPerformanceCountProjector(repairDb, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        Assert.True(await repair.BackfillAsync(100) > 0);
        Assert.Equal(
            1,
            await repairDb.ReviewerPerformanceDailyCounts
                .Where(row => row.CodeInsightPullRequestId == pr.Id && row.PublicationState == CodeInsightPublicationState.Published)
                .SumAsync(row => row.Count));
    }

    [Fact]
    public async Task ConcurrentCurrentOutcomesKeepTheNewestObservationAcrossPublicationCopies()
    {
        var pr = new CodeInsightPullRequest
            { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "outcome-race", PullRequestId = 1, CreatedAt = DateTimeOffset.UtcNow };
        var finding = Finding(pr.Id, "shared-thread");
        var copy = Finding(pr.Id, "shared-thread");
        this._db.AddRange(pr, finding, copy);
        await this._db.SaveChangesAsync();
        var barrier = new CommandBarrier(command => command.Contains("UPDATE code_insight_findings", StringComparison.Ordinal));
        await using var olderDb = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(barrier).Options);
        await using var newerDb = new MeisterProPRDbContext(this._options);
        var now = DateTimeOffset.UtcNow;
        var outcome = new CodeInsightDispositionRecord(
            CodeInsightDisposition.Acknowledged, ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unknown, null, null);
        var older = new CodeInsightPerformanceEvidenceStore(olderDb).RecordCurrentOutcomeAsync(finding.Id, "WontFix", outcome, now);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = new CodeInsightPerformanceEvidenceStore(newerDb).RecordCurrentOutcomeAsync(finding.Id, "ByDesign", outcome, now.AddSeconds(1));
        await Task.Delay(100);
        barrier.Release.TrySetResult();
        await Task.WhenAll(older, newer);
        this._db.ChangeTracker.Clear();
        var rows = await this._db.CodeInsightFindings.Where(row => row.CodeInsightPullRequestId == pr.Id).ToListAsync();
        Assert.All(
            rows, row =>
            {
                Assert.Equal("ByDesign", row.NativeStatus);
                Assert.Equal(now.AddSeconds(1).ToUnixTimeMilliseconds(), row.OutcomeObservedAt!.Value.ToUnixTimeMilliseconds());
            });
    }

    [Fact]
    public async Task RepeatedOutcomeWatermarkRejectsDelayedDifferentStatusAcrossPublicationCopies()
    {
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "outcome-watermark", PullRequestId = 1 };
        var finding = Finding(pr.Id, "shared-thread");
        var copy = Finding(pr.Id, "shared-thread");
        this._db.AddRange(pr, finding, copy);
        await this._db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(this._db);
        var now = DateTimeOffset.UtcNow;
        var outcome = new CodeInsightDispositionRecord(
            CodeInsightDisposition.Addressed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null);
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", outcome, now));
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", outcome, now.AddSeconds(2)));
        Assert.False(await store.RecordCurrentOutcomeAsync(copy.Id, "WontFix", outcome, now.AddSeconds(1)));
        this._db.ChangeTracker.Clear();
        var rows = await this._db.CodeInsightFindings.Where(row => row.CodeInsightPullRequestId == pr.Id).ToListAsync();
        Assert.All(
            rows, row =>
            {
                Assert.Equal("Fixed", row.NativeStatus);
                Assert.Equal(now.AddSeconds(2).ToUnixTimeMilliseconds(), row.OutcomeObservedAt!.Value.ToUnixTimeMilliseconds());
            });
    }

    [Fact]
    public async Task UnchangedOutcomeWatermarkDoesNotCreateProjectionWorkAndChecksFingerprintAtomically()
    {
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "unchanged-watermark", PullRequestId = 1 };
        var finding = Finding(pr.Id, "thread");
        this._db.AddRange(pr, finding);
        await this._db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(this._db);
        var now = DateTimeOffset.UtcNow;
        var outcome = new CodeInsightDispositionRecord(
            CodeInsightDisposition.Addressed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null);
        await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", outcome, now, sourceFingerprint: "source");
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(this._a, Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(this._db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await projector.ProjectPullRequestAsync(pr.Id);
        var cutoff = pr.PerformanceProjectedAt;
        var updated = finding.PerformanceEvidenceUpdatedAt;
        Assert.True(await store.ObserveUnchangedOutcomeAsync(finding.Id, "Fixed", "source", now.AddSeconds(2)));
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", outcome, now.AddSeconds(3), sourceFingerprint: "source"));
        Assert.False(await store.ObserveUnchangedOutcomeAsync(finding.Id, "Fixed", "stale-fingerprint", now.AddSeconds(4)));
        Assert.Equal(now.AddSeconds(3).ToUnixTimeMilliseconds(), finding.OutcomeObservedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(updated!.Value.ToUnixTimeMilliseconds(), finding.PerformanceEvidenceUpdatedAt!.Value.ToUnixTimeMilliseconds());
        Assert.False(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
        Assert.Equal(
            0, (await new ReviewerPerformanceRangeReader(this._db).QueryAsync(Request([this._a]).Query, [this._a])).Views[0].Evidence.PendingSourceAggregates);
        Assert.Equal(0, await projector.BackfillAsync(100));
        Assert.Equal(cutoff, pr.PerformanceProjectedAt);
        Assert.Equal("Fixed", finding.NativeStatus);
    }

    [Fact]
    public async Task ParallelFileExposureCallsRetainEveryDistinctFileAndDeduplicateConcurrentReplays()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MeisterProPRDbContext>(builder => builder.UseNpgsql(fixture.ConnectionString, options => options.UseVector()));
        services.AddScoped<ICodeInsightsCollectionGate, DatabaseGate>();
        services.AddSingleton<ILogger<CodeInsightReviewExposureCollector>>(NullLogger<CodeInsightReviewExposureCollector>.Instance);
        services.AddScoped<CodeInsightReviewExposureCollector>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var collector = scope.ServiceProvider.GetRequiredService<CodeInsightReviewExposureCollector>();
        var key = new CodeInsightPullRequestKey(this._a, "parallel-exposure", 1);
        var job = Guid.NewGuid();
        await Task.WhenAll(
            Enumerable.Range(0, 12).SelectMany(index => Enumerable.Range(0, 2).Select(_ => collector.RecordAsync(
                key, job, $"src/File{index}.cs", "revision", "review-model", null, "GitHub:https://github.example", "in-process", DateTimeOffset.UtcNow))));
        Assert.Equal(1, await this._db.CodeInsightPullRequests.CountAsync(row => row.ClientId == this._a && row.RepositoryId == key.RepositoryId));
        Assert.Equal(12, await this._db.CodeInsightReviewExposures.CountAsync(row => row.JobId == job));
    }

    [Fact]
    public async Task IndependentEmptyHarvestCoverageSurvivesCollidingProviderIdentities()
    {
        var gate = OpenGate();
        var projector = new ReviewerPerformanceCountProjector(this._db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var coverage = new CodeInsightHarvestCoverageRecorder(this._db, gate, projector, NullLogger<CodeInsightHarvestCoverageRecorder>.Instance);
        var key = new CodeInsightPullRequestKey(this._a, "coverage-race", 1);
        var now = DateTimeOffset.UtcNow;
        await coverage.RecordAsync(key, "AzureDevOps:https://dev.azure.com/orgA", true, now);
        await coverage.RecordAsync(key, "AzureDevOps:https://dev.azure.com/orgB", true, now);
        var rows = await this._db.ReviewerPerformanceDailyCounts.Where(row => row.ClientId == this._a && row.RepositoryId == key.RepositoryId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("collectionComplete", row.Outcome));
    }

    [Fact]
    public async Task ConcurrentCoverageKeepsNewerIncompleteEnumerationOverOlderSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var source = "GitHub:https://github.example";
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "coverage-order", PullRequestId = 1 };
        this._db.AddRange(
            pr,
            new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = source, ObservedAt = now, AllHumanThreadsResolved = true,
                EnumerationComplete = false,
            });
        await this._db.SaveChangesAsync();
        var barrier = new CommandBarrier(command =>
            command.Contains("UPDATE", StringComparison.Ordinal) && command.Contains("code_insight_pull_requests", StringComparison.Ordinal));
        await using var olderDb = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(barrier).Options);
        await using var newerDb = new MeisterProPRDbContext(this._options);
        var gate = OpenGate();
        var key = new CodeInsightPullRequestKey(this._a, pr.RepositoryId, 1);

        CodeInsightHarvestCoverageRecorder Recorder(MeisterProPRDbContext db)
        {
            return new CodeInsightHarvestCoverageRecorder(
                db, gate, new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance),
                NullLogger<CodeInsightHarvestCoverageRecorder>.Instance);
        }

        var older = Recorder(olderDb).RecordAsync(key, source, true, now.AddSeconds(1));
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = Recorder(newerDb).RecordAsync(key, source, true, now.AddSeconds(2), enumerationComplete: false);
        await Task.WhenAny(newer, Task.Delay(1000));
        barrier.Release.TrySetResult();
        await Task.WhenAll(older, newer);
        var retained = await this._db.CodeInsightHarvestCoverage.AsNoTracking().SingleAsync(row => row.CodeInsightPullRequestId == pr.Id);
        Assert.False(retained.EnumerationComplete);
        Assert.Equal(now.AddSeconds(2).ToUnixTimeMilliseconds(), retained.ObservedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ParallelFirstCoverageCallsRetainAllIndependentSourcesWithScopedDatabaseGates()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MeisterProPRDbContext>(builder => builder.UseNpgsql(fixture.ConnectionString, options => options.UseVector()));
        services.AddScoped<ICodeInsightsCollectionGate, DatabaseGate>();
        services.AddSingleton<ILogger<ReviewerPerformanceCountProjector>>(NullLogger<ReviewerPerformanceCountProjector>.Instance);
        services.AddSingleton<ILogger<CodeInsightHarvestCoverageRecorder>>(NullLogger<CodeInsightHarvestCoverageRecorder>.Instance);
        services.AddScoped<ReviewerPerformanceCountProjector>();
        services.AddScoped<CodeInsightHarvestCoverageRecorder>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<CodeInsightHarvestCoverageRecorder>();
        var key = new CodeInsightPullRequestKey(this._a, "parallel-first-coverage", 1);
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(
            Enumerable.Range(0, 8).SelectMany(index =>
                Enumerable.Range(0, 2).Select(_ => recorder.RecordAsync(key, $"AzureDevOps:https://dev.azure.com/org-{index}", true, now))));
        Assert.Equal(1, await this._db.CodeInsightPullRequests.CountAsync(row => row.ClientId == this._a && row.RepositoryId == key.RepositoryId));
        Assert.Equal(
            8,
            await this._db.CodeInsightHarvestCoverage.CountAsync(row =>
                row.CodeInsightPullRequestId == this._db.CodeInsightPullRequests.Where(pr => pr.ClientId == this._a && pr.RepositoryId == key.RepositoryId)
                    .Select(pr => pr.Id).Single()));
    }

    [Fact]
    public async Task EveryEvidenceMutationMarksItsAggregateTransactionallyWithoutProjectorSelfDirtying()
    {
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "dirty-source", PullRequestId = 1 };
        var anchor = Finding(pr.Id, "anchor");
        this._db.AddRange(pr, anchor);
        await this._db.SaveChangesAsync();

        async Task ClearAsync()
        {
            await this._db.CodeInsightPerformanceDirty.Where(row => row.CodeInsightPullRequestId == pr.Id).ExecuteDeleteAsync();
        }

        async Task CheckAsync()
        {
            Assert.True(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
            await ClearAsync();
        }

        async Task ExerciseAsync(object row, string property, object value)
        {
            this._db.Add(row);
            await this._db.SaveChangesAsync();
            await CheckAsync();
            this._db.Entry(row).Property(property).CurrentValue = value;
            await this._db.SaveChangesAsync();
            await CheckAsync();
            this._db.Remove(row);
            await this._db.SaveChangesAsync();
            await CheckAsync();
        }

        await ClearAsync();
        await ExerciseAsync(Finding(pr.Id, "receipt"), nameof(CodeInsightFinding.NativeStatus), "ByDesign");
        await ExerciseAsync(
            new CodeInsightFindingTag
            {
                Id = Guid.NewGuid(), CodeInsightFindingId = anchor.Id, IsCore = true, CoreSlug = "logic-error", ClassifierVersion = "type-v1",
                AssignedAt = DateTimeOffset.UtcNow,
            }, nameof(CodeInsightFindingTag.ClassifierVersion), "type-v2");
        await ExerciseAsync(
            new CodeInsightFindingDisposition
                { Id = Guid.NewGuid(), CodeInsightFindingId = anchor.Id, Disposition = CodeInsightDisposition.Addressed, DecidedAt = DateTimeOffset.UtcNow },
            nameof(CodeInsightFindingDisposition.ClassifierVersion), "disposition-v2");
        var miss = new CodeInsightMiss
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderThreadId = "human", ProviderScope = anchor.ProviderScope,
            HarvestedAt = DateTimeOffset.UtcNow,
        };
        miss.RecordJudgement(true, true, true, .9, "human-v1", true, "protected discussion", DateTimeOffset.UtcNow);
        await ExerciseAsync(miss, nameof(CodeInsightMiss.DimensionClassifierVersion), "type-v2");
        await ExerciseAsync(
            new CodeInsightReviewExposure
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), FilePath = "src/Service.cs", ModelId = "model",
                ProviderScope = anchor.ProviderScope, ObservedAt = DateTimeOffset.UtcNow,
            }, nameof(CodeInsightReviewExposure.RevisionKey), "new-revision");
        await ExerciseAsync(
            new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = anchor.ProviderScope, ObservedAt = DateTimeOffset.UtcNow,
                EnumerationComplete = true,
            }, nameof(CodeInsightHarvestCoverage.AllHumanThreadsResolved), true);
        pr.MissHarvestObservedAt = DateTimeOffset.UtcNow;
        pr.MissHarvestProviderScope = anchor.ProviderScope;
        await this._db.SaveChangesAsync();
        await CheckAsync();
        await using (var transaction = await this._db.Database.BeginTransactionAsync())
        {
            this._db.Add(Finding(pr.Id, "rollback"));
            await this._db.SaveChangesAsync();
            Assert.True(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
            await transaction.RollbackAsync();
        }

        this._db.ChangeTracker.Clear();
        Assert.False(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
        var projector = new ReviewerPerformanceCountProjector(this._db, OpenGate(), NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await projector.ProjectPullRequestAsync(pr.Id);
        await projector.ProjectPullRequestAsync(pr.Id);
        Assert.False(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
        Assert.Equal(
            1,
            await this._db.ReviewerPerformanceDailyCounts
                .Where(row => row.CodeInsightPullRequestId == pr.Id && row.PublicationState == CodeInsightPublicationState.Published)
                .SumAsync(row => row.Count));
    }

    [Fact]
    public async Task NarrowWindowReadsOnlyRelevantRepresentativeRepositoryLabels()
    {
        var now = DateTimeOffset.UtcNow;
        var relevant = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = "visible-repo", RepositoryName = "Friendly repository", PullRequestId = 1, UpdatedAt = now,
        };
        this._db.Add(relevant);
        this._db.AddRange(
            Enumerable.Range(2, 1200).Select(index => new CodeInsightPullRequest
            {
                Id = Guid.NewGuid(), ClientId = this._a, RepositoryId = index < 602 ? "visible-repo" : $"old-repo-{index}", RepositoryName = "Old name",
                PullRequestId = index, UpdatedAt = now.AddYears(-2),
            }));
        this._db.Add(
            new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), CellKey = "bounded-label", CodeInsightPullRequestId = relevant.Id, ClientId = this._a,
                RepositoryId = relevant.RepositoryId,
                ProviderScope = "GitHub:https://github.example", BucketDate = new DateOnly(2026, 9, 1),
                PublicationState = CodeInsightPublicationState.Published,
                Outcome = "positive", Count = 2, UpdatedAt = now,
            });
        await this._db.SaveChangesAsync();
        var guard = new MetadataReadGuard(1);
        await using var db = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(guard).Options);
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Request([this._a]).Query, [this._a]);
        Assert.Equal(1, guard.RepositoryRows);
        Assert.Contains("Friendly repository", Assert.Single(result.Views[0].Facets.Repositories).Label);
    }

    [Fact]
    public async Task ExpiryAndClientPurgeDeleteReportsWithoutReadingCopiedPayloads()
    {
        var store = new ReviewerPerformanceReportStore(this._db, new ReviewerPerformanceRangeReader(this._db));
        var expired = Request([this._a, this._b]);
        var remaining = Request([this._b]);
        await store.SaveAsync(expired, [this._a, this._b]);
        await store.SaveAsync(remaining, [this._a, this._b]);
        await this._db.ReviewerPerformanceReports.Where(row => row.Id == expired.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        var guard = new MetadataReadGuard(rejectReportPayload: true);
        await using var db = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(guard).Options);
        var purge = new ReviewerPerformanceReportStore(db, new ReviewerPerformanceRangeReader(db));
        Assert.Equal(1, await purge.PurgeExpiredAsync());
        Assert.True(await this._db.ReviewerPerformanceReports.AnyAsync(row => row.Id == remaining.Id));
        Assert.Equal(1, await purge.PurgeForClientAsync(this._b));
        Assert.False(await this._db.ReviewerPerformanceReportClients.AnyAsync(row => row.ReportId == remaining.Id));
    }

    [Fact]
    public async Task ConcurrentMissUpdatesAtomicallyRetainTheNewestSourceObservation()
    {
        var key = new CodeInsightPullRequestKey(this._a, "miss-order", 1);
        var now = DateTimeOffset.UtcNow;
        var input = new CodeInsightMissRecord(
            "thread", null, null, "Original discussion", true, true, true, .9, "human-v1", true, SourceFingerprint: "original", SourceObservedAt: now);
        var codec = Codec();
        Assert.True(await new CodeInsightFindingStore(this._db, codec).RecordMissAsync(key, input));
        var barrier = new CommandBarrier(command => command.Contains("UPDATE code_insight_misses", StringComparison.Ordinal));
        await using var olderDb = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>(this._options).AddInterceptors(barrier).Options);
        await using var newerDb = new MeisterProPRDbContext(this._options);
        var older = new CodeInsightFindingStore(olderDb, codec).RejudgeMissAsync(
            key,
            input with
            {
                Discussion = "Older open observation", WasActedOn = false, JudgedThreadResolved = false, SourceFingerprint = "older",
                SourceObservedAt = now.AddSeconds(1),
            });
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = new CodeInsightFindingStore(newerDb, codec).RejudgeMissAsync(
            key, input with { Discussion = "Newest settled observation", SourceFingerprint = "newest", SourceObservedAt = now.AddSeconds(2) });
        await Task.Delay(100);
        barrier.Release.TrySetResult();
        await Task.WhenAll(older, newer);
        this._db.ChangeTracker.Clear();
        var miss = await this._db.CodeInsightMisses.SingleAsync(row =>
            row.ProviderThreadId == "thread" && row.CodeInsightPullRequest!.RepositoryId == key.RepositoryId && row.CodeInsightPullRequest.ClientId == this._a);
        Assert.True(miss.WasActedOn);
        Assert.True(miss.JudgedThreadResolved);
        Assert.Equal("newest", miss.SourceFingerprint);
        Assert.Equal(now.AddSeconds(2).ToUnixTimeMilliseconds(), miss.SourceObservedAt!.Value.ToUnixTimeMilliseconds());
        Assert.False(
            await new CodeInsightFindingStore(this._db, codec).RejudgeMissAsync(
                key, input with { SourceFingerprint = "delayed", SourceObservedAt = now.AddSeconds(1) }));
    }

    [Fact]
    public async Task ConcurrentFirstHumanObservationsAcknowledgeOneRetainedSourceWithoutCollectionLoss()
    {
        var key = new CodeInsightPullRequestKey(this._a, "miss-first", 1);
        var evt = new ThreadUpdatedEvent(
            this._a, Guid.Empty, key.RepositoryId, 1, "thread", null, null, "Fixed", DateTimeOffset.UtcNow,
            [new ThreadUpdatedComment("1", "Human", false, DateTimeOffset.UtcNow, "The retry counter must be checked.")], DateTimeOffset.UtcNow);

        async Task<bool> HarvestAsync()
        {
            await using var db = new MeisterProPRDbContext(this._options);
            var store = new CodeInsightFindingStore(db, Codec());
            var human = Substitute.For<IHumanMissClassifier>();
            human.ClassifierVersion.Returns("human-v1");
            human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
                .Returns(new HumanMissJudgement(true, true, true, .9, "accepted"));
            return await new CodeInsightMissHarvester(
                    LocalScmPolicies.Registry.CompatibilityCodec,
                    store, store, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance)
                .HandleThreadObservedAsync(evt);
        }

        Assert.All(await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => HarvestAsync())), Assert.True);
        Assert.Equal(
            1,
            await this._db.CodeInsightMisses.CountAsync(row =>
                row.CodeInsightPullRequest!.ClientId == this._a && row.CodeInsightPullRequest.RepositoryId == key.RepositoryId));
    }

    [Fact]
    public async Task MissWatermarkAdvancementRejectsIntermediateChangesWithoutProjectionWork()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new CodeInsightPullRequestKey(this._a, "miss-watermark", 1);
        var input = new CodeInsightMissRecord(
            "thread", null, null, "Human concern", true, true, true, .9, "human-v1", true, SourceFingerprint: "source", SourceObservedAt: now);
        var store = new CodeInsightFindingStore(this._db, Codec());
        await store.RecordMissAsync(key, input);
        var pr = await this._db.CodeInsightPullRequests.SingleAsync(row => row.ClientId == this._a && row.RepositoryId == key.RepositoryId);
        var miss = await this._db.CodeInsightMisses.SingleAsync(row => row.CodeInsightPullRequestId == pr.Id);
        await this._db.Entry(miss).ReloadAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(this._a, Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(this._db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await projector.ProjectPullRequestAsync(pr.Id);
        var judgementAt = miss.LastJudgedAt;
        var acknowledged = await store.ObserveUnchangedMissAsync(key, "thread", "source", now.AddSeconds(2));
        Assert.True(acknowledged.Retained);
        Assert.False(acknowledged.Changed);
        Assert.False(
            await store.RejudgeMissAsync(key, input with { SourceFingerprint = "intermediate", WasActedOn = false, SourceObservedAt = now.AddSeconds(1) }));
        Assert.True(miss.WasActedOn);
        Assert.Equal(judgementAt, miss.LastJudgedAt);
        Assert.Equal(now.AddSeconds(2).ToUnixTimeMilliseconds(), miss.SourceObservedAt!.Value.ToUnixTimeMilliseconds());
        Assert.False(await this._db.CodeInsightPerformanceDirty.AnyAsync(row => row.CodeInsightPullRequestId == pr.Id));
        Assert.Equal(0, await projector.BackfillAsync(100));
        Assert.Equal(
            0, (await new ReviewerPerformanceRangeReader(this._db).QueryAsync(Request([this._a]).Query, [this._a])).Views[0].Evidence.PendingSourceAggregates);
    }

    [Fact]
    public async Task NewerAiObservationOrdersPendingFirstHumanClassificationInPostgres()
    {
        var key = new CodeInsightPullRequestKey(this._a, "first-human-ai", 1);
        var now = DateTimeOffset.UtcNow;
        var source = "AzureDevOps:https://ado.example.test/tfs/collection";
        var evt = new ThreadUpdatedEvent(
            this._a, Guid.Empty, key.RepositoryId, 1, "thread", null, null, "Fixed", now,
            [new ThreadUpdatedComment("1", "Human", false, now, "The retry counter must be checked.")], now, source);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HumanMissJudgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstDb = new MeisterProPRDbContext(this._options);
        var firstStore = new CodeInsightFindingStore(firstDb, Codec());
        var human = Substitute.For<IHumanMissClassifier>();
        human.ClassifierVersion.Returns("human-v1");
        human.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var first = new CodeInsightMissHarvester(
                LocalScmPolicies.Registry.CompatibilityCodec,
                firstStore, firstStore, human, OpenGate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance)
            .HandleThreadObservedAsync(evt);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        async Task<bool> ObserveAiAsync()
        {
            await using var db = new MeisterProPRDbContext(this._options);
            var store = new CodeInsightFindingStore(db, Codec());
            return await new CodeInsightMissHarvester(
                    LocalScmPolicies.Registry.CompatibilityCodec,
                    store, store, Substitute.For<IHumanMissClassifier>(), OpenGate(),
                    TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance)
                .HandleThreadObservedAsync(
                    evt with
                    {
                        ObservedAt = now.AddSeconds(1), Comments = [.. evt.Comments, new ThreadUpdatedComment("2", "Bot", true, now, "Reviewer reply")],
                    });
        }

        Assert.All(await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ObserveAiAsync())), Assert.True);
        Assert.False(
            await this._db.CodeInsightMisses.AnyAsync(row =>
                row.CodeInsightPullRequest!.ClientId == this._a && row.CodeInsightPullRequest.RepositoryId == key.RepositoryId));
        release.TrySetResult(new HumanMissJudgement(true, true, true, .9, "accepted"));
        Assert.True(await first);
        Assert.False(
            await this._db.CodeInsightMisses.AnyAsync(row =>
                row.CodeInsightPullRequest!.ClientId == this._a && row.CodeInsightPullRequest.RepositoryId == key.RepositoryId));
        await new ReviewerPerformanceCountProjector(this._db, OpenGate(), NullLogger<ReviewerPerformanceCountProjector>.Instance).ProjectAsync(key);
        Assert.False(
            await this._db.ReviewerPerformanceDailyCounts.AnyAsync(row => row.ClientId == this._a && row.RepositoryId == key.RepositoryId && row.IsMiss));
        var retained = await this._db.CodeInsightThreadEligibility.AsNoTracking()
            .SingleAsync(row => row.CodeInsightPullRequest!.ClientId == this._a && row.ProviderScope == source);
        Assert.True(retained.ExcludedFromHumanMisses);
        Assert.Equal(now.AddSeconds(1).ToUnixTimeMilliseconds(), retained.SourceObservedAt.ToUnixTimeMilliseconds());
        var other = "AzureDevOps:https://ado.example.test/tfs/othercollection";
        await using var otherDb = new MeisterProPRDbContext(this._options);
        var otherStore = new CodeInsightFindingStore(otherDb, Codec());
        Assert.True(
            await new CodeInsightMissHarvester(
                LocalScmPolicies.Registry.CompatibilityCodec,
                otherStore, otherStore, human, OpenGate(), TestPostedCommentComposer.Default,
                NullLogger<CodeInsightMissHarvester>.Instance).HandleThreadObservedAsync(evt with { ProviderScope = other }));
        Assert.True(
            (await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(row => row.CodeInsightPullRequestId == retained.CodeInsightPullRequestId))
            .CountsAsMiss);
        Assert.Equal(2, await this._db.CodeInsightThreadEligibility.CountAsync(row => row.CodeInsightPullRequestId == retained.CodeInsightPullRequestId));
        Assert.Equal(1, await new CodeInsightFindingStore(this._db, Codec()).PurgeForClientAsync(this._a));
        Assert.False(await this._db.CodeInsightThreadEligibility.AnyAsync(row => row.CodeInsightPullRequestId == retained.CodeInsightPullRequestId));
    }

    private static ISecretProtectionCodec Codec()
    {
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => (string)call[0]);
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => (string)call[0]);
        return codec;
    }

    private static ICodeInsightsCollectionGate OpenGate()
    {
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return gate;
    }

    private static ReviewerPerformanceSaveReportRequest Request(Guid[] clients)
    {
        return new ReviewerPerformanceSaveReportRequest(
            Guid.NewGuid(), "Recorded sample",
            new ReviewerPerformanceQuery
                { Views = [new ReviewerPerformanceViewQuery { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 9, 1), ClientIds = clients }] });
    }

    private static CodeInsightFinding Finding(Guid pr, string thread)
    {
        return new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr, JobId = Guid.NewGuid(), RevisionKey = Guid.NewGuid().ToString(),
            ObservedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), ProviderThreadId = thread, ProviderCommentId = "1",
            PublicationState = CodeInsightPublicationState.Published, ProviderScope = "AzureDevOps:https://dev.azure.com/test",
        };
    }

    private sealed class DatabaseGate(MeisterProPRDbContext db) : ICodeInsightsCollectionGate
    {
        public async Task<bool> IsCollectionEnabledAsync(Guid clientId, CancellationToken ct = default)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.02)", ct);
            return await db.Clients.AnyAsync(row => row.Id == clientId, ct);
        }
    }

    private sealed class CommandBarrier(Func<string, bool> matches, bool afterRead = false) : DbCommandInterceptor
    {
        private int _used;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task WaitAsync(DbCommand command, CancellationToken ct)
        {
            if (matches(command.CommandText) && Interlocked.Exchange(ref this._used, 1) == 0)
            {
                this.Started.TrySetResult();
                await this.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!afterRead)
            {
                await this.WaitAsync(command, cancellationToken);
            }

            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (afterRead)
            {
                await this.WaitAsync(command, cancellationToken);
            }

            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await this.WaitAsync(command, cancellationToken);
            return result;
        }
    }

    private sealed class MetadataReadGuard(int? maximumRepositoryRows = null, bool rejectReportPayload = false) : DbCommandInterceptor
    {
        public int RepositoryRows { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            var names = Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray();
            if (rejectReportPayload && names.Contains("Payload", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Deletion must not read copied report payloads.");
            }

            if (maximumRepositoryRows is null || result.FieldCount != 3 || !names.Contains("RepositoryName", StringComparer.OrdinalIgnoreCase))
            {
                return result;
            }

            var table = new DataTable();
            for (var index = 0; index < result.FieldCount; index++)
            {
                table.Columns.Add(result.GetName(index), result.GetFieldType(index));
            }

            while (await result.ReadAsync(cancellationToken))
            {
                this.RepositoryRows++;
                if (this.RepositoryRows > maximumRepositoryRows)
                {
                    throw new InvalidOperationException("Repository label reads exceeded the retained identity population.");
                }

                var values = new object[result.FieldCount];
                result.GetValues(values);
                table.Rows.Add(values);
            }

            await result.DisposeAsync();
            return table.CreateDataReader();
        }
    }
}
