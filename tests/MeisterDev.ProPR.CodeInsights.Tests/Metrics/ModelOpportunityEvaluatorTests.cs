// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ModelOpportunityEvaluatorTests
{
    internal static readonly DateTimeOffset Origin = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    internal static readonly ModelReviewSource Source = new(Guid.NewGuid(), "repo", 42, "GitHub:https://github.com", Guid.NewGuid());

    internal static ReviewConcernAnchor Anchor => new()
    {
        RootCommentId = "human", OriginAt = Origin, OriginalScopeProven = true,
        FilePath = "a.cs", Side = "RIGHT", StartLine = 12, EndLine = 12, HeadSha = "head", CommitSha = "head"
    };

    [Fact]
    public void CompleteZeroIsKnownButUnrelatedFailedModelDoesNotInvalidateIt()
    {
        var proof = Proof([Pass("A"), Pass("B") with { Disposition = ModelGenerationDisposition.Failed, CandidateSetComplete = false }]);
        var a = ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "A", "logical-A");
        Assert.True(a.Applicable);
        Assert.True(a.NegativeComplete);
        Assert.Empty(a.Candidates);
        Assert.False(ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "B", "logical-B").NegativeComplete);
    }

    [Fact]
    public void LaterAcceptanceCannotBorrowAnEarlierEnvelopeReceiptOrClaimedCompletion()
    {
        var proof = Proof([Pass("A"), Pass("B") with { AcceptedAt = Origin.AddMinutes(1) }]);
        Assert.True(ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "A", "logical-A").NegativeComplete);
        Assert.False(ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "B", "logical-B").NegativeComplete);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("connection")]
    [InlineData("host")]
    [InlineData("tenant")]
    public void IncompatibleSourceCannotEstablishAnOpportunity(string mismatch)
    {
        var source = mismatch switch
        {
            "connection" => Source with { ConnectionId = Guid.NewGuid() },
            "host" => Source with { ProviderScope = "GitHub:https://other.example" }, "tenant" => Source with { ClientId = Guid.NewGuid() }, _ => Source
        };
        var anchor = mismatch == "revision" ? Anchor with { CommitSha = "other-head", HeadSha = "other-head" } : Anchor;
        Assert.False(ModelOpportunityEvaluator.Assess(Snapshot(Proof([Pass("A")])), source, anchor, "A", "logical-A").NegativeComplete);
    }

    [Fact]
    public void PositiveClosedAssignmentOutsideConcernRangeIsNotApplicable()
    {
        var proof = Proof([Pass("A")]) with { AssignedRanges = [new(50, 60)] };
        var assessed = ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "A", "logical-A");
        Assert.True(assessed.NotApplicable);
        Assert.Null(assessed.UnavailableReason);
    }

    [Fact]
    public void MissingInputScopeKeepsPositiveCandidatesButCannotCertifyNonDetection()
    {
        var proof = Proof([Pass("A") with { Candidates = [new("finding", "a.cs", 12, "Null input throws")] }]) with { InputComplete = false };
        var assessed = ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "A", "logical-A");
        Assert.False(assessed.NegativeComplete);
        Assert.Single(assessed.Candidates);
        Assert.Equal("model-input-scope-unavailable", assessed.UnavailableReason);
    }

    [Fact]
    public void CarriedCandidatesCannotBorrowTheDestinationJobsRevision()
    {
        var proof = Proof([Pass("A") with { Candidates = [new("old-finding", "a.cs", 12, "Original revision finding")] }])
            with
            {
                HeadSha = "previous-head", RevisionKey = "previous-revision", InputComplete = false, OriginJobId = Guid.NewGuid()
            };
        var assessment = ModelOpportunityEvaluator.Assess(Snapshot(proof), Source, Anchor, "A", "logical-A");
        Assert.Empty(assessment.Candidates);
        Assert.False(assessment.NegativeComplete);
    }

    [Fact]
    public void MissingOriginalAnchorNeverUsesObservationHeadOrAnAbsentModel()
    {
        var snapshot = Snapshot(Proof([Pass("A")]));
        Assert.False(ModelOpportunityEvaluator.Assess(snapshot, Source, Anchor with { OriginAt = null }, "A", "logical-A").NegativeComplete);
        Assert.False(ModelOpportunityEvaluator.Assess(snapshot, Source, Anchor, "missing", "logical-missing").NotApplicable);
    }

    [Fact]
    public void ClosedPerFileInventoryProvesAnotherModelDidNotReviewTheConcernFile()
    {
        var a = Proof([Pass("A")]);
        var b = Proof([Pass("B")]) with { FilePath = "b.cs" };
        var snapshot = Snapshot(a, b);
        snapshot = snapshot with
        {
            Executions = [snapshot.Executions[0] with { Manifest = snapshot.Executions[0].Manifest! with { Files = ["a.cs", "b.cs"] } }]
        };
        var anchor = Anchor with { FilePath = "b.cs" };
        Assert.True(ModelOpportunityEvaluator.Assess(snapshot, Source, anchor, "A", "logical-A").NotApplicable);
        var incomplete = snapshot with { Executions = [snapshot.Executions[0] with { Files = [a, b with { InventoryComplete = false }] }] };
        Assert.False(ModelOpportunityEvaluator.Assess(incomplete, Source, anchor, "A", "logical-A").NotApplicable);
    }

    [Fact]
    public void LaterSameModelExecutionsCannotInvalidateTheEarlierConcernOpportunity()
    {
        var early = Proof([Pass("A")]);
        var later = Proof([Pass("A") with { PassKey = "later-A", AcceptedAt = Origin.AddHours(2), CompletedAt = Origin.AddHours(1) }]);
        var snapshot = Snapshot(early) with { Executions = Snapshot(early).Executions.Concat(Snapshot(later).Executions).ToArray() };
        Assert.True(ModelOpportunityEvaluator.Assess(snapshot, Source, Anchor, "A", "logical-A").NegativeComplete);
        Assert.False(ModelOpportunityEvaluator.Assess(Snapshot(later), Source, Anchor, "A", "logical-A").NegativeComplete);
    }

    internal static ModelReviewPassEvidence Pass(string model) => new()
    {
        JobId = Guid.Empty, PassKey = "pass-" + model,
        ModelId = model, LogicalModelName = "logical-" + model, Kind = "generation", Disposition = ModelGenerationDisposition.Completed,
        CandidateSetComplete = true, StartedAt = Origin.AddHours(-2), CompletedAt = Origin.AddHours(-1), AcceptedAt = Origin.AddMinutes(-30), Candidates = []
    };

    internal static ModelReviewEvidence Proof(IReadOnlyList<ModelReviewPassEvidence> passes) => new()
    {
        ClientId = Source.ClientId,
        RepositoryId = Source.RepositoryId, PullRequestId = Source.PullRequestId, ProviderScope = Source.ProviderScope,
        SourceConnectionId = Source.ConnectionId,
        RevisionKey = "rev", HeadSha = "head", BaseSha = "base", FilePath = "a.cs", InputFingerprint = "input", InventoryComplete = true,
        InputComplete = true, AssignedRanges = [new(1, 30)], CapturedAt = Origin.AddHours(-2), AcceptedAt = Origin.AddMinutes(-30), Passes = passes
    };

    internal static ModelReviewEvidenceSnapshot Snapshot(params ModelReviewEvidence[] files) => new(
    [
        new(
            Guid.Empty, Source, "rev", "head", "base",
            new() { Files = ["a.cs"], InventoryComplete = true, CapturedAt = Origin.AddHours(-2), AcceptedAt = Origin.AddMinutes(-30) }, files)
    ], true);
}
