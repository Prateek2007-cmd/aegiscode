// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class ReviewerPerformanceEvidenceTests
{
    [Fact]
    public async Task AChangedPublicationReceiptClearsCurrentThreadEvidenceAndRetainsTheInitialDisposition()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var now = DateTimeOffset.UtcNow;
        var job = Guid.NewGuid();
        var input = new CodeInsightFindingSnapshot(
            0, "src/Service.cs", 1, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "1",
            PublicationState: CodeInsightPublicationState.Published);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        var initial = new CodeInsightFindingDisposition
        {
            Id = Guid.NewGuid(),
            CodeInsightFindingId = finding.Id,
            Disposition = CodeInsightDisposition.Acknowledged,
            NativeStatus = "WontFix"
        };
        db.Add(initial);
        await db.SaveChangesAsync();
        await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(
            finding.Id, "WontFix", Outcome(), now, sourceFingerprint: "first-thread-evidence");
        var originalReceipt = new CodeInsightPublicationReceipt(finding.ProviderScope, finding.ProviderThreadId, finding.ProviderCommentId);
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now, [
                input with
                {
                    ProviderThreadId = "second-thread",
                    ProviderCommentId = "2"
                }
            ]);
        Assert.Equal("Unknown", finding.NativeStatus);
        Assert.Null(finding.CurrentDisposition);
        Assert.Null(finding.OutcomeSourceFingerprint);
        Assert.Null(finding.OutcomeObservedAt);
        Assert.Equal("WontFix", initial.NativeStatus);
        Assert.False(
            await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(
                finding.Id, "Fixed", Outcome(), now.AddSeconds(1), receipt: originalReceipt));
        Assert.Equal("Unknown", finding.NativeStatus);
    }

    [Fact]
    public async Task ANewReceiptCannotInheritDuplicateVerificationFromThePreviousPublication()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var now = DateTimeOffset.UtcNow;
        var job = Guid.NewGuid();
        var input = new CodeInsightFindingSnapshot(
            0, null, null, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "1",
            PublicationState: CodeInsightPublicationState.Published, DuplicateState: CodeInsightDuplicateState.ConfirmedDuplicate,
            DuplicateOfPublicationId: "original", DuplicateVerificationSource: "human-audit", DuplicateVerificationConfidence: .9, DuplicateVerifiedAt: now);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now,
            [
                input with
                {
                    ProviderThreadId = "new-thread",
                    DuplicateState = CodeInsightDuplicateState.Unknown,
                    DuplicateOfPublicationId = null,
                    DuplicateVerificationSource = null,
                    DuplicateVerificationConfidence = null,
                    DuplicateVerifiedAt = null
                }
            ]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        Assert.Equal(CodeInsightDuplicateState.Unknown, finding.DuplicateState);
        Assert.Null(finding.DuplicateVerificationSource);
        Assert.Null(finding.DuplicateOfPublicationId);
        Assert.Null(finding.DuplicateVerifiedAt);
    }

    [Fact]
    public async Task NativeChangesAndReopeningRefreshCurrentEvidenceWithoutReplacingInitialDisposition()
    {
        await using var db = Db();
        var finding = Finding(Guid.NewGuid(), "scope-a");
        db.Add(finding);
        var initial = new CodeInsightFindingDisposition
        {
            Id = Guid.NewGuid(),
            CodeInsightFindingId = finding.Id,
            Disposition = CodeInsightDisposition.Acknowledged,
            NativeStatus = "WontFix"
        };
        db.Add(initial);
        await db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(db);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "WontFix", Outcome(), now));
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "ByDesign", Outcome(), now.AddSeconds(1)));
        Assert.Equal("ByDesign", finding.NativeStatus);
        Assert.Equal("WontFix", initial.NativeStatus);
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Active", null, now.AddSeconds(2)));
        Assert.Null(finding.CurrentDisposition);
        Assert.Equal(CodeInsightDisposition.Acknowledged, initial.Disposition);
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "ByDesign", Outcome(), now));
    }

    [Fact]
    public async Task SameNativeStatusRetainsLaterCorroborationAndRecoversFromFailedClassifierWithBoundedAttempts()
    {
        await using var db = Db();
        var finding = Finding(Guid.NewGuid(), "scope-a");
        db.Add(finding);
        await db.SaveChangesAsync();
        var store = new CodeInsightPerformanceEvidenceStore(db);
        var now = DateTimeOffset.UtcNow;
        var failed = new CodeInsightDispositionRecord(
            CodeInsightDisposition.Dismissed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unknown, "judge-v1", null);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed, now.AddSeconds(i)));
        }

        Assert.Equal(3, finding.OutcomeJudgementAttempts);
        Assert.False(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed, now.AddSeconds(4)));
        Assert.True(await store.RecordCurrentOutcomeAsync(finding.Id, "Fixed", failed with { ClassifierConfidence = .9 }, now.AddSeconds(5)));
        Assert.Equal(.9, finding.CurrentClassifierConfidence);
        Assert.True(
            await store.RecordCurrentOutcomeAsync(
                finding.Id, "Fixed", new(CodeInsightDisposition.Addressed, ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, null, null),
                now.AddSeconds(6)));
        Assert.Equal(ThreadAnchorCodeChange.Changed, finding.CurrentCodeChange);
    }

    [Fact]
    public async Task ProviderQualifiedLookupRefusesAmbiguousUnqualifiedIdentities()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "1",
            PullRequestId = 1
        };
        db.Add(pr);
        var a = Finding(pr.Id, "GitHub:https://github.example");
        var b = Finding(pr.Id, "GitLab:https://gitlab.example");
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("finding");
        var store = new CodeInsightFindingStore(db, codec);
        Assert.Null(await store.FindByProviderThreadAsync(client, "1", 1, "1"));
        Assert.Equal(a.Id, (await store.FindByProviderThreadAsync(client, "1", 1, "1", providerScope: a.ProviderScope))!.Id);
        await new CodeInsightPerformanceEvidenceStore(db).RecordCurrentOutcomeAsync(a.Id, "ByDesign", Outcome(), DateTimeOffset.UtcNow);
        Assert.Null(b.NativeStatus);
    }

    [Fact]
    public async Task KnownProviderEventsCannotClaimAnUnknownHistoricalSource()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            RepositoryId = "1",
            PullRequestId = 1
        };
        db.Add(pr);
        var finding = Finding(pr.Id, "");
        db.Add(finding);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("finding");
        var store = new CodeInsightFindingStore(db, codec);
        Assert.Null(await store.FindByProviderThreadAsync(client, "1", 1, "1", providerScope: "GitHub:https://other.example"));
        Assert.Equal(finding.Id, (await store.FindByProviderThreadAsync(client, "1", 1, "1"))!.Id);
    }

    [Fact]
    public void ProviderScopeRetainsProviderAndConfiguredBasePathWithoutCredentials()
    {
        Assert.Equal(
            "GitHub:https://github.example/base/path",
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.GitHub, "https://user:password@github.example/base/path/?token=secret#fragment").Value);
        Assert.NotEqual(
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.AzureDevOps, "https://dev.azure.com/orgA").Value,
            ProviderSourceIdentity.FromConfiguredHost(ScmProvider.AzureDevOps, "https://dev.azure.com/orgB").Value);
    }

    [Fact]
    public async Task DifferentProviderNamespacesRetainAndRefreshIndependentHumanThreadJudgements()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var key = new CodeInsightPullRequestKey(client, "1", 1);
        var a = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Provider = ScmProvider.AzureDevOps,
            HostBaseUrl = "https://dev.azure.com/orgA"
        };
        var b = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Provider = ScmProvider.AzureDevOps,
            HostBaseUrl = "https://dev.azure.com/orgB"
        };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var miss = new CodeInsightMissRecord(
            "1", "src/Service.cs", 1, "human concern", true, true, true, .9, "judge-v1", true, ConnectionId: a.Id, SourceFingerprint: "a");
        Assert.True(await store.RecordMissAsync(key, miss));
        Assert.True(
            await store.RecordMissAsync(
                key, miss with
                {
                    ConnectionId = b.Id,
                    SourceFingerprint = "b"
                }));
        Assert.False(await store.RecordMissAsync(key, miss));
        Assert.Null(await store.GetObservationAsync(key, "1"));
        Assert.Equal("a", (await store.GetObservationAsync(key, "1", connectionId: a.Id))!.SourceFingerprint);
        Assert.Equal("b", (await store.GetObservationAsync(key, "1", connectionId: b.Id))!.SourceFingerprint);
        Assert.True(
            await store.RejudgeMissAsync(
                key, miss with
                {
                    ConnectionId = b.Id,
                    WasActedOn = false,
                    SourceFingerprint = "updated-b"
                }));
        Assert.True((await db.CodeInsightMisses.SingleAsync(row => row.ProviderScope.EndsWith("orgA"))).WasActedOn);
        Assert.False((await db.CodeInsightMisses.SingleAsync(row => row.ProviderScope.EndsWith("orgB"))).WasActedOn);
    }

    [Fact]
    public async Task PublicationReplayEnrichesVerifiedDuplicatesAndChangedReceiptsWithoutErasingKnownEvidence()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected");
        var store = new CodeInsightFindingStore(db, codec);
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        var job = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var input = new CodeInsightFindingSnapshot(
            0, "src/Service.cs", 1, CommentSeverity.Warning, "finding", null, null, null, false, null, null, "first-thread", "first-comment",
            PublicationState: CodeInsightPublicationState.Published);
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [input]);
        var finding = await db.CodeInsightFindings.SingleAsync();
        var stamp = finding.PerformanceEvidenceUpdatedAt;
        var enriched = input with
        {
            ProviderCommentId = "new-comment",
            DuplicateState = CodeInsightDuplicateState.ConfirmedDuplicate,
            DuplicateVerificationSource = "human-audit",
            DuplicateVerificationConfidence = .9,
            DuplicateVerifiedAt = now.AddMinutes(1),
            DuplicateOfPublicationId = "known-publication"
        };
        await store.MaterialiseFindingsAsync(key, job, "revision", now, [enriched]);
        Assert.Equal(CodeInsightDuplicateState.ConfirmedDuplicate, finding.DuplicateState);
        Assert.Equal("human-audit", finding.DuplicateVerificationSource);
        Assert.True(finding.PerformanceEvidenceUpdatedAt > stamp);
        stamp = finding.PerformanceEvidenceUpdatedAt;
        await store.MaterialiseFindingsAsync(
            key, job, "revision", now,
            [
                input with
                {
                    ProviderThreadId = null,
                    ProviderCommentId = null,
                    PublicationState = CodeInsightPublicationState.Unknown
                }
            ]);
        Assert.Equal("new-comment", finding.ProviderCommentId);
        Assert.Equal(CodeInsightDuplicateState.ConfirmedDuplicate, finding.DuplicateState);
        Assert.Equal(stamp, finding.PerformanceEvidenceUpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CurrentDispositionAcknowledgesOnlyCompletedJudgmentOrTheExistingAttemptCap(int successfulAttempt)
    {
        await using var db = Db();
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1);
        db.CodeInsightPullRequests.Add(new() { Id = Guid.NewGuid(), ClientId = key.ClientId, RepositoryId = key.RepositoryId, PullRequestId = 1 });
        await db.SaveChangesAsync();
        var finding = Finding((await db.CodeInsightPullRequests.SingleAsync()).Id, "scope-a");
        db.Add(finding);
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("finding");
        var findings = new CodeInsightFindingStore(db, codec);
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(key.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var classifier = Substitute.For<IDisregardedFindingClassifier>();
        classifier.ClassifierVersion.Returns("judge-v1");
        var attempts = 0;
        classifier.JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
            ++attempts == successfulAttempt ? new DisregardedFindingJudgement(true, .9, "incorrect") : null);
        var service = new CodeInsightDispositionService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.CompatibilityCodec,
            findings, findings, classifier, gate, NullLogger<CodeInsightDispositionService>.Instance,
            performanceEvidence: new CodeInsightPerformanceEvidenceStore(db));
        var checkpoint = new MeisterDev.ProPR.Domain.Entities.DiscussionThreadProgress();
        var stop = successfulAttempt == 0 ? 3 : successfulAttempt;
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= stop; attempt++)
        {
            Assert.True(checkpoint.Needs("disposition", "same-content", "same-version"));
            var ack = await service.ObserveCurrentAsync(
                new(
                    key.ClientId, "https://dev.azure.com/org", "project", "repo", 1,
                    "1", "file.cs", null, "Human: this was intentional", now.AddSeconds(attempt), ThreadResolutionIntent.ClaimsFix,
                    ThreadAnchorCodeChange.Unchanged, "Fixed", "scope-a"));
            Assert.Equal(attempt == stop, ack.Retained);
            if (ack.Retained)
            {
                checkpoint.Acknowledge("disposition", "same-content", "same-version");
            }
        }

        Assert.False(checkpoint.Needs("disposition", "same-content", "same-version"));
        Assert.Equal(stop, attempts);
        var initial = await db.CodeInsightFindingDispositions.SingleAsync();
        Assert.Equal(CodeInsightDisposition.Dismissed, initial.Disposition);
        Assert.Null(initial.ClassifierConfidence);
    }

    [Fact]
    public async Task RemovingAllVisibleCommentsRemovesAnExistingHumanMissWithoutDeletingTheThread()
    {
        await using var db = Db();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        var store = new CodeInsightFindingStore(db, codec);
        var classifier = Substitute.For<IHumanMissClassifier>();
        classifier.ClassifierVersion.Returns("judge-v1");
        classifier.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanMissJudgement(true, true, true, .9, "substantive"));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new CodeInsightMissHarvester(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.CompatibilityCodec,
            store, store, classifier, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var evt = new ThreadUpdatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "repo", 1, "thread-1", "file.cs", 1,
            "Fixed", DateTimeOffset.UtcNow, [new("1", "alice", false, DateTimeOffset.UtcNow, "This can lose a record.")], ObservedAt: DateTimeOffset.UtcNow,
            ProviderScope: "scope-a");
        Assert.True((await service.ObserveCurrentAsync(evt)).Retained);
        Assert.True((await db.CodeInsightMisses.SingleAsync()).IsSubstantive);
        Assert.True((await service.ObserveCurrentAsync(evt with { Comments = [], ObservedAt = evt.ObservedAt!.Value.AddSeconds(1) })).Retained);
        Assert.True((await db.CodeInsightMisses.SingleAsync()).ExcludedAsOwnFinding);
        Assert.True((await db.CodeInsightThreadEligibility.SingleAsync()).ExcludedFromHumanMisses);
        Assert.False(evt.IsDeleted);
        await classifier.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletionWithCapturedConnectionPreventsOlderHumanEvidenceInItsResolvedScope()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var connection = Guid.NewGuid();
        const string host = "https://dev.azure.com/org";
        var scope = ProviderSourceIdentity.FromConfiguredHost(ScmProvider.AzureDevOps, host).Value;
        db.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = connection, ClientId = client, Provider = ScmProvider.AzureDevOps, HostBaseUrl = host,
            });
        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        var store = new CodeInsightFindingStore(db, codec);
        var classifier = Substitute.For<IHumanMissClassifier>();
        classifier.ClassifierVersion.Returns("judge");
        classifier.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanMissJudgement(true, true, true, .9, "substantive"));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(client, Arg.Any<CancellationToken>()).Returns(true);
        var service = new CodeInsightMissHarvester(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.CompatibilityCodec,
            store, store, classifier, gate, TestPostedCommentComposer.Default, NullLogger<CodeInsightMissHarvester>.Instance);
        var now = DateTimeOffset.UtcNow;
        var human = new ThreadUpdatedEvent(
            client, connection, "repo", 42, "thread", "file.cs", 1,
            "Fixed", now, [new("comment", "alice", false, now, "This can lose a record.")], ObservedAt: now.AddSeconds(-1));
        Assert.True((await service.ObserveCurrentAsync(human with { IsDeleted = true, Comments = [], ObservedAt = now })).Retained);
        await service.ObserveCurrentAsync(human);
        var eligibility = Assert.Single(await db.CodeInsightThreadEligibility.ToListAsync());
        Assert.Equal(scope, eligibility.ProviderScope);
        Assert.True(eligibility.ExcludedFromHumanMisses);
        Assert.Empty(await db.CodeInsightMisses.ToListAsync());
        await classifier.DidNotReceive().JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CodeInsightFinding Finding(Guid pr, string scope) => new()
    {
        Id = Guid.NewGuid(),
        CodeInsightPullRequestId = pr,
        JobId = Guid.NewGuid(),
        RevisionKey = Guid.NewGuid().ToString(),
        ProviderScope = scope,
        ProviderThreadId = "1",
        ProviderCommentId = "1"
    };

    private static CodeInsightDispositionRecord Outcome() => new(
        CodeInsightDisposition.Acknowledged, ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unknown, null, null);
}
