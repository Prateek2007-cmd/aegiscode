// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.Tests.Metrics;

namespace MeisterDev.ProPR.CodeInsights.Tests.Misses;

public sealed class ModelConcernObserverTests
{
    [Fact]
    public async Task EquivalentMissedConcernsShareOneCanonicalIdentityWithoutInventingModelDetection()
    {
        await using var db = CreateDb();
        var truth = Substitute.For<IHumanMissClassifier>();
        truth.ClassifierVersion.Returns("truth");
        truth.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanMissJudgement(true, true, true, .9, "Confirmed", true, true));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var matcher = Substitute.For<IHumanConcernModelMatcher>();
        matcher.ClassifierVersion.Returns("match");
        matcher.MatchAsync(Arg.Any<HumanConcernModelMatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<HumanConcernModelMatchRequest>();
            return new HumanConcernModelMatchResult(HumanConcernMatchState.Match, [], [request.OtherConcerns[0].Id], [], true);
        });
        var reader = Substitute.For<IModelReviewEvidenceReader>();
        reader.ReadAsync(Arg.Any<IReadOnlyList<ModelReviewSource>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ModelOpportunityEvaluatorTests.Snapshot(ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A")])));
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        var observer = new CodeInsightModelConcernObserver(
            db, truth, matcher, codec, gate, NullLogger<CodeInsightModelConcernObserver>.Instance, evidenceReader: reader);
        var first = Observation(false, true) with
        {
            ClientId = ModelOpportunityEvaluatorTests.Source.ClientId,
            ConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId!.Value, RepositoryId = "repo", PullRequestId = 42,
            ProviderScope = ModelOpportunityEvaluatorTests.Source.ProviderScope, ConcernAnchor = ModelOpportunityEvaluatorTests.Anchor
        };
        Assert.True((await observer.ObserveAsync(first)).ConcernRetained);
        Assert.True(
            (await observer.ObserveAsync(first with { ThreadId = "alias-thread", ConcernAnchor = first.ConcernAnchor! with { RootCommentId = "alias-root" } }))
            .ConcernRetained);
        var concerns = await db.CodeInsightHumanConcerns.ToListAsync();
        Assert.Equal(2, concerns.Count);
        Assert.Single(concerns.Select(concern => concern.CanonicalConcernId).Distinct());
        Assert.All(
            db.CodeInsightModelConcernEvidence, decision =>
            {
                Assert.Equal("missed", decision.Decision);
                Assert.Empty(decision.CandidateIds);
            });
    }

    [Fact]
    public async Task IndependentModelDecisionsAndEnumerationAreDurableAndIdempotent()
    {
        await using var db = CreateDb();
        var truth = Substitute.For<IHumanMissClassifier>();
        truth.ClassifierVersion.Returns("truth");
        truth.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanMissJudgement(true, true, true, .9, "confirmed", true, true));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var matcher = Substitute.For<IHumanConcernModelMatcher>();
        matcher.ClassifierVersion.Returns("match");
        matcher.MatchAsync(Arg.Any<HumanConcernModelMatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<HumanConcernModelMatchRequest>();
            var ids = request.Candidates.Select(candidate => candidate.Id).ToArray();
            return new HumanConcernModelMatchResult(
                ids.Length == 0 ? HumanConcernMatchState.NoMatch : HumanConcernMatchState.Match, ids, [], ids, true, Confidence: .9);
        });
        var reader = Substitute.For<IModelReviewEvidenceReader>();
        var proof = ModelOpportunityEvaluatorTests.Proof(
        [
            ModelOpportunityEvaluatorTests.Pass("A"), ModelOpportunityEvaluatorTests.Pass("B") with
            {
                Candidates = [new("B-finding", "a.cs", 12, "Null input throws")]
            }
        ]);
        reader.ReadAsync(Arg.Any<IReadOnlyList<ModelReviewSource>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ModelOpportunityEvaluatorTests.Snapshot(proof));
        var secrets = Substitute.For<ISecretProtectionCodec>();
        secrets.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        secrets.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
        var observer = new CodeInsightModelConcernObserver(
            db, truth, matcher, secrets, gate, NullLogger<CodeInsightModelConcernObserver>.Instance,
            evidenceReader: reader);
        var evt = Observation(true, true) with
        {
            ClientId = ModelOpportunityEvaluatorTests.Source.ClientId,
            ConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId!.Value, RepositoryId = "repo", PullRequestId = 42,
            ProviderScope = ModelOpportunityEvaluatorTests.Source.ProviderScope, ConcernAnchor = ModelOpportunityEvaluatorTests.Anchor
        };
        await observer.ObserveAsync(evt);
        var source = Assert.Single(db.CodeInsightHumanConcerns);
        Assert.Equal("missed", Assert.Single(db.CodeInsightModelConcernEvidence.Where(row => row.ModelId == "A")).Decision);
        Assert.Equal("detected", Assert.Single(db.CodeInsightModelConcernEvidence.Where(row => row.ModelId == "B")).Decision);
        Assert.True(
            await observer.CompleteEnumerationAsync(
                new(evt.ClientId, evt.RepositoryId, evt.PullRequestId), evt.ProviderScope,
                evt.ConnectionId, evt.ObservedAt!.Value, true, [source.NativeIdentity]));
        var coverage = Assert.Single(db.CodeInsightHarvestCoverage);
        Assert.True(coverage.ModelConcernEnumerationComplete);
        Assert.Equal(evt.ConnectionId, coverage.ModelConcernConnectionId);
        await observer.ObserveAsync(evt);
        Assert.Equal(2, db.CodeInsightModelConcernEvidence.Count());
        await truth.Received(1).JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmedReviewerOwnedConcernIsRetainedBeforeAnyLegacyOverlapExclusion()
    {
        await using var db = CreateDb();
        var truth = Substitute.For<IHumanMissClassifier>();
        truth.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanMissJudgement(true, true, true, 0.9, "Human verified the issue", true, true));
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var secrets = Substitute.For<ISecretProtectionCodec>();
        secrets.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns("protected-discussion");
        var observer = new CodeInsightModelConcernObserver(
            db, truth, Substitute.For<IHumanConcernModelMatcher>(), secrets, gate,
            NullLogger<CodeInsightModelConcernObserver>.Instance);
        var evt = Observation(reviewerRoot: true, humanReply: true);
        var ack = await observer.ObserveAsync(evt);
        Assert.True(ack.ConcernRetained);
        var retained = Assert.Single(db.CodeInsightHumanConcerns);
        Assert.True(retained.IsSubstantive);
        Assert.True(retained.WasActedOn);
        Assert.Equal(evt.ConcernAnchor, retained.Anchor);
        Assert.Equal("protected-discussion", retained.EncryptedDiscussion);
        await truth.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => request.IndependentConcern && request.ReviewerOwnedRoot), Arg.Any<CancellationToken>());
        Assert.Empty(db.CodeInsightMisses);
    }

    [Fact]
    public async Task BotOnlyFindingsDoNotCreateHumanGroundTruth()
    {
        await using var db = CreateDb();
        var truth = Substitute.For<IHumanMissClassifier>();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var observer = new CodeInsightModelConcernObserver(
            db, truth, Substitute.For<IHumanConcernModelMatcher>(), Substitute.For<ISecretProtectionCodec>(), gate,
            NullLogger<CodeInsightModelConcernObserver>.Instance);
        var ack = await observer.ObserveAsync(Observation(reviewerRoot: true, humanReply: false));
        Assert.True(ack.ConcernRetained);
        Assert.Empty(db.CodeInsightHumanConcerns);
        await truth.DidNotReceiveWithAnyArgs().JudgeAsync(default!, default);
    }

    private static MeisterProPRDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ThreadUpdatedEvent Observation(bool reviewerRoot, bool humanReply)
    {
        var origin = DateTimeOffset.UtcNow.AddDays(-1);
        var comments = new List<ThreadUpdatedComment> { new("1", reviewerRoot ? "bot" : "human", reviewerRoot, origin, "The null input throws.") };
        if (humanReply)
        {
            comments.Add(new("2", "human", false, origin.AddHours(1), "Confirmed and fixed the null input."));
        }

        return new(
            Guid.NewGuid(), Guid.NewGuid(), "repo", 1, "thread", "f.cs", 12, "Fixed", origin.AddHours(1), comments,
            DateTimeOffset.UtcNow, "GitHub:https://github.com", ConcernAnchor: new()
            {
                RootCommentId = "1", OriginAt = origin,
                FilePath = "f.cs", Side = "RIGHT", StartLine = 12, EndLine = 12, CommitSha = "head", HeadSha = "head", OriginalScopeProven = true
            });
    }
}
