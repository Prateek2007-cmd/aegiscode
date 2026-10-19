// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Entities;
using System.Text.Json;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class ModelReviewEvidenceTests
{
    [Fact]
    public void MalformedRangeMetadataCannotThrowDuringCheckpointMerge()
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var proof = new ModelReviewEvidence { JobId = file.JobId, FilePath = file.FilePath, Passes = [Pass()] };
        file.RecordModelEvidence(proof);
        Assert.Null(Record.Exception(() => file.RecordModelEvidence(proof with { AssignedRanges = null! })));
        Assert.Equal("model-proof-limit-exceeded", file.ModelEvidence!.UnavailableReason);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("client")]
    [InlineData("repository")]
    [InlineData("pull-request")]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("ranges")]
    [InlineData("input-completeness")]
    [InlineData("same")]
    public void ResumedCheckpointCannotRelabelOldPassesWithAnotherSourceOrInputScope(string identity)
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var receipt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var proof = new ModelReviewEvidence
        {
            JobId = file.JobId, Attempt = 1, LeaseGeneration = 1, ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 42,
            ProviderScope = "GitHub:https://github.com", SourceConnectionId = Guid.NewGuid(), RevisionKey = "revision", HeadSha = "head", BaseSha = "base",
            FilePath = file.FilePath, InputFingerprint = "input", AssignedRanges = [new(1, 20)], InputComplete = true, InventoryComplete = true,
            Passes = [Pass() with { JobId = file.JobId, Attempt = 1, LeaseGeneration = 1, AcceptedAt = receipt }],
        };
        file.RecordModelEvidence(proof);
        var incoming = proof with
        {
            Attempt = 2, LeaseGeneration = 2,
            Passes = [Pass() with { JobId = file.JobId, Attempt = 2, LeaseGeneration = 2, PassKey = "new", ModelId = "B" }]
        };
        incoming = identity switch
        {
            "connection" => incoming with { SourceConnectionId = Guid.NewGuid() },
            "client" => incoming with { ClientId = Guid.NewGuid() },
            "repository" => incoming with { RepositoryId = "another-repo" },
            "pull-request" => incoming with { PullRequestId = 99 },
            "head" => incoming with { HeadSha = "another-head" },
            "base" => incoming with { BaseSha = "another-base" },
            "ranges" => incoming with { AssignedRanges = [new(10, 40)] },
            "input-completeness" => incoming with { InputComplete = false },
            _ => incoming,
        };

        file.ResetForRetry();
        file.RecordModelEvidence(incoming);

        Assert.Equal(incoming.SourceConnectionId, file.ModelEvidence!.SourceConnectionId);
        Assert.Equal(incoming.ClientId, file.ModelEvidence.ClientId);
        Assert.Equal(incoming.RepositoryId, file.ModelEvidence.RepositoryId);
        Assert.Equal(incoming.PullRequestId, file.ModelEvidence.PullRequestId);
        Assert.Equal(incoming.HeadSha, file.ModelEvidence.HeadSha);
        Assert.Equal(incoming.BaseSha, file.ModelEvidence.BaseSha);
        Assert.Equal(incoming.AssignedRanges, file.ModelEvidence.AssignedRanges);
        Assert.Equal(incoming.InputComplete, file.ModelEvidence.InputComplete);
        if (identity == "same")
        {
            Assert.Equal(receipt, Assert.Single(file.ModelEvidence.Passes, pass => pass.ModelId == "model-a").AcceptedAt);
            Assert.Equal(2, file.ModelEvidence.Passes.Count);
        }
        else
        {
            Assert.Equal("B", Assert.Single(file.ModelEvidence.Passes).ModelId);
        }
    }

    [Theory]
    [InlineData(ModelGenerationDisposition.Completed, true)]
    [InlineData(ModelGenerationDisposition.Skipped, false)]
    [InlineData(ModelGenerationDisposition.NotApplicable, false)]
    [InlineData(ModelGenerationDisposition.InvalidOutput, false)]
    [InlineData(ModelGenerationDisposition.Failed, false)]
    [InlineData(ModelGenerationDisposition.Cancelled, false)]
    public void OnlyPositivelyCompletedGenerationCertifiesAnEmptyCandidateSet(ModelGenerationDisposition disposition, bool certified)
    {
        var pass = Pass() with { Disposition = disposition };
        Assert.Equal(certified, pass.IsCertified);
        Assert.False((pass with { CandidateSetComplete = false }).IsCertified);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void BlankCandidateTextCannotCertifyAnAcceptedCandidateSet(string message)
    {
        var proof = new ModelReviewEvidence { Passes = [Pass() with { Candidates = [new("candidate", "f.cs", 1, message)] }] };
        Assert.False(proof.IsBounded);
    }

    [Fact]
    public void ProofFragmentsBoundBytesOffsetsAndRetainedPartCount()
    {
        var fragment = new ModelReviewProofFragment(new string('a', 64), 0, 4, Convert.ToBase64String(new byte[4]));
        Assert.True(fragment.IsBounded);
        Assert.False((fragment with { Offset = -1 }).IsBounded);
        Assert.False((fragment with { TotalBytes = ModelReviewProofFragment.MaximumProofBytes + 1 }).IsBounded);
        Assert.False((fragment with { Data = new string('a', ModelReviewProofFragment.MaximumDataCharacters + 1) }).IsBounded);
        Assert.False(
            (new ModelReviewEvidence { PendingFragments = Enumerable.Repeat(fragment, ModelReviewProofFragment.MaximumFragments + 1).ToArray() }).IsBounded);
    }

    [Fact]
    public void DuplicatePassOrCandidateIdentitiesCannotCertifyAnAmbiguousCandidateSet()
    {
        var duplicatePass = new ModelReviewEvidence { Passes = [Pass(), Pass()] };
        var duplicateCandidate = new ModelReviewEvidence { Passes = [Pass() with { Candidates = [new("id", "f.cs", 1, "one"), new("id", "f.cs", 2, "two")] }] };
        Assert.False(duplicatePass.IsBounded);
        Assert.False(duplicateCandidate.IsBounded);
        Assert.Empty(duplicatePass.BoundedOrUnknown().Passes);
    }

    [Fact]
    public void BoundedEvidenceIncludesZeroCandidatesButRefusesOversizedInventories()
    {
        var evidence = new ModelReviewEvidence { Passes = [Pass()], AssignedRanges = [new(1, 10)] };
        Assert.True(evidence.IsBounded);
        Assert.False((evidence with { Passes = Enumerable.Repeat(Pass(), ModelReviewEvidence.MaximumPasses + 1).ToArray() }).IsBounded);
        Assert.False((evidence with { AssignedRanges = [new(5, 1)] }).IsBounded);
        Assert.False(
            (evidence with
            {
                Passes =
                [
                    Pass() with
                    {
                        Candidates =
                        [
                            new(
                                "candidate", "f.cs", 1,
                                new string('x', ModelReviewEvidence.MaximumCandidateCharacters + 1))
                        ]
                    }
                ]
            }).IsBounded);
    }

    private static ModelReviewPassEvidence Pass() => new()
    {
        PassKey = "baseline", ModelId = "model-a", CandidateSetComplete = true,
        Disposition = ModelGenerationDisposition.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData("{\"Passes\":[null]}")]
    [InlineData("{\"AssignedRanges\":[null]}")]
    [InlineData("{\"ProviderScope\":null}")]
    [InlineData("{\"Passes\":[{\"PassKey\":\"pass\",\"ModelId\":null}]}")]
    [InlineData("{\"Passes\":[{\"PassKey\":\"pass\",\"ModelId\":\"m\",\"Candidates\":[null]}]}")]
    [InlineData("{\"Passes\":[{\"PassKey\":\"pass\",\"ModelId\":\"m\",\"Candidates\":[{\"Id\":\"c\",\"Comment\":null}]}]}")]
    public void InvalidWireProofBecomesUnknownWithoutRejectingTheReview(string json)
    {
        var proof = JsonSerializer.Deserialize<ModelReviewEvidence>(json)!;
        var failure = Record.Exception(() => proof.BoundedOrUnknown());
        Assert.Null(failure);
        Assert.Equal("model-proof-limit-exceeded", proof.BoundedOrUnknown().UnavailableReason);
    }

    [Fact]
    public void CheckpointKeepsSuccessfulModelProofWhenAnotherPassFailsAndReusesItsOriginalCutoff()
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var proof = new ModelReviewEvidence
        {
            JobId = file.JobId, FilePath = file.FilePath, InputFingerprint = "input",
            InventoryComplete = true, Passes = [Pass(), Pass() with { PassKey = "second", ModelId = "b", Disposition = ModelGenerationDisposition.Failed }]
        };
        file.RecordModelEvidence(proof);
        file.MarkCompleted("summary", []);
        var resumed = ReviewFileResult.CreateResumed(Guid.NewGuid(), file);
        var carried = ReviewFileResult.CreateCarriedForward(Guid.NewGuid(), file, "input");
        var unproven = ReviewFileResult.CreateCarriedForward(Guid.NewGuid(), file);

        Assert.NotNull(file.ModelEvidence);
        Assert.True(file.ModelEvidence.Passes[0].IsCertified);
        Assert.Equal(proof.Passes[0].CompletedAt, resumed.ModelEvidence!.Passes[0].CompletedAt);
        Assert.Equal(file.JobId, resumed.ModelEvidence.OriginJobId);
        Assert.True(carried.ModelEvidence!.UnchangedInputProven);
        Assert.False(unproven.ModelEvidence!.UnchangedInputProven);
    }

    [Fact]
    public void LaterInvalidPassCannotRemoveAnAlreadyRetainedIndependentCertificate()
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var good = new ModelReviewEvidence
        {
            JobId = file.JobId, FilePath = file.FilePath, ProviderScope = "source",
            RevisionKey = "head", InputFingerprint = "input", InventoryComplete = true, Passes = [Pass()]
        };
        file.RecordModelEvidence(good);
        file.RecordModelEvidence(
            good with
            {
                Passes =
                [
                    Pass() with
                    {
                        PassKey = "second", ModelId = "b",
                        Candidates = [new("b1", "f.cs", 1, new string('x', ModelReviewEvidence.MaximumCandidateCharacters + 1))]
                    }
                ]
            });

        Assert.NotNull(file.ModelEvidence);
        Assert.Contains(file.ModelEvidence.Passes, pass => pass.ModelId == "model-a" && pass.IsCertified);
        Assert.Contains(file.ModelEvidence.Passes, pass => pass.ModelId == "b" && !pass.IsCertified && pass.Candidates.Count == 0);
        Assert.Equal("head", file.ModelEvidence.RevisionKey);
    }

    [Fact]
    public void OversizedAnalyticsProofDoesNotDiscardCompletedReviewComments()
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var comments = new[] { new ReviewComment("f.cs", 1, CommentSeverity.Warning, "A review finding") };
        file.RecordModelEvidence(
            new ModelReviewEvidence
            {
                JobId = file.JobId, FilePath = file.FilePath,
                Passes = Enumerable.Repeat(Pass(), ModelReviewEvidence.MaximumPasses + 1).ToArray()
            });
        file.MarkCompleted("summary", comments);

        Assert.True(file.IsComplete);
        Assert.Equal(comments, file.Comments);
        Assert.NotNull(file.ModelEvidence);
        Assert.Equal("model-proof-limit-exceeded", file.ModelEvidence.UnavailableReason);
        Assert.False(file.ModelEvidence.InventoryComplete);
    }
}
