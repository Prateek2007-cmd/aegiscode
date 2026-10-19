// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Classification;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ModelEvidenceReviewRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AClosedFileCannotCertifyNonDetectionUnderAnIncompleteGlobalInventory(bool absent, bool sourceScoring)
    {
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture();
        fixture.Proof = fixture.Proof with
        {
            Passes = [ModelOpportunityEvaluatorTests.Pass("A") with { Candidates = [new("positive", "a.cs", 12, "retained candidate")] }]
        };
        fixture.Concern("verified concern", false);
        var snapshot = ModelOpportunityEvaluatorTests.Snapshot(fixture.Proof);
        snapshot = snapshot with
        {
            Executions =
            [
                snapshot.Executions[0] with
                {
                    Manifest = absent ? null : snapshot.Executions[0].Manifest! with { InventoryComplete = false }
                }
            ]
        };
        var opportunity = ModelOpportunityEvaluator.Assess(
            snapshot, ModelOpportunityEvaluatorTests.Source, ModelOpportunityEvaluatorTests.Anchor, "A", "logical-A");
        Assert.Single(opportunity.Candidates);
        if (!sourceScoring)
        {
            Assert.False(opportunity.NegativeComplete);
            return;
        }

        var original = fixture.Snapshot();
        var context = new ReviewerPerformanceModelEvidence(
            snapshot, original.Aggregates, fixture.Concerns, fixture.Decisions,
            [
                new()
                {
                    CodeInsightPullRequestId = fixture.AggregateId, ProviderScope = fixture.Key.ProviderScope, ModelConcernEnumerationComplete = true,
                    ModelConcernConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId,
                    ModelConcernObservedAt = ModelOpportunityEvaluatorTests.Origin.AddHours(1)
                }
            ]);
        var scope = fixture.Scope("A");
        Assert.Contains("model-input-scope-unavailable", context.Assess(fixture.Key, scope, fixture.To).Reasons);
        var view = ReviewerPerformanceViewBuilder.Build(
            new() { Views = [scope] }, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId],
            original with { ModelEvidence = context }, new(80, 1), ModelOpportunityEvaluatorTests.Origin.AddHours(2));
        var point = Assert.Single(view.Series).Points.Last();
        Assert.Equal(0, point.Coverage!.CoveredPullRequests);
        Assert.Null(point.Score.Summary.F1);
    }

    [Fact]
    public async Task ThreadLocalRootIdsCannotReplaceOrDeleteAnotherNativeConcern()
    {
        await using var harness = new ConcernHarness();
        var first = harness.Observation("101", "First issue") with { ConcernAnchor = ModelOpportunityEvaluatorTests.Anchor with { RootCommentId = "1" } };
        var second = harness.Observation("102", "Second issue") with { ConcernAnchor = ModelOpportunityEvaluatorTests.Anchor with { RootCommentId = "1" } };
        await harness.Observer.ObserveAsync(first);
        await harness.Observer.ObserveAsync(second);
        Assert.Equal(2, await harness.Db.CodeInsightHumanConcerns.CountAsync());
        Assert.NotEqual(ModelConcernIdentity.Of(first), ModelConcernIdentity.Of(second));
        await harness.Observer.ObserveAsync(
            second with
            {
                ObservedAt = second.ObservedAt!.Value.AddMinutes(1),
                Comments = [second.Comments[0] with { IsAiAuthored = true }]
            });
        var retained = await harness.Db.CodeInsightHumanConcerns.ToListAsync();
        Assert.False(retained.Single(row => row.ProviderThreadId == "101").IsDeleted);
        Assert.True(retained.Single(row => row.ProviderThreadId == "102").IsDeleted);
    }

    [Fact]
    public async Task NewIncompleteEnumerationRevokesOnlyItsCoverageAndFencesOlderSuccess()
    {
        await using var harness = new ConcernHarness();
        var observation = harness.Observation("thread", "Concern");
        await harness.Observer.ObserveAsync(observation);
        var key = new CodeInsightPullRequestKey(observation.ClientId, observation.RepositoryId, observation.PullRequestId);
        var initial = observation.ObservedAt!.Value;
        await harness.Observer.CompleteEnumerationAsync(
            key, observation.ProviderScope!, observation.ConnectionId, initial, true,
            [ModelConcernIdentity.Of(observation)!]);
        var coverage = await harness.Db.CodeInsightHarvestCoverage.SingleAsync();
        coverage.EnumerationComplete = true;
        coverage.AllHumanThreadsResolved = true;
        await harness.Db.SaveChangesAsync();
        Assert.False(
            await harness.Observer.CompleteEnumerationAsync(
                key, observation.ProviderScope!, observation.ConnectionId,
                initial.AddHours(2), false, []));
        Assert.False(coverage.ModelConcernEnumerationComplete);
        Assert.Equal(initial.AddHours(2), coverage.ModelConcernObservedAt);
        Assert.True(coverage.EnumerationComplete);
        Assert.True(coverage.AllHumanThreadsResolved);
        Assert.False((await harness.Db.CodeInsightHumanConcerns.SingleAsync()).IsDeleted);
        Assert.Equal(initial, (await harness.Db.CodeInsightHumanConcerns.SingleAsync()).ObservedAt);
        await harness.Observer.CompleteEnumerationAsync(key, observation.ProviderScope!, observation.ConnectionId, initial.AddHours(1), true, []);
        Assert.False(coverage.ModelConcernEnumerationComplete);
        Assert.False((await harness.Db.CodeInsightHumanConcerns.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task PermanentMatcherLimitsAreRetainedWithoutRequestingAnotherUnchangedAttempt()
    {
        await using var harness = new ConcernHarness();
        harness.Snapshot = ModelOpportunityEvaluatorTests.Snapshot(
            ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A") with { Candidates = [new("finding", "a.cs", 12, "candidate")] }]));
        harness.Matcher.MatchAsync(Arg.Any<HumanConcernModelMatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new HumanConcernModelMatchResult(HumanConcernMatchState.Unknown, [], [], [], false, "model-concern-input-limit-exceeded"));
        var observation = harness.Observation("thread", "Concern");
        var acknowledgment = await harness.Observer.ObserveAsync(observation);
        Assert.True(acknowledgment.ModelsCurrent);
        var retained = await harness.Db.CodeInsightModelConcernEvidence.SingleAsync();
        Assert.Equal("model-concern-input-limit-exceeded", retained.UnavailableReason);
        Assert.Equal(0, retained.FailedAttempts);
    }

    [Fact]
    public async Task ChangedConcernInvalidatesOutgoingAndInverseEquivalenceWithoutSplittingUnchangedProof()
    {
        await using var harness = new ConcernHarness();
        harness.Matcher.MatchAsync(Arg.Any<HumanConcernModelMatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<HumanConcernModelMatchRequest>();
            var target = request.Discussion.Contains("alias-two", StringComparison.Ordinal) ? "alias-one"
                : request.Discussion.Contains("alias-one", StringComparison.Ordinal) ? "original" : null;
            var equivalent = target is null
                ? null
                : request.OtherConcerns?.FirstOrDefault(other => other.Discussion.Contains(target, StringComparison.Ordinal));
            return equivalent is null
                ? new HumanConcernModelMatchResult(HumanConcernMatchState.NoMatch, [], [], request.Candidates.Select(candidate => candidate.Id).ToArray(), true)
                : new HumanConcernModelMatchResult(HumanConcernMatchState.Match, [], [equivalent.Id], [], true);
        });
        var first = harness.Observation("original", "original");
        var second = harness.Observation("alias-one", "alias-one");
        var third = harness.Observation("alias-two", "alias-two");
        await harness.Observer.ObserveAsync(first);
        await harness.Observer.ObserveAsync(second);
        await harness.Observer.ObserveAsync(third);
        Assert.Single((await harness.Db.CodeInsightHumanConcerns.ToListAsync()).Select(row => row.CanonicalConcernId).Distinct());
        await harness.Observer.ObserveAsync(
            first with
            {
                ObservedAt = first.ObservedAt!.Value.AddMinutes(1), Comments = [first.Comments[0] with { Text = "a distinct edited issue" }]
            });
        await harness.Observer.ObserveAsync(second);
        var rows = await harness.Db.CodeInsightHumanConcerns.ToDictionaryAsync(row => row.ProviderThreadId);
        Assert.NotEqual(rows["original"].CanonicalConcernId, rows["alias-one"].CanonicalConcernId);
        Assert.Equal(rows["alias-one"].CanonicalConcernId, rows["alias-two"].CanonicalConcernId);
        Assert.DoesNotContain(
            await harness.Db.CodeInsightModelConcernEvidence.ToListAsync(),
            decision => decision.EquivalentConcernIds.Contains(rows["original"].Id.ToString(), StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ModelGenerationDisposition.Failed)]
    [InlineData(ModelGenerationDisposition.InvalidOutput)]
    [InlineData(ModelGenerationDisposition.Cancelled)]
    public void IncompleteEarlierSameModelPassCannotDisappearBesideCertifiedZero(ModelGenerationDisposition disposition)
    {
        var failed = ModelOpportunityEvaluatorTests.Pass("A") with
        {
            PassKey = "incomplete", Disposition = disposition, CandidateSetComplete = false, AcceptedAt = null,
            CompletedAt = disposition == ModelGenerationDisposition.Cancelled ? null : ModelOpportunityEvaluatorTests.Origin.AddHours(-1)
        };
        var proof = ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A"), failed]);
        Assert.False(
            ModelOpportunityEvaluator.Assess(
                ModelOpportunityEvaluatorTests.Snapshot(proof), ModelOpportunityEvaluatorTests.Source,
                ModelOpportunityEvaluatorTests.Anchor, "A", "logical-A").NegativeComplete);
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture { Proof = proof };
        Assert.Contains("model-generation-incomplete", fixture.Build().Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
    }

    [Fact]
    public async Task OversizedCanonicalGroupCannotKeepScoringAfterItsOriginalMemberChanges()
    {
        await using var harness = new ConcernHarness();
        var original = harness.Observation("original", "original issue");
        await harness.Observer.ObserveAsync(original);
        var member = await harness.Db.CodeInsightHumanConcerns.SingleAsync();
        harness.Db.CodeInsightHumanConcerns.AddRange(
            Enumerable.Range(1, 1000).Select(index => new CodeInsightHumanConcern
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = member.CodeInsightPullRequestId, CanonicalConcernId = member.Id,
                NativeIdentity = index.ToString("D64"), ProviderThreadId = "alias-" + index, ProviderScope = member.ProviderScope,
                ConnectionId = member.ConnectionId, Anchor = member.Anchor, SourceFingerprint = "alias-" + index,
                HarvestedAt = member.HarvestedAt, ObservedAt = member.ObservedAt, IsSubstantive = true, IsInScope = true,
                IsSingleConcern = true, IsHumanConfirmed = true, ThreadResolved = true
            }));
        await harness.Db.SaveChangesAsync();
        await harness.Observer.ObserveAsync(
            original with
            {
                ObservedAt = original.ObservedAt!.Value.AddMinutes(1), Comments = [original.Comments[0] with { Text = "a new unrelated concern" }]
            });
        var aggregate = await harness.Db.CodeInsightPullRequests.SingleAsync();
        var context = new ReviewerPerformanceModelEvidence(
            harness.Snapshot,
            new Dictionary<Guid, CodeInsightPullRequest> { [aggregate.Id] = aggregate },
            await harness.Db.CodeInsightHumanConcerns.ToListAsync(), await harness.Db.CodeInsightModelConcernEvidence.ToListAsync(),
            [
                new()
                {
                    CodeInsightPullRequestId = aggregate.Id, ProviderScope = member.ProviderScope, ModelConcernEnumerationComplete = true,
                    ModelConcernConnectionId = member.ConnectionId, ModelConcernObservedAt = original.ObservedAt
                }
            ]);
        var scope = new ReviewerPerformanceViewQuery { Models = [ReviewerPerformanceModelEvidence.ModelKey("A", "logical-A")] };
        var reasons = context.Assess(new(aggregate.Id, member.ProviderScope), scope, new(2026, 10, 10)).Reasons;
        Assert.Contains("model-concern-equivalence-limit-exceeded", reasons);
        Assert.Equal("none", context.RecoveryAction(new(aggregate.Id, member.ProviderScope), scope, reasons));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvaluationBudgetIncludesManifestAndPublicationOnlyModels(bool publication)
    {
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture
            { Proof = ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A")]) };
        fixture.Concern("concern", false);
        var snapshot = ModelOpportunityEvaluatorTests.Snapshot(fixture.Proof);
        var cells = publication
            ? new[]
            {
                new ReviewerPerformanceDailyCount
                {
                    CodeInsightPullRequestId = fixture.AggregateId, ClientId = ModelOpportunityEvaluatorTests.Source.ClientId,
                    RepositoryId = "repo", PullRequestId = 42, ProviderScope = fixture.Key.ProviderScope, ModelId = "B", LogicalModelName = "logical-B",
                    Outcome = "positive", Count = 1
                }
            }
            : [];
        if (!publication)
        {
            snapshot = snapshot with
            {
                Executions =
                [
                    snapshot.Executions[0] with
                    {
                        Manifest = snapshot.Executions[0].Manifest! with { AllowedModels = [new("B", "logical-B", null, null)] }
                    }
                ]
            };
        }

        Assert.Throws<ArgumentException>(() => new ReviewerPerformanceModelEvidence(
            snapshot,
            new Dictionary<Guid, CodeInsightPullRequest>
            {
                [fixture.AggregateId] = new()
                    { Id = fixture.AggregateId, ClientId = ModelOpportunityEvaluatorTests.Source.ClientId, RepositoryId = "repo", PullRequestId = 42 }
            },
            fixture.Concerns, fixture.Decisions, [], cells, new(Evaluations: 1)));
    }

    [Fact]
    public void PermanentMatcherReasonRemainsNonretryableInSourceQualification()
    {
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture();
        fixture.Concern("concern", false).RetainedSourceComplete = true;
        var decision = fixture.Decisions.Single(row => row.ModelId == "A");
        decision.Decision = "unknown";
        decision.UnavailableReason = "model-concern-input-limit-exceeded";
        var context = fixture.Build();
        var qualification = context.Assess(fixture.Key, fixture.Scope("A"), fixture.To);
        Assert.Contains("model-concern-input-limit-exceeded", qualification.Reasons);
        Assert.Equal("none", context.RecoveryAction(fixture.Key, fixture.Scope("A"), qualification.Reasons));
    }

    [Theory]
    [InlineData("logical")]
    [InlineData("configured")]
    [InlineData("ambiguous")]
    public void UnresolvedAssignmentCannotProveAnotherFilesModelNonassignment(string identity)
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = ModelOpportunityEvaluatorTests.Pass("A") with { ConfiguredModelId = aId, LogicalModelName = identity == "logical" ? "logical-A" : "" };
        var b = ModelOpportunityEvaluatorTests.Pass("B") with { ConfiguredModelId = bId, LogicalModelName = identity == "logical" ? "logical-B" : "" };
        var known = ModelOpportunityEvaluatorTests.Proof([a, b]);
        var uncertain = ModelOpportunityEvaluatorTests.Proof(
            [
                b with
                {
                    ModelId = "", ConfiguredModelId = identity == "ambiguous" ? Guid.NewGuid() : bId,
                    Disposition = ModelGenerationDisposition.Unknown, CandidateSetComplete = false, AcceptedAt = null, CompletedAt = null
                }
            ]) with
            {
                FilePath = "b.cs"
            };
        var snapshot = ModelOpportunityEvaluatorTests.Snapshot(known, uncertain);
        snapshot = snapshot with
        {
            Executions =
            [
                snapshot.Executions[0] with
                {
                    Manifest = snapshot.Executions[0].Manifest! with { Files = ["a.cs", "b.cs"] }
                }
            ]
        };
        var anchor = ModelOpportunityEvaluatorTests.Anchor with { FilePath = "b.cs" };
        Assert.False(ModelOpportunityEvaluator.Assess(snapshot, ModelOpportunityEvaluatorTests.Source, anchor, "B", b.LogicalModelName).NotApplicable);
        Assert.False(ModelOpportunityEvaluator.Assess(snapshot, ModelOpportunityEvaluatorTests.Source, anchor, "B", b.LogicalModelName).NegativeComplete);
        Assert.Equal(
            identity != "ambiguous",
            ModelOpportunityEvaluator.Assess(snapshot, ModelOpportunityEvaluatorTests.Source, anchor, "A", a.LogicalModelName).NotApplicable);
    }

    [Fact]
    public void UnresolvedSameLogicalCohortExcludesItWhileKnownUnrelatedAStillScores()
    {
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture();
        fixture.Concern("verified miss", false);
        var uncertain = fixture.Proof with
        {
            FilePath = "b.cs", Passes =
            [
                ModelOpportunityEvaluatorTests.Pass("B") with
                {
                    ModelId = "", Disposition = ModelGenerationDisposition.Unknown, CandidateSetComplete = false, AcceptedAt = null, CompletedAt = null
                }
            ]
        };
        var execution = ModelOpportunityEvaluatorTests.Snapshot(fixture.Proof, uncertain);
        var original = fixture.Snapshot();
        var model = new ReviewerPerformanceModelEvidence(
            execution, original.Aggregates, fixture.Concerns, fixture.Decisions,
            [
                new()
                {
                    CodeInsightPullRequestId = fixture.AggregateId, ProviderScope = fixture.Key.ProviderScope, ModelConcernEnumerationComplete = true,
                    ModelConcernConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId,
                    ModelConcernObservedAt = ModelOpportunityEvaluatorTests.Origin.AddHours(1)
                }
            ]);
        var source = original with { ModelEvidence = model };
        foreach (var selected in new[] { "A", "B" })
        {
            var scope = fixture.Scope(selected);
            var view = ReviewerPerformanceViewBuilder.Build(
                new() { Views = [scope] }, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId], source,
                new(80, 1), ModelOpportunityEvaluatorTests.Origin.AddHours(2));
            var point = Assert.Single(view.Series).Points.Last();
            Assert.Equal(selected == "A" ? 1 : 0, point.Coverage!.CoveredPullRequests);
            if (selected == "A")
            {
                Assert.Equal(1, point.Score.Counts.ActedMisses);
                Assert.Equal(0, point.Score.Summary.F1!.Median);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OversizedKnownBCandidatesPreserveOnlyPositivelyClosedAScoring(bool inventoryComplete)
    {
        var file = new ReviewFileResult(Guid.Empty, "a.cs");
        var proof = ModelOpportunityEvaluatorTests.Proof(
            [
                ModelOpportunityEvaluatorTests.Pass("A"), ModelOpportunityEvaluatorTests.Pass("B") with
                {
                    Disposition = ModelGenerationDisposition.Unknown, CandidateSetComplete = false, AcceptedAt = null
                }
            ]) with
            {
                InventoryComplete = inventoryComplete
            };
        file.RecordModelEvidence(proof);
        file.RecordModelEvidence(
            proof with
            {
                Passes =
                [
                    ModelOpportunityEvaluatorTests.Pass("B") with
                    {
                        Candidates = Enumerable.Range(0, 700).Select(index => new ModelReviewCandidate("b-" + index, "a.cs", 12, new string('x', 400)))
                            .ToArray()
                    }
                ]
            });
        var fixture = new ReviewerPerformanceModelEvidenceTests.Fixture { Proof = file.ModelEvidence! };
        fixture.Concern("verified miss", false);
        var scope = fixture.Scope("A");
        var view = ReviewerPerformanceViewBuilder.Build(
            new() { Views = [scope] }, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId],
            fixture.Snapshot(), new(80, 1), ModelOpportunityEvaluatorTests.Origin.AddHours(2));
        var point = Assert.Single(view.Series).Points.Last();
        Assert.Equal(inventoryComplete ? 1 : 0, point.Coverage!.CoveredPullRequests);
        Assert.Equal(inventoryComplete, file.ModelEvidence!.InventoryComplete);
        Assert.DoesNotContain(file.ModelEvidence.Passes, pass => pass.ModelId == "B" && pass.IsCertified);
        if (inventoryComplete)
        {
            Assert.Equal(1, point.Score.Counts.ActedMisses);
            Assert.Equal(0, point.Score.Summary.F1!.Median);
        }
        else
        {
            Assert.Null(point.Score.Summary.F1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalenceInFirstChunkDoesNotHideLaterCandidateDetection(bool detected)
    {
        var chat = Substitute.For<IChatClient>();
        var calls = 0;
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var input = call.Arg<IEnumerable<ChatMessage>>().Last().Text;
            using var json = JsonDocument.Parse(input!);
            var ids = json.RootElement.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("Id").GetString()!).ToArray();
            var first = ++calls == 1;
            return new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant, JsonSerializer.Serialize(
                        new
                        {
                            state = first || detected ? "Match" : "NoMatch", matchedFindingIds = !first && detected ? ids : [],
                            equivalentConcernIds = first ? new[] { "other" } : [], evaluatedCandidateIds = ids, confidence = .9
                        })));
        });
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(runtime);
        var matcher = new AiHumanConcernModelMatcher(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanConcernModelMatcher>.Instance);
        var result = await matcher.MatchAsync(
            new(
                Guid.NewGuid(), Guid.NewGuid(), "concern",
                Enumerable.Range(0, 17).Select(index => new ModelReviewCandidate($"c{index:D2}", "a.cs", 12, "candidate")).ToArray(),
                [new("other", "same concern")]));
        Assert.Equal(2, calls);
        Assert.Contains("other", result.EquivalentConcernIds);
        Assert.Equal(17, result.EvaluatedCandidateIds.Count);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(detected ? new[] { "c16" } : [], result.MatchedFindingIds);
    }

    private sealed class ConcernHarness : IAsyncDisposable
    {
        internal MeisterProPRDbContext Db { get; } =
            new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        internal IHumanConcernModelMatcher Matcher { get; } = Substitute.For<IHumanConcernModelMatcher>();

        internal ModelReviewEvidenceSnapshot Snapshot { get; set; } =
            ModelOpportunityEvaluatorTests.Snapshot(ModelOpportunityEvaluatorTests.Proof([ModelOpportunityEvaluatorTests.Pass("A")]));

        internal CodeInsightModelConcernObserver Observer { get; }

        internal ConcernHarness()
        {
            var truth = Substitute.For<IHumanMissClassifier>();
            truth.ClassifierVersion.Returns("truth");
            truth.JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
                .Returns(new HumanMissJudgement(true, true, true, .9, "confirmed", true, true));
            this.Matcher.ClassifierVersion.Returns("match");
            this.Matcher.MatchAsync(Arg.Any<HumanConcernModelMatchRequest>(), Arg.Any<CancellationToken>()).Returns(call => new HumanConcernModelMatchResult(
                HumanConcernMatchState.NoMatch, [], [], call.Arg<HumanConcernModelMatchRequest>().Candidates.Select(candidate => candidate.Id).ToArray(),
                true));
            var reader = Substitute.For<IModelReviewEvidenceReader>();
            reader.ReadAsync(Arg.Any<IReadOnlyList<ModelReviewSource>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ => this.Snapshot);
            var codec = Substitute.For<ISecretProtectionCodec>();
            codec.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
            codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));
            var gate = Substitute.For<ICodeInsightsCollectionGate>();
            gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Observer = new(this.Db, truth, this.Matcher, codec, gate, NullLogger<CodeInsightModelConcernObserver>.Instance, evidenceReader: reader);
        }

        internal ThreadUpdatedEvent Observation(string thread, string text) => new(
            ModelOpportunityEvaluatorTests.Source.ClientId, ModelOpportunityEvaluatorTests.Source.ConnectionId!.Value, "repo", 42,
            thread, "a.cs", 12, "Fixed", ModelOpportunityEvaluatorTests.Origin.AddMinutes(1),
            [new("1", "human", false, ModelOpportunityEvaluatorTests.Origin, text)], ModelOpportunityEvaluatorTests.Origin.AddHours(1),
            ModelOpportunityEvaluatorTests.Source.ProviderScope, ConcernAnchor: ModelOpportunityEvaluatorTests.Anchor with { RootCommentId = thread });

        public ValueTask DisposeAsync() => this.Db.DisposeAsync();
    }
}
