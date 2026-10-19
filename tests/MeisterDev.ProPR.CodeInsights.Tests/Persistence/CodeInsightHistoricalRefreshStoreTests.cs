// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class CodeInsightHistoricalRefreshStoreTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string Scope = "AzureDevOps:https://ado.example.test/collection";
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<Guid> _jobIds = [];
    private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private MeisterProPRDbContext _db = null!;
    private ICodeInsightsCollectionGate _gate = null!;
    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        Assert.True(fixture.IsAvailable, "Historical refresh verification requires PostgreSQL.");
        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        this._db = new MeisterProPRDbContext(this._options);
        this._db.AddRange(
            new TenantRecord { Id = this._tenant, Slug = this._tenant.ToString("N"), DisplayName = "Historical refresh", CreatedAt = this._now },
            new ClientRecord { Id = this._client, TenantId = this._tenant, DisplayName = "Historical refresh", CreatedAt = this._now });
        await this._db.SaveChangesAsync();
        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(true);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await this._db.ReviewJobs.Where(j => j.ClientId == this._client || this._jobIds.Contains(j.Id)).ExecuteDeleteAsync();
            await this._db.Clients.Where(c => c.Id == this._client).ExecuteDeleteAsync();
            await this._db.Tenants.Where(t => t.Id == this._tenant).ExecuteDeleteAsync();
        }
        finally
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequeueRacingAClaimCannotInvalidateTheNewActiveAttempt()
    {
        var pr = this.Seed(3002);
        await this._db.SaveChangesAsync();
        await using var claimDb = new MeisterProPRDbContext(this._options);
        await using var retryDb = new MeisterProPRDbContext(this._options);
        var claim = this.Store(claimDb).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15));
        var retry = this.Store(retryDb).RequeueAsync(pr.Id, [this._client], this._now);
        await Task.WhenAll(claim, retry);
        var claims = await claim;
        if (claims.Count == 0)
        {
            Assert.Equal(CodeInsightHistoricalRequeueResult.Queued, await retry);
            claims = await this.Store(this._db).ClaimAsync(1, this._now.AddSeconds(1), TimeSpan.FromMinutes(15));
        }

        var active = Assert.Single(claims);
        Assert.Contains(await retry, new[] { CodeInsightHistoricalRequeueResult.Queued, CodeInsightHistoricalRequeueResult.InProgress });
        await this._db.Entry(pr).ReloadAsync();
        Assert.Equal(active.AttemptId, pr.LastHistoricalRefreshAttemptId);
        Assert.Equal(
            CodeInsightHistoricalRequeueResult.InProgress,
            await this.Store(this._db).RequeueAsync(pr.Id, [this._client], this._now.AddSeconds(2)));
    }

    [Theory]
    [InlineData("SourceCoordinatesUnavailable", false)]
    [InlineData("ProviderConnectionUnavailable", false)]
    [InlineData("ProviderIdentityUnavailable", false)]
    [InlineData("HistoricalCapabilityUnsupported", false)]
    [InlineData("ObserverUnavailable", false)]
    [InlineData("OwnershipUnavailable", true)]
    [InlineData("ProviderReadFailed", true)]
    [InlineData("ProcessingDeadlineExceeded", true)]
    [InlineData("FutureFailure", true)]
    public async Task StoredFailureReasonsUseTheSharedAutomaticRetryPolicy(string reason, bool retryable)
    {
        var pr = this.Seed(3003);
        pr.LastHistoricalRefreshReason = reason;
        await this._db.SaveChangesAsync();
        var claimed = await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15));
        Assert.Equal(retryable ? 1 : 0, claimed.Count);
    }

    [Fact]
    public async Task RequeuePreservesEvidenceAndFencesThePreviousAttempt()
    {
        var pr = this.Seed(3000);
        pr.LastHistoricalRefreshCompletedAt = this._now.AddDays(-10);
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.True(
            await this.Store(this._db).RecordResultAsync(
                pr.Id, claim.AttemptId,
                new(CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable),
                this._now.AddSeconds(1), TimeSpan.FromHours(6)));
        var before = await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id);
        var findings = await this._db.CodeInsightFindings.CountAsync(f => f.CodeInsightPullRequestId == pr.Id);
        var dirty = await this._db.CodeInsightPerformanceDirty.CountAsync(d => d.CodeInsightPullRequestId == pr.Id);
        Assert.Equal(
            CodeInsightHistoricalRequeueResult.NotFound,
            await this.Store(this._db).RequeueAsync(pr.Id, [Guid.NewGuid()], this._now.AddSeconds(2)));
        Assert.Equal(
            CodeInsightHistoricalRequeueResult.Queued,
            await this.Store(this._db).RequeueAsync(pr.Id, [this._client], this._now.AddSeconds(2)));
        var after = await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id);
        Assert.Null(after.LastHistoricalRefreshAttemptId);
        Assert.Null(after.LastHistoricalRefreshReason);
        Assert.Equal(before.LastHistoricalRefreshAttemptAt, after.LastHistoricalRefreshAttemptAt);
        Assert.Equal(before.LastHistoricalRefreshCompletedAt, after.LastHistoricalRefreshCompletedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.LastActivityAt, after.LastActivityAt);
        Assert.Equal(findings, await this._db.CodeInsightFindings.CountAsync(f => f.CodeInsightPullRequestId == pr.Id));
        Assert.Equal(dirty, await this._db.CodeInsightPerformanceDirty.CountAsync(d => d.CodeInsightPullRequestId == pr.Id));
        Assert.False(
            await this.Store(this._db).RecordResultAsync(
                pr.Id, claim.AttemptId,
                new(CodeInsightObservationStatus.Observed, this._now), this._now.AddSeconds(1), TimeSpan.FromHours(6)));
        Assert.Equal(pr.Id, Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now.AddSeconds(3), TimeSpan.FromMinutes(15))).AggregateId);
    }

    [Fact]
    public async Task RequeueRejectsActiveClaimsAndDisabledClients()
    {
        var pr = this.Seed(3001);
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(
            CodeInsightHistoricalRequeueResult.InProgress,
            await this.Store(this._db).RequeueAsync(pr.Id, [this._client], this._now.AddSeconds(1)));
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(false);
        Assert.Equal(
            CodeInsightHistoricalRequeueResult.CollectionDisabled,
            await this.Store(this._db).RequeueAsync(pr.Id, [this._client], this._now.AddHours(1)));
        await this._db.Entry(pr).ReloadAsync();
        Assert.Equal(claim.AttemptId, pr.LastHistoricalRefreshAttemptId);
    }

    [Theory]
    [InlineData("SourceCoordinatesUnavailable")]
    [InlineData("ProviderConnectionUnavailable")]
    public async Task RepairRequiredSourcesDoNotConsumeTheBoundedClaimScan(string reason)
    {
        foreach (var number in Enumerable.Range(1000, 251))
        {
            var blocked = this.Seed(number);
            blocked.LastHistoricalRefreshReason = reason;
        }

        var retryable = this.Seed(2000);
        retryable.LastHistoricalRefreshAttemptAt = this._now.AddDays(-1);
        retryable.LastHistoricalRefreshReason = "ProviderReadFailed";
        await this._db.SaveChangesAsync();
        var claimed = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(retryable.Id, claimed.AggregateId);
        Assert.True(
            await this.Store(this._db).RecordResultAsync(
                claimed.AggregateId, claimed.AttemptId,
                new(CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed),
                this._now.AddSeconds(1), TimeSpan.FromHours(6)));
        Assert.Empty(await this.Store(this._db).ClaimAsync(250, this._now.AddHours(2), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task BoundedClaimsIncludeSealedHistoryAndPersistBeforeExternalWork()
    {
        var rows = Enumerable.Range(1, 4).Select(i => this.Seed(i)).ToList();
        this._db.Add(
            new CodeInsightPullRequestMetric
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = rows[0].Id,
                ClientId = this._client, RepositoryId = rows[0].RepositoryId, PullRequestId = rows[0].PullRequestId,
                CloseState = "Completed", SealedAt = this._now.AddDays(-20), SealedOn = DateOnly.FromDateTime(this._now.AddDays(-20).UtcDateTime),
            });
        await this._db.SaveChangesAsync();
        var originalAge = rows[0].LastActivityAt;
        var claims = await this.Store(this._db).ClaimAsync(2, this._now, TimeSpan.FromMinutes(15));
        Assert.Equal(2, claims.Count);
        Assert.All(claims, c => Assert.Equal(CodeInsightHistoricalGap.NativeOutcome | CodeInsightHistoricalGap.HarvestCoverage, c.Gaps));
        await using var restarted = new MeisterProPRDbContext(this._options);
        foreach (var claim in claims)
        {
            var retained = await restarted.CodeInsightPullRequests.SingleAsync(p => p.Id == claim.AggregateId);
            Assert.Equal(claim.AttemptId, retained.LastHistoricalRefreshAttemptId);
            Assert.Equal(this._now.ToUnixTimeMilliseconds(), retained.LastHistoricalRefreshAttemptAt!.Value.ToUnixTimeMilliseconds());
            Assert.Equal(this._now.AddMinutes(15).ToUnixTimeMilliseconds(), retained.HistoricalRefreshNotBefore!.Value.ToUnixTimeMilliseconds());
            Assert.Equal(originalAge.ToUnixTimeMilliseconds(), retained.LastActivityAt.ToUnixTimeMilliseconds());
        }
    }

    [Fact]
    public async Task DisabledClientsDoNotConsumeTheClaimCap()
    {
        this.Seed(1);
        await this._db.SaveChangesAsync();
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(false);
        Assert.Empty(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(true);
        Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrCrashedAttemptRemainsRetryableAfterPartialWritesFillTheOriginalGaps(bool recordFailure)
    {
        var pr = this.Seed(71);
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        var finding = await this._db.CodeInsightFindings.SingleAsync(f => f.CodeInsightPullRequestId == pr.Id);
        finding.NativeStatus = "Active";
        this._db.Add(
            new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id,
                ProviderScope = Scope, EnumerationComplete = true, ObservedAt = this._now,
            });
        await this._db.SaveChangesAsync();
        if (recordFailure)
        {
            Assert.True(
                await this.Store(this._db).RecordResultAsync(
                    pr.Id, claim.AttemptId,
                    new CodeInsightObservationResult(
                        CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed),
                    this._now.AddMinutes(1), TimeSpan.FromHours(6)));
        }

        Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now.AddHours(7), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task FindinglessRetainedMissAndExposureStillNeedCoverage()
    {
        var missPr = this.Seed(1, false);
        var miss = new CodeInsightMiss
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = missPr.Id, ProviderScope = Scope, ProviderThreadId = "human", HarvestedAt = this._now.AddDays(-30),
        };
        miss.RecordJudgement(true, true, true, .9, "old-human", true, "Human: Same concern", this._now.AddDays(-30));
        this._db.Add(miss);
        var exposurePr = this.Seed(2, false);
        this._db.Add(
            new CodeInsightReviewExposure
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = exposurePr.Id, JobId = Guid.NewGuid(), ProviderScope = Scope,
                ObservedAt = this._now.AddDays(-30),
            });
        this.Seed(3, false);
        await this._db.SaveChangesAsync();
        var claims = await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15));
        Assert.Equal(2, claims.Count);
        Assert.Contains(claims, c => c.AggregateId == missPr.Id && c.Gaps.HasFlag(CodeInsightHistoricalGap.MissFingerprint));
        Assert.Contains(claims, c => c.AggregateId == exposurePr.Id && c.Gaps == CodeInsightHistoricalGap.HarvestCoverage);
        Assert.All(claims.SelectMany(c => c.Sources), source => Assert.Equal(Scope, source.ProviderScope));
        Assert.All(claims.SelectMany(c => c.Sources), source => Assert.Null(source.RepresentativeJobId));
    }

    [Theory]
    [InlineData(CodeInsightPublicationState.PolicyWithheld)]
    [InlineData(CodeInsightPublicationState.Shadow)]
    [InlineData(CodeInsightPublicationState.PostingDisabled)]
    [InlineData(CodeInsightPublicationState.SuppressedRepeat)]
    [InlineData(CodeInsightPublicationState.Failed)]
    public async Task IntentionalNonpublicationDoesNotCreateANativeOutcomeGap(CodeInsightPublicationState publication)
    {
        var pr = this.Seed(1, publication: publication, coverage: true);
        await this._db.SaveChangesAsync();
        Assert.Empty(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        var coverage = await this._db.CodeInsightHarvestCoverage.SingleAsync(c => c.CodeInsightPullRequestId == pr.Id);
        coverage.EnumerationComplete = false;
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(CodeInsightHistoricalGap.HarvestCoverage, claim.Gaps);
    }

    [Fact]
    public async Task CompleteAndNonterminalEvidenceIsNotSelectedOrModified()
    {
        var complete = this.Seed(1, coverage: true);
        var active = this.Seed(2, coverage: true);
        var findings = this._db.ChangeTracker.Entries<CodeInsightFinding>().Select(e => e.Entity).ToList();
        var terminal = findings.Single(f => f.CodeInsightPullRequestId == complete.Id);
        terminal.NativeStatus = "Fixed";
        terminal.CurrentDisposition = CodeInsightDisposition.Addressed;
        terminal.OutcomeSourceFingerprint = "complete";
        terminal.OutcomeObservedAt = this._now.AddDays(-1);
        findings.Single(f => f.CodeInsightPullRequestId == active.Id).NativeStatus = "Active";
        await this._db.SaveChangesAsync();
        Assert.Empty(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        Assert.Null(complete.LastHistoricalRefreshAttemptAt);
        Assert.Null(active.LastHistoricalRefreshAttemptAt);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Pending")]
    [InlineData(" active ")]
    public async Task ReopenedFindingWithAnInitialDispositionNeedsOnlyKnownNativeStatus(string nativeStatus)
    {
        var pr = this.Seed(1, coverage: true);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        finding.NativeStatus = nativeStatus;
        var initial = new CodeInsightFindingDisposition
        {
            Id = Guid.NewGuid(), CodeInsightFindingId = finding.Id,
            Disposition = CodeInsightDisposition.Addressed, SourceIntent = ThreadResolutionIntent.ClaimsFix,
            SourceCodeChange = ThreadAnchorCodeChange.Changed, DecidedAt = this._now.AddDays(-30),
        };
        this._db.Add(initial);
        await this._db.SaveChangesAsync();

        Assert.Empty(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        await this._db.Entry(pr).ReloadAsync();
        Assert.Null(pr.LastHistoricalRefreshAttemptId);
        Assert.Null(pr.LastHistoricalRefreshAttemptAt);
        Assert.Null(pr.HistoricalRefreshNotBefore);
        Assert.Null(finding.OutcomeSourceFingerprint);
        Assert.Null(finding.OutcomeObservedAt);

        var coverage = await this._db.CodeInsightHarvestCoverage.SingleAsync(c => c.CodeInsightPullRequestId == pr.Id);
        coverage.EnumerationComplete = false;
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(CodeInsightHistoricalGap.HarvestCoverage, claim.Gaps);
        Assert.Equal(CodeInsightHistoricalGap.HarvestCoverage, Assert.Single(claim.Sources).Gaps);
        Assert.Equal(CodeInsightDisposition.Addressed, (await this._db.CodeInsightFindingDispositions.SingleAsync(d => d.Id == initial.Id)).Disposition);
    }

    [Fact]
    public async Task ExplicitPublishedEvidenceWithoutReceiptRemainsVisibleForIdentityRecovery()
    {
        var pr = this.Seed(1, coverage: true);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        finding.ProviderThreadId = null;
        finding.ProviderCommentId = null;
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(pr.Id, claim.AggregateId);
        Assert.Equal(CodeInsightHistoricalGap.NativeOutcome, claim.Gaps);
    }

    [Fact]
    public async Task ConcurrentClaimsAreDisjointAndRestartHonorsLeases()
    {
        for (var i = 1; i <= 3; i++)
        {
            this.Seed(i);
        }

        await this._db.SaveChangesAsync();

        async Task<IReadOnlyList<CodeInsightHistoricalRefreshCandidate>> Claim()
        {
            await using var db = new MeisterProPRDbContext(this._options);
            return await this.Store(db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15));
        }

        var claims = (await Task.WhenAll(Claim(), Claim(), Claim())).SelectMany(c => c).ToList();
        Assert.Equal(3, claims.Count);
        Assert.Equal(3, claims.Select(c => c.AggregateId).Distinct().Count());
        Assert.Empty(await Claim());
    }

    [Fact]
    public async Task ExpiredAttemptIsFencedAndUnavailableWorkRotatesDuringCooldown()
    {
        var first = this.Seed(1);
        this.Seed(2);
        await this._db.SaveChangesAsync();
        var old = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(1)));
        Assert.True(
            await this.Store(this._db).RecordResultAsync(
                old.AggregateId, old.AttemptId,
                new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed),
                this._now.AddSeconds(1), TimeSpan.FromHours(6)));
        var rotated = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now.AddSeconds(2), TimeSpan.FromMinutes(1)));
        Assert.NotEqual(old.AggregateId, rotated.AggregateId);
        var next = await this.Store(this._db).ClaimAsync(2, this._now.AddHours(7), TimeSpan.FromMinutes(1));
        var current = Assert.Single(next.Where(c => c.AggregateId == first.Id));
        Assert.NotEqual(old.AttemptId, current.AttemptId);
        Assert.False(
            await this.Store(this._db).RecordResultAsync(
                old.AggregateId, old.AttemptId,
                new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, this._now.AddHours(7)), this._now.AddHours(7), TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task AttemptMetadataDoesNotDirtyScoringOrExtendRetention()
    {
        var pr = this.Seed(1);
        await this._db.SaveChangesAsync();
        await this._db.CodeInsightPerformanceDirty.Where(d => d.CodeInsightPullRequestId == pr.Id).ExecuteDeleteAsync();
        var age = pr.LastActivityAt;
        var updated = pr.UpdatedAt;
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.True(
            await this.Store(this._db).RecordResultAsync(
                pr.Id, claim.AttemptId,
                new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable),
                this._now.AddSeconds(1), TimeSpan.FromHours(6)));
        await this._db.Entry(pr).ReloadAsync();
        Assert.Equal(age, pr.LastActivityAt);
        Assert.Equal(updated, pr.UpdatedAt);
        Assert.Empty(await this._db.CodeInsightPerformanceDirty.Where(d => d.CodeInsightPullRequestId == pr.Id).ToListAsync());
        Assert.Equal("ProviderConnectionUnavailable", pr.LastHistoricalRefreshReason);
        Assert.Null(pr.LastHistoricalRefreshCompletedAt);
    }

    [Fact]
    public async Task CompletionAtTheLeaseExpiryBoundaryIsRejected()
    {
        var pr = this.Seed(1);
        await this._db.SaveChangesAsync();
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(1)));

        Assert.False(
            await this.Store(this._db).RecordResultAsync(
                pr.Id, claim.AttemptId,
                new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, claim.LeaseUntil), claim.LeaseUntil, TimeSpan.FromHours(6)));

        await this._db.Entry(pr).ReloadAsync();
        Assert.Null(pr.LastHistoricalRefreshCompletedAt);
    }

    [Fact]
    public async Task WhitespaceOnlyRetainedEvidenceIsClaimedForEveryManagedWhitespaceCharacter()
    {
        var expected = new Dictionary<Guid, CodeInsightHistoricalGap>();
        var number = 1;
        foreach (var whitespace in ManagedWhitespaceCharacters())
        {
            var missPr = this.Seed(number++, finding: false, coverage: true);
            var miss = this.LegacyMiss(missPr, "human");
            miss.ProviderScope = Scope;
            miss.SourceFingerprint = whitespace;
            this._db.Add(miss);
            expected.Add(missPr.Id, CodeInsightHistoricalGap.MissFingerprint);

            var outcomePr = this.Seed(number++, coverage: true);
            var outcome = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == outcomePr.Id).Entity;
            outcome.NativeStatus = "Closed";
            outcome.CurrentDisposition = CodeInsightDisposition.Addressed;
            outcome.OutcomeObservedAt = this._now.AddDays(-1);
            outcome.OutcomeSourceFingerprint = whitespace;
            expected.Add(outcomePr.Id, CodeInsightHistoricalGap.NativeOutcome);

            var receiptPr = this.Seed(number++, coverage: true);
            var receipt = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == receiptPr.Id).Entity;
            receipt.NativeStatus = "Active";
            receipt.ProviderThreadId = whitespace;
            expected.Add(receiptPr.Id, CodeInsightHistoricalGap.NativeOutcome);

            var scopePr = this.Seed(number++, finding: false);
            this._db.Add(
                new CodeInsightHarvestCoverage
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = scopePr.Id, ProviderScope = whitespace,
                    EnumerationComplete = true, ObservedAt = this._now.AddDays(-1)
                });
            expected.Add(scopePr.Id, CodeInsightHistoricalGap.HarvestCoverage);

            var statusPr = this.Seed(number++, coverage: true);
            var status = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == statusPr.Id).Entity;
            status.NativeStatus = whitespace;
            expected.Add(statusPr.Id, CodeInsightHistoricalGap.NativeOutcome);
        }

        var retention = this._db.ChangeTracker.Entries<CodeInsightPullRequest>()
            .ToDictionary(e => e.Entity.Id, e => (e.Entity.CreatedAt, e.Entity.UpdatedAt, e.Entity.LastActivityAt));
        await this._db.SaveChangesAsync();

        var claims = await this.Store(this._db).ClaimAsync(CodeInsightHistoricalRefreshStore.MaximumPullRequests, this._now, TimeSpan.FromMinutes(15));

        Assert.Equal(expected.Count, claims.Count);
        foreach (var claim in claims)
        {
            Assert.Equal(expected[claim.AggregateId], claim.Gaps);
            var pr = await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == claim.AggregateId);
            Assert.Equal(retention[pr.Id], (pr.CreatedAt, pr.UpdatedAt, pr.LastActivityAt));
        }
    }

    [Fact]
    public async Task CompleteWhitespaceNormalizedStatusesAndAbsentReceiptsDoNotExhaustTheClaimScan()
    {
        var whitespace = ManagedWhitespaceCharacters();
        string[] statuses = ["Active", "Pending", "Fixed", "Closed", "WontFix", "ByDesign"];
        for (var number = 1; number <= CodeInsightHistoricalRefreshStore.MaximumPullRequests; number++)
        {
            var pr = this.Seed(number, coverage: true);
            var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == pr.Id).Entity;
            var padding = whitespace[(number - 1) % whitespace.Count];
            if (number % 10 == 0)
            {
                finding.PublicationState = CodeInsightPublicationState.Unknown;
                finding.ProviderThreadId = padding;
                finding.ProviderCommentId = padding;
            }
            else
            {
                finding.NativeStatus = $"\t{padding}{statuses[(number - 1) % statuses.Length]}{padding}\r\n";
                finding.CurrentDisposition = CodeInsightDisposition.Addressed;
                finding.OutcomeSourceFingerprint = "retained";
                finding.OutcomeObservedAt = this._now.AddDays(-1);
            }
        }

        var incomplete = this.Seed(CodeInsightHistoricalRefreshStore.MaximumPullRequests + 1, coverage: true);
        // A completed older attempt sorts after untouched complete evidence.
        incomplete.LastHistoricalRefreshAttemptAt = this._now.AddDays(-2);
        incomplete.LastHistoricalRefreshCompletedAt = this._now.AddDays(-1);
        await this._db.SaveChangesAsync();

        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));

        Assert.Equal(incomplete.Id, claim.AggregateId);
        Assert.Equal(CodeInsightHistoricalGap.NativeOutcome, claim.Gaps);
        Assert.Equal(1, await this._db.CodeInsightPullRequests.CountAsync(p => p.ClientId == this._client && p.HistoricalRefreshNotBefore != null));
    }

    [Theory]
    [InlineData("\u200b")]
    [InlineData("\ufeff")]
    public async Task NonWhitespaceEvidenceAndExactCoveredNamespacesRemainComplete(string value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        var pr = this.Seed(1, coverage: true);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        finding.NativeStatus = "Closed";
        finding.CurrentDisposition = CodeInsightDisposition.Addressed;
        finding.OutcomeObservedAt = this._now.AddDays(-1);
        finding.OutcomeSourceFingerprint = value;
        finding.ProviderThreadId = value;
        var miss = this.LegacyMiss(pr, "human");
        miss.ProviderScope = Scope;
        miss.SourceFingerprint = value;
        this._db.Add(miss);
        var exactScope = $"\t{Scope}\n";
        var coveredPr = this.Seed(2, finding: false);
        this._db.Add(
            new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = coveredPr.Id, ProviderScope = exactScope,
                EnumerationComplete = true, ObservedAt = this._now.AddDays(-1)
            });
        await this._db.SaveChangesAsync();

        Assert.Empty(await this.Store(this._db).ClaimAsync(10, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(exactScope, (await this._db.CodeInsightHarvestCoverage.SingleAsync(c => c.CodeInsightPullRequestId == coveredPr.Id)).ProviderScope);
        Assert.Equal(value, finding.ProviderThreadId);
        Assert.Equal(value, finding.OutcomeSourceFingerprint);
        Assert.Equal(value, miss.SourceFingerprint);
    }

    private static IReadOnlyList<string> ManagedWhitespaceCharacters()
    {
        return Enumerable.Range(char.MinValue, char.MaxValue + 1).Select(value => (char)value).Where(char.IsWhiteSpace).Select(value => value.ToString())
            .ToList();
    }

    private CodeInsightHistoricalRefreshStore Store(MeisterProPRDbContext db)
    {
        return new CodeInsightHistoricalRefreshStore(db, this._gate, providerRegistry: LocalScmPolicies.Registry);
    }

    private CodeInsightPullRequest Seed(
        int number, bool finding = true, CodeInsightPublicationState publication = CodeInsightPublicationState.Published, bool coverage = false)
    {
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = this._client, RepositoryId = "history", PullRequestId = number,
            PullRequestState = "Completed", CreatedAt = this._now.AddDays(-60), LastActivityAt = this._now.AddDays(-30), UpdatedAt = this._now.AddDays(-30),
        };
        this._db.Add(pr);
        if (finding)
        {
            this._db.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), RevisionKey = "old", Ordinal = 0,
                    ProviderScope = Scope, ProviderThreadId = "own", ProviderCommentId = "1", PublicationState = publication, EncryptedMessage = "Concern",
                    ObservedAt = this._now.AddDays(-30), CreatedAt = this._now.AddDays(-30),
                });
        }

        if (coverage)
        {
            this._db.Add(
                new CodeInsightHarvestCoverage
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = Scope,
                    ObservedAt = this._now.AddDays(-1), AllHumanThreadsResolved = true, EnumerationComplete = true,
                });
        }

        return pr;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("unknown-status")]
    public async Task TerminalOrUnknownNativeStateNeedsUsableCurrentJudgement(string state)
    {
        this.Seed(1, coverage: true);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        finding.NativeStatus = state == "unknown-status" ? "unsupported" : "Closed";
        finding.CurrentDisposition = state == "missing" ? null : CodeInsightDisposition.Dismissed;
        finding.CurrentClassifierVersion = state == "missing" ? null : "old-judge";
        finding.CurrentClassifierConfidence = state == "unknown-status" ? .9 : null;
        finding.OutcomeJudgementAttempts = 3;
        finding.OutcomeSourceFingerprint = "retained";
        finding.OutcomeObservedAt = this._now.AddDays(-1);
        await this._db.SaveChangesAsync();

        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        Assert.Equal(CodeInsightHistoricalGap.NativeOutcome, claim.Gaps);
    }

    [Fact]
    public async Task MissOnlySourceResolvesARetainedJobInItsCapturedNamespace()
    {
        var pr = this.Seed(1, false);
        this._db.Add(
            new CodeInsightMiss
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = Scope, ProviderThreadId = "human", HarvestedAt = this._now.AddDays(-30),
            });
        var matching = new ReviewJob(Guid.NewGuid(), this._client, "https://ado.example.test/collection", "project", pr.RepositoryId, 1, 1);
        var foreign = new ReviewJob(Guid.NewGuid(), this._client, "https://ado.example.test/other", "project", pr.RepositoryId, 1, 1);
        this._db.AddRange(matching, foreign);
        await this._db.SaveChangesAsync();

        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        var source = Assert.Single(claim.Sources);
        Assert.Equal(Scope, source.ProviderScope);
        Assert.Equal(matching.Id, source.RepresentativeJobId);
    }

    [Theory]
    [InlineData("finding")]
    [InlineData("exposure")]
    [InlineData("miss")]
    public async Task BlankEvidenceUsesRetainedCapturedCoordinatesWithoutExtendingRetention(string kind)
    {
        var pr = this.Seed(1, kind == "finding");
        var job = this.CapturedJob(pr);
        this._db.Add(job);
        if (kind == "finding")
        {
            var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
            this._db.Entry(finding).Property(f => f.JobId).CurrentValue = job.Id;
            finding.ProviderScope = "";
            finding.CurrentDisposition = CodeInsightDisposition.Addressed;
            finding.CurrentClassifierVersion = "retained-judge";
            finding.CurrentClassifierConfidence = .9;
            finding.OutcomeSourceFingerprint = "retained-source";
            finding.OutcomeObservedAt = this._now.AddDays(-1);
        }
        else if (kind == "exposure")
        {
            this._db.Add(
                new CodeInsightReviewExposure
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = job.Id, ProviderScope = "", ObservedAt = this._now.AddDays(-30)
                });
        }
        else
        {
            this._db.Add(this.LegacyMiss(pr, "human"));
        }

        await this._db.SaveChangesAsync();
        var originalAge = pr.LastActivityAt;
        var originalUpdated = pr.UpdatedAt;
        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        var source = Assert.Single(claim.Sources);
        Assert.Equal(Scope, source.ProviderScope);
        Assert.Equal(job.Id, source.RepresentativeJobId);
        Assert.True(source.Gaps.HasFlag(CodeInsightHistoricalGap.HarvestCoverage));
        await this._db.Entry(pr).ReloadAsync();
        Assert.Equal(originalAge, pr.LastActivityAt);
        Assert.Equal(originalUpdated, pr.UpdatedAt);
        if (kind == "finding")
        {
            var finding = await this._db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.CodeInsightPullRequestId == pr.Id);
            Assert.Equal(Scope, finding.ProviderScope);
            Assert.Equal("retained-judge", finding.CurrentClassifierVersion);
            Assert.Equal(.9, finding.CurrentClassifierConfidence);
            Assert.Equal("retained-source", finding.OutcomeSourceFingerprint);
            Assert.Equal(this._now.AddDays(-1), finding.OutcomeObservedAt);
        }
    }

    [Fact]
    public async Task DirectBlankSourcesAreQualifiedIndividuallyAndAnAmbiguousMissRemainsUnassigned()
    {
        var pr = this.Seed(1);
        var ownJob = this.CapturedJob(pr);
        var otherJob = this.CapturedJob(pr, "https://ado.example.test/other");
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        this._db.Entry(finding).Property(f => f.JobId).CurrentValue = ownJob.Id;
        finding.ProviderScope = "";
        this._db.AddRange(
            ownJob, otherJob, this.LegacyMiss(pr, "human"), new CodeInsightReviewExposure
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = otherJob.Id, ProviderScope = "", ObservedAt = this._now.AddDays(-30)
            });
        await this._db.SaveChangesAsync();

        var sources = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15))).Sources;

        Assert.Equal(3, sources.Count);
        Assert.Contains(sources, s => s.ProviderScope == Scope && s.RepresentativeJobId == ownJob.Id);
        Assert.Contains(sources, s => s.ProviderScope == "AzureDevOps:https://ado.example.test/other" && s.RepresentativeJobId == otherJob.Id);
        Assert.Contains(sources, s => s.ProviderScope == "" && s.RepresentativeJobId is null);
        Assert.Equal("", (await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id)).ProviderScope);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("foreign-repository")]
    [InlineData("foreign-pr")]
    [InlineData("foreign-client")]
    [InlineData("malformed")]
    public async Task BlankDirectEvidenceCannotBorrowAnUnrelatedValidJob(string invalid)
    {
        var pr = this.Seed(1);
        var direct = this.CapturedJob(pr);
        var valid = this.CapturedJob(pr);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        this._db.Entry(finding).Property(f => f.JobId).CurrentValue = direct.Id;
        finding.ProviderScope = "";
        this._db.Add(valid);
        if (invalid != "deleted")
        {
            this._db.Add(direct);
            if (invalid == "foreign-repository")
            {
                this._db.Entry(direct).Property(j => j.RepositoryId).CurrentValue = "foreign";
            }
            else if (invalid == "foreign-pr")
            {
                this._db.Entry(direct).Property(j => j.PullRequestId).CurrentValue = 2;
            }
            else if (invalid == "foreign-client")
            {
                this._db.Entry(direct).Property(j => j.ClientId).CurrentValue = Guid.NewGuid();
            }
            else
            {
                this._db.Entry(direct).Property(j => j.OrganizationUrl).CurrentValue = "malformed";
            }
        }

        this._db.Add(this.LegacyMiss(pr, "human"));
        await this._db.SaveChangesAsync();

        var source = Assert.Single(Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15))).Sources);

        Assert.Equal("", source.ProviderScope);
        Assert.Null(source.RepresentativeJobId);
        Assert.Equal("", (await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id)).ProviderScope);
    }

    [Fact]
    public async Task CanonicalMissCollisionPreservesBothSuccessfulJudgmentsAndLeavesTheLegacySourceUnavailable()
    {
        var pr = this.Seed(1, false);
        var legacy = this.LegacyMiss(pr, "human");
        var canonical = this.LegacyMiss(pr, "human");
        canonical.ProviderScope = Scope;
        canonical.RecordJudgement(false, false, false, .8, "other-judge", true, "Other retained evidence", this._now.AddDays(-20));
        this._db.AddRange(this.CapturedJob(pr), legacy, canonical);
        await this._db.SaveChangesAsync();

        var sources = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15))).Sources;

        Assert.Contains(sources, s => s.ProviderScope == "" && s.RepresentativeJobId is null);
        var retained = await this._db.CodeInsightMisses.AsNoTracking().Where(m => m.CodeInsightPullRequestId == pr.Id).ToListAsync();
        Assert.Equal(2, retained.Count);
        Assert.Equal("retained-human", retained.Single(m => m.Id == legacy.Id).ClassifierVersion);
        Assert.Equal("Other retained evidence", retained.Single(m => m.Id == canonical.Id).EncryptedDiscussion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualifiedCoverageRequiresFreshEnumerationAndEligibilityKeepsItsNewestExclusion(bool legacyIsNewer)
    {
        var pr = this.Seed(1, false);
        this._db.AddRange(
            this.CapturedJob(pr), this.LegacyMiss(pr, "human"), new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = "", EnumerationComplete = true,
                AllHumanThreadsResolved = true, ObservedAt = this._now.AddDays(-10)
            }, new CodeInsightHarvestCoverage
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = Scope, EnumerationComplete = true,
                AllHumanThreadsResolved = true, ObservedAt = this._now.AddDays(-20)
            });
        await this._db.SaveChangesAsync();
        var codec = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ISecretProtectionCodec>();
        var misses = new CodeInsightMissStore(this._db, codec);
        var key = new CodeInsightPullRequestKey(this._client, pr.RepositoryId, pr.PullRequestId);
        var newest = this._now.AddDays(-1);
        await misses.ObserveThreadEligibilityAsync(key, "human", "", legacyIsNewer, legacyIsNewer ? newest : newest.AddDays(-1));
        await misses.ObserveThreadEligibilityAsync(key, "human", Scope, !legacyIsNewer, legacyIsNewer ? newest.AddDays(-1) : newest);
        pr.MissHarvestProviderScope = "";
        pr.MissHarvestObservedAt = this._now.AddDays(-10);
        pr.MissHarvestSettled = true;
        await this._db.SaveChangesAsync();

        var source = Assert.Single(Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15))).Sources);

        Assert.Equal(Scope, source.ProviderScope);
        Assert.True(source.Gaps.HasFlag(CodeInsightHistoricalGap.HarvestCoverage));
        var coverage = Assert.Single(await this._db.CodeInsightHarvestCoverage.AsNoTracking().Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync());
        Assert.Equal(Scope, coverage.ProviderScope);
        Assert.False(coverage.EnumerationComplete);
        Assert.Equal(this._now.AddDays(-10), coverage.ObservedAt);
        var eligibility = await misses.GetThreadEligibilityAsync(key, "human", Scope);
        Assert.NotNull(eligibility);
        Assert.Equal(newest, eligibility.SourceObservedAt);
        Assert.True(eligibility.ExcludedFromHumanMisses);
        Assert.Single(await this._db.CodeInsightThreadEligibility.Where(e => e.CodeInsightPullRequestId == pr.Id).ToListAsync());
        await this._db.Entry(pr).ReloadAsync();
        Assert.Equal(Scope, pr.MissHarvestProviderScope);
        Assert.Equal(this._now.AddDays(-10), pr.MissHarvestObservedAt);
        var miss = await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.CodeInsightPullRequestId == pr.Id);
        Assert.Equal("retained-human", miss.ClassifierVersion);
        Assert.Equal("Retained discussion", miss.EncryptedDiscussion);
        Assert.Equal(this._now.AddDays(-30), miss.LastJudgedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptOutOrCancellationDuringQualificationRollsBackSourceAssociations(bool cancel)
    {
        var pr = this.Seed(1, false);
        var miss = this.LegacyMiss(pr, "human");
        this._db.AddRange(this.CapturedJob(pr), miss);
        pr.MissHarvestObservedAt = this._now.AddDays(-10);
        pr.MissHarvestProviderScope = "";
        await this._db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var gateCalls = 0;
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++gateCalls < 3)
            {
                return true;
            }

            if (cancel)
            {
                cancellation.Cancel();
            }

            return cancel;
        });

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.Store(this._db).ClaimAsync(
                1, this._now, TimeSpan.FromMinutes(15), cancellation.Token));
        }
        else
        {
            Assert.Empty(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        }

        await this._db.Entry(pr).ReloadAsync();
        Assert.Null(pr.LastHistoricalRefreshAttemptAt);
        Assert.Equal("", pr.MissHarvestProviderScope);
        Assert.Equal("", (await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.Id == miss.Id)).ProviderScope);
        Assert.Empty(await this._db.CodeInsightHarvestCoverage.Where(c => c.CodeInsightPullRequestId == pr.Id).ToListAsync());
    }

    [Fact]
    public async Task QualificationSkipsAnActiveReceiptWriterWithoutChangingItsCapturedScope()
    {
        var pr = this.Seed(1);
        var job = this.CapturedJob(pr);
        this._db.Add(job);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        this._db.Entry(finding).Property(f => f.JobId).CurrentValue = job.Id;
        finding.ProviderScope = "";
        await this._db.SaveChangesAsync();
        await using var writer = new MeisterProPRDbContext(this._options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM code_insight_findings WHERE id = {finding.Id} FOR UPDATE");

        var claims = await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(claims);
        Assert.Equal("", (await this._db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == finding.Id)).ProviderScope);
        await this._db.Entry(pr).ReloadAsync();
        Assert.Null(pr.LastHistoricalRefreshAttemptAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimContinuesPastBusyLegacyQualificationToTheNextEligibleAggregate(bool aggregateWriter)
    {
        var first = this.Seed(1);
        var second = this.Seed(2);
        first.LastHistoricalRefreshAttemptAt = this._now.AddDays(-2);
        second.LastHistoricalRefreshAttemptAt = this._now.AddDays(-1);
        var firstJob = this.CapturedJob(first);
        var secondJob = this.CapturedJob(second);
        this._db.AddRange(firstJob, secondJob);
        var firstFinding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == first.Id).Entity;
        firstFinding.ProviderScope = "";
        this._db.Entry(firstFinding).Property(f => f.JobId).CurrentValue = firstJob.Id;
        var secondFinding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == second.Id).Entity;
        this._db.Entry(secondFinding).Property(f => f.JobId).CurrentValue = secondJob.Id;
        await this._db.SaveChangesAsync();
        await using var writer = new MeisterProPRDbContext(this._options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        if (aggregateWriter)
        {
            await CodeInsightAggregateWriteLock.AcquireAsync(writer, new(first.ClientId, first.RepositoryId, first.PullRequestId), CancellationToken.None);
        }
        else
        {
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM code_insight_findings WHERE id = {firstFinding.Id} FOR UPDATE");
        }

        var claim = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(second.Id, claim.AggregateId);
        Assert.Equal(secondJob.Id, Assert.Single(claim.Sources).RepresentativeJobId);
        Assert.Equal("", (await this._db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == firstFinding.Id)).ProviderScope);
        Assert.Equal(
            this._now.AddDays(-2), (await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == first.Id)).LastHistoricalRefreshAttemptAt);
    }

    [Fact]
    public async Task QualificationSearchStopsAtTheExistingMaximumWhenEarlierLegacyEvidenceIsBusy()
    {
        var lockedFindings = new List<Guid>();
        CodeInsightPullRequest last = null!;
        for (var i = 0; i <= CodeInsightHistoricalRefreshStore.MaximumPullRequests; i++)
        {
            var pr = this.Seed(i + 1);
            pr.LastHistoricalRefreshAttemptAt = this._now.AddMinutes(i - 500);
            var job = this.CapturedJob(pr);
            this._db.Add(job);
            var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single(e => e.Entity.CodeInsightPullRequestId == pr.Id).Entity;
            this._db.Entry(finding).Property(f => f.JobId).CurrentValue = job.Id;
            if (i < CodeInsightHistoricalRefreshStore.MaximumPullRequests)
            {
                finding.ProviderScope = "";
                lockedFindings.Add(finding.Id);
            }

            last = pr;
        }

        await this._db.SaveChangesAsync();
        await using var writer = new MeisterProPRDbContext(this._options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var ids = lockedFindings.ToArray();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM code_insight_findings WHERE id = ANY({ids}) FOR UPDATE");

        Assert.Empty(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(
            last.LastHistoricalRefreshAttemptAt,
            (await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == last.Id)).LastHistoricalRefreshAttemptAt);
        Assert.Null(last.LastHistoricalRefreshAttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EligibilityOnlyQualificationRequiresFreshCoverageForANewerLegacyExclusion(bool canonicalEligibility)
    {
        var pr = this.Seed(1, coverage: true);
        var job = this.CapturedJob(pr);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        this._db.Entry(finding).Property(f => f.JobId).CurrentValue = job.Id;
        finding.NativeStatus = "Active";
        var miss = this.LegacyMiss(pr, "human");
        miss.ProviderScope = Scope;
        miss.SourceFingerprint = "retained-human-source";
        this._db.AddRange(job, miss);
        await this._db.SaveChangesAsync();
        var misses = new CodeInsightMissStore(this._db, Substitute.For<MeisterDev.ProPR.Application.Interfaces.ISecretProtectionCodec>());
        var key = new CodeInsightPullRequestKey(pr.ClientId, pr.RepositoryId, pr.PullRequestId);
        if (canonicalEligibility)
        {
            await misses.ObserveThreadEligibilityAsync(key, "human", Scope, false, this._now.AddDays(-2), mode: CodeInsightObservationMode.HistoricalBackfill);
        }

        await misses.ObserveThreadEligibilityAsync(key, "human", "", true, this._now.AddDays(-1), mode: CodeInsightObservationMode.HistoricalBackfill);

        var source = Assert.Single(Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15))).Sources);

        Assert.Equal(CodeInsightHistoricalGap.HarvestCoverage, source.Gaps);
        var coverage = await this._db.CodeInsightHarvestCoverage.AsNoTracking().SingleAsync(c => c.CodeInsightPullRequestId == pr.Id);
        Assert.False(coverage.EnumerationComplete);
        Assert.Equal(this._now.AddDays(-1), coverage.ObservedAt);
        var eligibility = await misses.GetThreadEligibilityAsync(key, "human", Scope);
        Assert.NotNull(eligibility);
        Assert.True(eligibility.ExcludedFromHumanMisses);
        Assert.Equal(this._now.AddDays(-1), eligibility.SourceObservedAt);
        Assert.False((await this._db.CodeInsightMisses.AsNoTracking().SingleAsync(m => m.Id == miss.Id)).ExcludedAsOwnFinding);
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("eligibility")]
    public async Task MetadataOnlyInterruptedAttemptRemainsClaimableAfterFreshCoverageFillsItsGap(string retainedPopulation)
    {
        var pr = this.Seed(1, false);
        this._db.Add(this.CapturedJob(pr));
        await this._db.SaveChangesAsync();
        if (retainedPopulation == "coverage")
        {
            this._db.Add(
                new CodeInsightHarvestCoverage { Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = Scope, ObservedAt = this._now });
            await this._db.SaveChangesAsync();
        }
        else
        {
            var misses = new CodeInsightMissStore(this._db, Substitute.For<MeisterDev.ProPR.Application.Interfaces.ISecretProtectionCodec>());
            await misses.ObserveThreadEligibilityAsync(
                new(pr.ClientId, pr.RepositoryId, pr.PullRequestId), "human", Scope, true, this._now,
                mode: CodeInsightObservationMode.HistoricalBackfill);
        }

        var original = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15)));
        var coverage = await this._db.CodeInsightHarvestCoverage.SingleOrDefaultAsync(c => c.CodeInsightPullRequestId == pr.Id);
        if (coverage is null)
        {
            coverage = new CodeInsightHarvestCoverage { Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = Scope };
            this._db.Add(coverage);
        }

        coverage.ObservedAt = this._now.AddSeconds(1);
        coverage.EnumerationComplete = true;
        await this._db.SaveChangesAsync();

        var retry = Assert.Single(await this.Store(this._db).ClaimAsync(1, this._now.AddMinutes(16), TimeSpan.FromMinutes(15)));

        Assert.Equal(pr.Id, retry.AggregateId);
        Assert.NotEqual(original.AttemptId, retry.AttemptId);
        Assert.Equal(CodeInsightHistoricalGap.None, retry.Gaps);
        Assert.Null((await this._db.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == pr.Id)).LastHistoricalRefreshCompletedAt);
    }

    [Fact]
    public async Task QualificationRetainsTheCapturedJobAgainstDeletionWhileAllowingItsLeaseToRenew()
    {
        var pr = this.Seed(1);
        var job = this.CapturedJob(pr);
        job.Status = JobStatus.Processing;
        job.ProcessingStartedAt = this._now;
        job.ApplyLease("host", 1, this._now.AddMinutes(2), this._now);
        this._db.Add(job);
        var finding = this._db.ChangeTracker.Entries<CodeInsightFinding>().Single().Entity;
        finding.ProviderScope = "";
        this._db.Entry(finding).Property(f => f.JobId).CurrentValue = job.Id;
        await this._db.SaveChangesAsync();
        var qualified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateCalls = 0;
        this._gate.IsCollectionEnabledAsync(this._client, Arg.Any<CancellationToken>()).Returns(async c =>
        {
            if (Interlocked.Increment(ref gateCalls) == 3)
            {
                qualified.SetResult();
                await release.Task.WaitAsync(c.ArgAt<CancellationToken>(1));
            }

            return true;
        });
        var claim = this.Store(this._db).ClaimAsync(1, this._now, TimeSpan.FromMinutes(15));
        await using var renewalDb = new MeisterProPRDbContext(this._options);
        var leases = new ReviewJobLeaseStore(
            renewalDb, Substitute.For<MeisterDev.ProPR.Application.Interfaces.IJobRepository>(), Options.Create(new ReviewLeaseOptions()),
            NullLogger<ReviewJobLeaseStore>.Instance);
        Task<ReviewJobLeaseRenewal>? renewal = null;
        try
        {
            await qualified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            renewal = leases.TryRenewAsync(new(job.Id, "host", 1, this._now.AddMinutes(2)), TimeSpan.FromMinutes(5));
            Assert.Same(renewal, await Task.WhenAny(renewal, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.True((await renewal).Accepted);
            await using var purgeDb = new MeisterProPRDbContext(this._options);
            await purgeDb.Database.OpenConnectionAsync();
            await purgeDb.Database.ExecuteSqlRawAsync("SET lock_timeout = '200ms'");
            var deletion = await Record.ExceptionAsync(() => purgeDb.ReviewJobs.Where(j => j.Id == job.Id).ExecuteDeleteAsync());
            var databaseFailure = Assert.IsType<Npgsql.PostgresException>(deletion?.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, databaseFailure.SqlState);
            Assert.True(await purgeDb.ReviewJobs.AnyAsync(j => j.Id == job.Id));
        }
        finally
        {
            release.TrySetResult();
            await claim;
            if (renewal is not null)
            {
                await renewal;
            }
        }

        Assert.Single(await claim);
        Assert.Equal(Scope, (await this._db.CodeInsightFindings.AsNoTracking().SingleAsync(f => f.Id == finding.Id)).ProviderScope);
    }

    private ReviewJob CapturedJob(CodeInsightPullRequest pr, string path = "https://ado.example.test/collection")
    {
        var job = new ReviewJob(Guid.NewGuid(), this._client, path, "project", pr.RepositoryId, (int)pr.PullRequestId, 1);
        this._jobIds.Add(job.Id);
        return job;
    }

    private CodeInsightMiss LegacyMiss(CodeInsightPullRequest pr, string thread)
    {
        var miss = new CodeInsightMiss
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ProviderScope = "", ProviderThreadId = thread, HarvestedAt = this._now.AddDays(-30),
            TypeMembership = "concurrency", DimensionClassifierVersion = "retained-types", DimensionConfidence = .9, SourceObservedAt = this._now.AddDays(-30)
        };
        miss.RecordJudgement(true, true, true, .9, "retained-human", true, "Retained discussion", this._now.AddDays(-30));
        return miss;
    }
}
