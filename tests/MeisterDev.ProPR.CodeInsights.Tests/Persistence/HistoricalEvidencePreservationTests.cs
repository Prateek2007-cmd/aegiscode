// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class HistoricalEvidencePreservationTests
{
    private const string Scope = "AzureDevOps:https://ado.example.test/collection";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullHistoricalNativeStatusIsNotAcknowledgedAndPreservesSuccessfulEvidence(bool newerRetainedEvidence)
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            finding.Id,
            new CodeInsightDispositionRecord(
                CodeInsightDisposition.FalsePositive, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "retained-judge", .9));
        finding.NativeStatus = "Closed";
        finding.CurrentDisposition = CodeInsightDisposition.FalsePositive;
        finding.CurrentClassifierVersion = "retained-judge";
        finding.CurrentClassifierConfidence = .9;
        finding.CurrentCodeChange = ThreadAnchorCodeChange.Unchanged;
        finding.OutcomeSourceFingerprint = "retained-source";
        var retainedAt = newerRetainedEvidence ? Now.AddMinutes(1) : Now.AddDays(-1);
        finding.OutcomeObservedAt = retainedAt;
        await db.SaveChangesAsync();
        var initial = await db.CodeInsightFindingDispositions.SingleAsync();
        var decidedAt = initial.DecidedAt;

        var acknowledgment = await new CodeInsightPerformanceEvidenceStore(db).TryPreserveHistoricalOutcomeAsync(
            finding.Id,
            new CodeInsightHistoricalOutcomeObservation(
                new CodeInsightPublicationReceipt(Scope, "own", "1"), null!, ThreadResolutionIntent.ClaimsFix,
                ThreadAnchorCodeChange.Unchanged, "new-source", Now));

        Assert.False(acknowledgment.Retained);
        Assert.Equal("Closed", finding.NativeStatus);
        Assert.Equal(CodeInsightDisposition.FalsePositive, finding.CurrentDisposition);
        Assert.Equal("retained-judge", finding.CurrentClassifierVersion);
        Assert.Equal(.9, finding.CurrentClassifierConfidence);
        Assert.Equal("retained-source", finding.OutcomeSourceFingerprint);
        Assert.Equal(retainedAt, finding.OutcomeObservedAt);
        Assert.Equal(decidedAt, initial.DecidedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledHistoricalEvidenceEntriesDoNotCreateAContext(bool acknowledgment)
    {
        await using var db = Db();
        var factory = Substitute.For<IDbContextFactory<MeisterProPRDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<MeisterProPRDbContext>(new InvalidOperationException("The canceled operation created a context.")));
        var store = new CodeInsightPerformanceEvidenceStore(db, factory);
        var receipt = new CodeInsightPublicationReceipt(Scope, "own", "1");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (acknowledgment)
            {
                await store.AcknowledgeOutcomeAsync(Guid.NewGuid(), receipt, "Closed", "source", Now, canceled.Token);
            }
            else
            {
                await store.TryPreserveHistoricalOutcomeAsync(
                    Guid.NewGuid(),
                    new CodeInsightHistoricalOutcomeObservation(
                        receipt, "Closed", ThreadResolutionIntent.ClaimsFix,
                        ThreadAnchorCodeChange.Unchanged, "source", Now), canceled.Token);
            }
        });

        await factory.DidNotReceive().CreateDbContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnsupportedHistoricalRepairReportsRetentionFailureWithoutClaimingAnIdentityFailure()
    {
        ICodeInsightPerformanceEvidenceStore store = new CurrentOnlyEvidenceStore();

        var acknowledgment = await store.TryRepairHistoricalThreadIdentityAsync(Guid.NewGuid(), new CodeInsightPublicationReceipt(Scope, null, "1"), "own");

        Assert.False(acknowledgment.Retained);
        Assert.Equal(CodeInsightObservationUnavailableReason.EvidenceRetentionFailed, acknowledgment.Reason);
    }

    [Fact]
    public async Task HistoricalFallbackAfterSiblingPreservationKeepsEachFindingJudgement()
    {
        await using var db = Db();
        var first = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            first.Id,
            new CodeInsightDispositionRecord(
                CodeInsightDisposition.FalsePositive, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "retained-first", .95));
        var second = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = first.CodeInsightPullRequestId, CodeInsightPullRequest = first.CodeInsightPullRequest,
            JobId = Guid.NewGuid(), RevisionKey = "later", ProviderScope = Scope, ProviderThreadId = "own", ProviderCommentId = "1",
            EncryptedMessage = "Another concern", PublicationState = CodeInsightPublicationState.Published,
        };
        db.Add(second);
        await db.SaveChangesAsync();
        await store.RecordDispositionAsync(
            second.Id,
            new CodeInsightDispositionRecord(CodeInsightDisposition.Addressed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null));
        var classifier = Judge();
        var service = Service(db, store, classifier);

        Assert.True((await service.ObserveHistoricalAsync(Snapshot(first), Outcome(first))).Retained);
        Assert.True((await service.ObserveHistoricalAsync(Snapshot(second), Outcome(second))).Retained);

        Assert.Equal(CodeInsightDisposition.FalsePositive, first.CurrentDisposition);
        Assert.Equal("retained-first", first.CurrentClassifierVersion);
        Assert.Equal(.95, first.CurrentClassifierConfidence);
        Assert.Equal(CodeInsightDisposition.Dismissed, second.CurrentDisposition);
        Assert.Equal("new-judge", second.CurrentClassifierVersion);
        await classifier.Received(1).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(
            CodeInsightDisposition.FalsePositive, (await db.CodeInsightFindingDispositions.SingleAsync(d => d.CodeInsightFindingId == first.Id)).Disposition);
        Assert.Equal(
            CodeInsightDisposition.Addressed, (await db.CodeInsightFindingDispositions.SingleAsync(d => d.CodeInsightFindingId == second.Id)).Disposition);
    }

    [Fact]
    public async Task HistoricalOutcomeFingerprintRepairDoesNotSuppressOrdinaryClassifierVersionReplay()
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            finding.Id,
            new CodeInsightDispositionRecord(
                CodeInsightDisposition.FalsePositive, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "old-judge", .95));
        var classifier = Judge();
        var service = Service(db, store, classifier);
        var evt = Outcome(finding);
        Assert.True((await service.ObserveHistoricalAsync(Snapshot(finding), evt)).Retained);
        await classifier.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());

        await service.HandleThreadResolvedAsync(evt with { ObservedAt = Now.AddMinutes(1) });

        await classifier.Received(1).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("new-judge", finding.CurrentClassifierVersion);
        Assert.Equal(CodeInsightDisposition.FalsePositive, (await db.CodeInsightFindingDispositions.SingleAsync()).Disposition);
    }

    [Fact]
    public async Task HistoricalHumanFingerprintRepairDoesNotSuppressOrdinaryClassifierVersionReplay()
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt));
        var human = HumanJudge();
        var harvester = Harvester(db, misses, human);
        Assert.True((await harvester.ObserveHistoricalAsync(evt)).Retained);
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());

        Assert.True(await harvester.HandleThreadObservedAsync(evt with { ObservedAt = Now.AddMinutes(1) }));

        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("new-human", (await db.CodeInsightMisses.SingleAsync()).ClassifierVersion);
    }

    [Fact]
    public async Task HistoricalRejudgementPersistsTheNewConcernAnchorForLaterPreservation()
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt));
        var human = HumanJudge();
        var harvester = Harvester(db, misses, human);
        var moved = evt with { FilePath = "src/Moved.cs", Line = 17 };

        Assert.True((await harvester.ObserveHistoricalAsync(moved)).Retained);
        var retained = await db.CodeInsightMisses.SingleAsync();
        Assert.Equal(moved.FilePath, retained.FilePath);
        Assert.Equal(moved.Line, retained.LineNumber);
        var judgedAt = retained.LastJudgedAt;
        human.ClearReceivedCalls();
        Assert.True((await harvester.ObserveHistoricalAsync(moved with { ObservedAt = Now.AddMinutes(1) })).Retained);
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(judgedAt, retained.LastJudgedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalUnusableDiscussionCannotAcknowledgeAnOlderHumanJudgement(bool whitespace)
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt));
        var human = HumanJudge();
        var unusable = evt with { Comments = whitespace ? [new ThreadUpdatedComment("1", "Human", false, Now, " ")] : [] };

        Assert.False((await Harvester(db, misses, human).ObserveHistoricalAsync(unusable)).Retained);
        Assert.Equal(Now.AddDays(-1).ToUnixTimeMilliseconds(), (await db.CodeInsightMisses.SingleAsync()).SourceObservedAt!.Value.ToUnixTimeMilliseconds());
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SharedReceiptPreservationDoesNotReplaceAnotherFindingsSuccessfulCurrentJudgement()
    {
        await using var db = Db();
        var first = await Finding(db);
        first.NativeStatus = "Closed";
        first.CurrentDisposition = CodeInsightDisposition.FalsePositive;
        first.CurrentCodeChange = ThreadAnchorCodeChange.Unchanged;
        first.CurrentClassifierVersion = "first-judge";
        first.CurrentClassifierConfidence = .8;
        var second = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = first.CodeInsightPullRequestId,
            JobId = Guid.NewGuid(), RevisionKey = "later", ProviderScope = Scope, ProviderThreadId = "own", ProviderCommentId = "1",
            NativeStatus = "Closed", CurrentDisposition = CodeInsightDisposition.Dismissed, CurrentCodeChange = ThreadAnchorCodeChange.Unchanged,
            CurrentClassifierVersion = "second-judge", CurrentClassifierConfidence = .9,
        };
        db.Add(second);
        await db.SaveChangesAsync();
        Assert.True(
            (await new CodeInsightPerformanceEvidenceStore(db).TryPreserveHistoricalOutcomeAsync(
                first.Id,
                new CodeInsightHistoricalOutcomeObservation(
                    new CodeInsightPublicationReceipt(Scope, "own", "1"), "Closed", ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "fresh",
                    Now))).Retained);
        Assert.Equal(CodeInsightDisposition.Dismissed, second.CurrentDisposition);
        Assert.Equal("second-judge", second.CurrentClassifierVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("inconsistent")]
    public async Task ConsistentInitialOutcomeRepairsNativeEvidenceWithoutReplacingJudgement(string? fingerprint)
    {
        await using var db = Db();
        var finding = await Finding(db);
        finding.OutcomeSourceFingerprint = fingerprint;
        var store = new CodeInsightFindingStore(db, Codec());
        Assert.True(
            await store.RecordDispositionAsync(
                finding.Id, new CodeInsightDispositionRecord(
                    CodeInsightDisposition.FalsePositive,
                    ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "old-judge", .95, CodeInsightRejectionReason.Wrong)));
        var initial = await db.CodeInsightFindingDispositions.SingleAsync();
        var decidedAt = initial.DecidedAt;
        var classifier = Judge();
        var snapshot = Snapshot(finding);
        var ack = await Service(db, store, classifier).ObserveHistoricalAsync(snapshot, Outcome(finding));
        Assert.True(ack.Retained);
        Assert.Equal(CodeInsightDisposition.FalsePositive, finding.CurrentDisposition);
        Assert.Equal("old-judge", finding.CurrentClassifierVersion);
        Assert.Equal(.95, finding.CurrentClassifierConfidence);
        Assert.Equal("Closed", finding.NativeStatus);
        Assert.NotNull(finding.OutcomeSourceFingerprint);
        Assert.Equal(Now, finding.OutcomeObservedAt);
        Assert.Equal(decidedAt, initial.DecidedAt);
        await classifier.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfulCurrentOutcomePrecedesInitialAndVersionChangesDoNotReplaceIt()
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            finding.Id, new CodeInsightDispositionRecord(
                CodeInsightDisposition.Dismissed,
                ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "initial", .8));
        finding.NativeStatus = "Fixed";
        finding.CurrentDisposition = CodeInsightDisposition.FalsePositive;
        finding.CurrentCodeChange = ThreadAnchorCodeChange.Unchanged;
        finding.CurrentClassifierVersion = "current-old-version";
        finding.CurrentClassifierConfidence = .9;
        await db.SaveChangesAsync();
        var classifier = Judge();
        var ack = await Service(db, store, classifier).ObserveHistoricalAsync(Snapshot(finding), Outcome(finding));
        Assert.True(ack.Retained);
        Assert.Equal(CodeInsightDisposition.FalsePositive, finding.CurrentDisposition);
        Assert.Equal("current-old-version", finding.CurrentClassifierVersion);
        Assert.Equal(CodeInsightDisposition.Dismissed, (await db.CodeInsightFindingDispositions.SingleAsync()).Disposition);
        await classifier.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ThreadAnchorCodeChange.Unknown)]
    [InlineData(ThreadAnchorCodeChange.Changed)]
    public async Task ChangedOrUnverifiableRequiredSignalsDoNotPreserveAnInitialSplit(ThreadAnchorCodeChange change)
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            finding.Id, new CodeInsightDispositionRecord(
                CodeInsightDisposition.FalsePositive,
                ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "old", .9));
        var classifier = Judge();
        var ack = await Service(db, store, classifier).ObserveHistoricalAsync(Snapshot(finding), Outcome(finding) with { CodeChangedSinceRaised = change });
        Assert.True(ack.Retained);
        Assert.Equal(
            change == ThreadAnchorCodeChange.Changed ? CodeInsightDisposition.Addressed : CodeInsightDisposition.Dismissed, finding.CurrentDisposition);
        Assert.Equal(CodeInsightDisposition.FalsePositive, (await db.CodeInsightFindingDispositions.SingleAsync()).Disposition);
        if (change == ThreadAnchorCodeChange.Unknown)
        {
            await classifier.Received(1).JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await classifier.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task UnknownClassifierResultIsExplicitlyUnacknowledged()
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        var classifier = Judge();
        classifier.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns((DisregardedFindingJudgement?)null);
        var ack = await Service(db, store, classifier).ObserveHistoricalAsync(Snapshot(finding), Outcome(finding));
        Assert.False(ack.Retained);
        Assert.Equal(CodeInsightObservationUnavailableReason.ClassificationUnavailable, ack.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("inconsistent")]
    public async Task SameScopedHumanConcernPreservesJudgementDimensionsAndJudgedText(string? fingerprint)
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt) with { SourceFingerprint = fingerprint });
        var retained = await db.CodeInsightMisses.SingleAsync();
        var judgedAt = retained.LastJudgedAt;
        var human = HumanJudge();
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("new-type-version");
        var harvester = new CodeInsightMissHarvester(
            LocalScmPolicies.Registry.CompatibilityCodec,
            new CodeInsightFindingStore(db, Codec()), misses, human, Gate(), TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance, types, Substitute.For<ICodeInsightTaxonomyService>());
        var ack = await harvester.ObserveHistoricalAsync(evt);
        Assert.True(ack.Retained);
        Assert.True(retained.CountsAsMiss);
        Assert.Equal("old-human", retained.ClassifierVersion);
        Assert.Equal("old-types", retained.DimensionClassifierVersion);
        Assert.Equal("concurrency", retained.TypeMembership);
        Assert.Equal("Human: This race needs synchronization.", retained.EncryptedDiscussion);
        Assert.Equal(judgedAt, retained.LastJudgedAt);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), retained.SourceObservedAt!.Value.ToUnixTimeMilliseconds());
        Assert.NotNull(retained.SourceFingerprint);
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await types.DidNotReceive().ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("changed-body")]
    [InlineData("reopened")]
    public async Task ChangedHumanConcernOrResolutionIsJudgedAgain(string change)
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt));
        var human = HumanJudge();
        var updated = change == "reopened"
            ? evt with { Status = "Active" }
            : evt with { Comments = [new ThreadUpdatedComment("1", "Human", false, Now, "A different concern now.")] };
        var harvester = new CodeInsightMissHarvester(
            LocalScmPolicies.Registry.CompatibilityCodec,
            new CodeInsightFindingStore(db, Codec()), misses, human, Gate(), TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance);
        Assert.True((await harvester.ObserveHistoricalAsync(updated)).Retained);
        await human.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DimensionRetryRetainsSuccessfulHumanJudgementAndItsOriginalProvenance()
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt) with { DimensionJudgementFailed = true, DimensionConfidence = null });
        var original = await db.CodeInsightMisses.SingleAsync();
        var judgedAt = original.LastJudgedAt;
        var human = HumanJudge();
        var types = Substitute.For<IFindingTypeClassifier>();
        types.ClassifierVersion.Returns("new-types");
        types.ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new FindingClassificationResult(
                new FindingTypeVerdict(["concurrency"], [], CodeInsightFindingLevel.File, CodeInsightFindingQualifier.Missing, .9), true));
        var harvester = new CodeInsightMissHarvester(
            LocalScmPolicies.Registry.CompatibilityCodec,
            new CodeInsightFindingStore(db, Codec()), misses, human, Gate(), TestPostedCommentComposer.Default,
            NullLogger<CodeInsightMissHarvester>.Instance, types, Substitute.For<ICodeInsightTaxonomyService>());
        Assert.True((await harvester.ObserveHistoricalAsync(evt)).Retained);
        Assert.Equal("old-human", original.ClassifierVersion);
        Assert.Equal(judgedAt, original.LastJudgedAt);
        Assert.Equal("Human: This race needs synchronization.", original.EncryptedDiscussion);
        Assert.Equal("new-types", original.DimensionClassifierVersion);
        await human.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CurrentOwnFindingOverlapExcludesMissWithoutReplacingItsRawJudgement()
    {
        await using var db = Db();
        var evt = Human();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var misses = new CodeInsightMissStore(db, Codec());
        await misses.RecordMissAsync(key, Miss(evt));
        var findings = new CodeInsightFindingStore(db, Codec());
        await findings.MaterialiseFindingsAsync(
            key, Guid.NewGuid(), "old", Now.AddDays(-2), [
                new CodeInsightFindingSnapshot(
                    0, evt.FilePath, evt.Line,
                    CommentSeverity.Warning, "This race needs synchronization.", null, null, null, false, null, null, "own", "1", ProviderScope: Scope),
            ]);
        var ack = await misses.TryPreserveHistoricalMissAsync(
            key,
            new CodeInsightHistoricalMissObservation(
                Scope, evt.ThreadId, evt.FilePath, evt.Line, "Human: This race needs synchronization.", true, "fresh", Now));
        Assert.True(ack.Retained);
        Assert.True(ack.Changed);
        var miss = await db.CodeInsightMisses.SingleAsync();
        Assert.True(miss.ExcludedAsOwnFinding);
        Assert.True(miss.IsSubstantive);
        Assert.True(miss.WasActedOn);
        Assert.False(miss.CountsAsMiss);
    }

    [Fact]
    public async Task OlderHistoricalPreservationCannotReplaceNewerCurrentEvidence()
    {
        await using var db = Db();
        var finding = await Finding(db);
        finding.NativeStatus = "Active";
        finding.OutcomeObservedAt = Now.AddMinutes(1);
        await db.SaveChangesAsync();
        var evidence = new CodeInsightPerformanceEvidenceStore(db);
        var ack = await evidence.TryPreserveHistoricalOutcomeAsync(
            finding.Id,
            new CodeInsightHistoricalOutcomeObservation(
                new CodeInsightPublicationReceipt(Scope, "own", "1"), "Closed", ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "fresh",
                Now));
        Assert.True(ack.Retained);
        Assert.Equal("Active", finding.NativeStatus);
        Assert.Equal(Now.AddMinutes(1), finding.OutcomeObservedAt);
    }

    [Fact]
    public async Task NewerFailedCurrentOutcomeCannotAcknowledgeHistoricalCompletionOrFallBackToInitial()
    {
        await using var db = Db();
        var finding = await Finding(db);
        var store = new CodeInsightFindingStore(db, Codec());
        await store.RecordDispositionAsync(
            finding.Id, new CodeInsightDispositionRecord(
                CodeInsightDisposition.FalsePositive,
                ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "initial", .9));
        finding.NativeStatus = "Closed";
        finding.OutcomeObservedAt = Now.AddMinutes(1);
        finding.CurrentDisposition = CodeInsightDisposition.Dismissed;
        finding.CurrentClassifierVersion = "failed-current";
        finding.CurrentClassifierConfidence = null;
        finding.OutcomeSourceFingerprint = "newer-failed";
        await db.SaveChangesAsync();
        var classifier = Judge();
        var ack = await Service(db, store, classifier).ObserveHistoricalAsync(Snapshot(finding), Outcome(finding));
        Assert.False(ack.Retained);
        Assert.Equal(CodeInsightObservationUnavailableReason.ClassificationUnavailable, ack.Reason);
        Assert.Equal("newer-failed", finding.OutcomeSourceFingerprint);
        Assert.Equal("failed-current", finding.CurrentClassifierVersion);
        await classifier.DidNotReceive().JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForeignScopeCannotPreserveAReceipt()
    {
        await using var db = Db();
        var finding = await Finding(db);
        Assert.False(
            (await new CodeInsightPerformanceEvidenceStore(db).TryPreserveHistoricalOutcomeAsync(
                finding.Id,
                new CodeInsightHistoricalOutcomeObservation(
                    new CodeInsightPublicationReceipt("GitHub:https://other.example", "own", "1"), "Fixed", ThreadResolutionIntent.ClaimsFix,
                    ThreadAnchorCodeChange.Changed, "fresh", Now))).Retained);
    }

    private sealed class CurrentOnlyEvidenceStore : ICodeInsightPerformanceEvidenceStore
    {
        public Task<bool> ObserveUnchangedOutcomeAsync(
            Guid findingId, string nativeStatus, string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default,
            CodeInsightPublicationReceipt? receipt = null)
        {
            return Task.FromResult(false);
        }

        public Task<bool> RecordCurrentOutcomeAsync(
            Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
            string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null)
        {
            return Task.FromResult(false);
        }
    }

    private static CodeInsightDispositionService Service(MeisterProPRDbContext db, CodeInsightFindingStore store, IDisregardedFindingClassifier judge)
    {
        return new CodeInsightDispositionService(
            LocalScmPolicies.Registry.CompatibilityCodec, store, store, judge, Gate(),
            NullLogger<CodeInsightDispositionService>.Instance, performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
    }

    private static CodeInsightMissHarvester Harvester(MeisterProPRDbContext db, CodeInsightMissStore misses, IHumanMissClassifier human)
    {
        return new CodeInsightMissHarvester(
            LocalScmPolicies.Registry.CompatibilityCodec, new CodeInsightFindingStore(db, Codec()),
            misses, human, Gate(), TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
    }

    private static async Task<CodeInsightFinding> Finding(MeisterProPRDbContext db)
    {
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, CodeInsightPullRequest = pr,
            JobId = Guid.NewGuid(), RevisionKey = "old", ProviderScope = Scope, ProviderThreadId = "own", ProviderCommentId = "1",
            EncryptedMessage = "Concern", PublicationState = CodeInsightPublicationState.Published, ObservedAt = Now.AddDays(-2),
        };
        db.AddRange(pr, finding);
        await db.SaveChangesAsync();
        return finding;
    }

    private static CodeInsightNativeFindingSnapshot Snapshot(CodeInsightFinding f)
    {
        return new CodeInsightNativeFindingSnapshot(
            new CodeInsightFindingView(
                f.Id, f.JobId, f.RevisionKey, f.Ordinal,
                f.FilePath, f.LineNumber, f.Severity, f.EncryptedMessage, f.ProviderThreadId, f.ObservedAt, f.NativeStatus, f.CurrentCodeChange,
                f.CurrentClassifierVersion, f.CurrentClassifierConfidence, f.OutcomeJudgementAttempts, f.ProviderScope, f.OutcomeSourceFingerprint,
                f.ProviderCommentId), f.PublicationState, f.CurrentDisposition, f.OutcomeObservedAt);
    }

    private static ThreadResolvedDomainEvent Outcome(CodeInsightFinding finding)
    {
        return new ThreadResolvedDomainEvent(
            finding.CodeInsightPullRequest!.ClientId,
            "https://ado.example.test/collection", "project", "repo", 1, "own", null, null, "Provider discussion", Now,
            ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged, "Closed", Scope);
    }

    private static ThreadUpdatedEvent Human()
    {
        return new ThreadUpdatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "repo", 1, "human", "src/Service.cs", 1,
            "Fixed", Now, [new ThreadUpdatedComment("1", "Human", false, Now, "This race needs synchronization.")], Now, Scope);
    }

    private static CodeInsightMissRecord Miss(ThreadUpdatedEvent evt)
    {
        return new CodeInsightMissRecord(
            evt.ThreadId, evt.FilePath, evt.Line,
            "Human: This race needs synchronization.", true, true, true, .9, "old-human", true, "concurrency",
            CodeInsightFindingQualifier.Missing, "old-types", .9, SourceObservedAt: Now.AddDays(-1), ProviderScope: Scope);
    }

    private static IDisregardedFindingClassifier Judge()
    {
        var judge = Substitute.For<IDisregardedFindingClassifier>();
        judge.ClassifierVersion.Returns("new-judge");
        judge.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DisregardedFindingJudgement(false, .8, "correct"));
        return judge;
    }

    private static IHumanMissClassifier HumanJudge()
    {
        var judge = Substitute.For<IHumanMissClassifier>();
        judge.ClassifierVersion.Returns("new-human");
        judge.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(new HumanMissJudgement(true, true, true, .9, "valid"));
        return judge;
    }

    private static MeisterProPRDbContext Db()
    {
        return new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    private static ICodeInsightsCollectionGate Gate()
    {
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return gate;
    }

    private static ISecretProtectionCodec Codec()
    {
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(c => c.ArgAt<string>(0));
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(c => c.ArgAt<string>(0));
        return codec;
    }
}
