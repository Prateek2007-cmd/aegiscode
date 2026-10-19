// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ModelReviewCaptureTests
{
    [Fact]
    public void CandidateIdentityIsStableOnReplayAndChangesWithAttemptOrAssignedInput()
    {
        var file = new ReviewFileResult(Guid.NewGuid(), "f.cs");
        var proof = new ModelReviewEvidence { JobId = file.JobId, FilePath = file.FilePath, Attempt = 1, InputFingerprint = "input-a" };
        file.RecordModelEvidence(proof);
        var context = new ReviewSystemContext(null, [], null) { ModelEvidenceCheckpoint = file, ModelId = "a", LogicalModelName = "reviewer" };
        var started = DateTimeOffset.UtcNow;
        ReviewComment[] candidates = [new("f.cs", 1, CommentSeverity.Warning, "finding")];
        var first = ModelReviewCapture.Pass(context, 1, started, ModelGenerationDisposition.Completed, candidates);
        var replay = ModelReviewCapture.Pass(context, 1, started, ModelGenerationDisposition.Completed, candidates);
        file.RecordModelEvidence(proof with { Attempt = 2 });
        var nextAttempt = ModelReviewCapture.Pass(context, 1, started, ModelGenerationDisposition.Completed, candidates);
        file.RecordModelEvidence(proof with { InputFingerprint = "input-b" });
        var changedInput = ModelReviewCapture.Pass(context, 1, started, ModelGenerationDisposition.Completed, candidates);

        Assert.Equal(first.PassKey, replay.PassKey);
        Assert.Equal(first.Candidates[0].Id, replay.Candidates[0].Id);
        Assert.NotEqual(first.PassKey, nextAttempt.PassKey);
        Assert.NotEqual(first.Candidates[0].Id, nextAttempt.Candidates[0].Id);
        Assert.NotEqual(first.PassKey, changedInput.PassKey);
    }
}
