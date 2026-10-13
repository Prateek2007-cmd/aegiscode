// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class WithheldReviewFindingMapperTests
{
    [Fact]
    public void FinalGateWithhold_PreservesCandidateMetadataAndUsesTheBlockingReason()
    {
        var finding = new CandidateReviewFinding(
                "finding",
                new CandidateFindingProvenance(
                    CandidateFindingProvenance.PrWidePassOrigin, "pr_wide_pass", unionPassIndex: 2, unionLens: "security",
                    originModelId: "review-model", originLogicalModelName: "reviewer", originSymbolName: "Execute", originSymbolKind: "Method"),
                CommentSeverity.Warning, "Potential defect.", CandidateReviewFinding.CrossCuttingCategory, "src/Foo.cs", 10,
                verificationOutcome: new VerificationOutcome(
                        "claim", "finding", VerificationOutcome.NonVerifiableKind,
                        FinalGateDecision.SummaryOnlyDisposition, [], [], VerificationOutcome.NoEvidence, null, VerificationOutcome.AiMicroVerifierEvaluator,
                        true)
                    { JudgeDegradation = EvidenceJudgeDegradations.OutputLimit })
            { ReadGrounding = ReviewCommentReadGrounding.Covered, ScopeRelation = ChangedLineRelation.OnChangedLine };
        var decision = new FinalGateDecision(
            "finding", FinalGateDecision.SummaryOnlyDisposition, ["withheld"], "verification", [], null, "Candidate withheld.");

        var withheld = Assert.Single(WithheldReviewFindingMapper.Materialize([finding], [decision]));

        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, withheld.ReasonCode);
        Assert.Equal("review-model", withheld.Comment.OriginModelId);
        Assert.Equal(2, withheld.Comment.OriginPassIndex);
        Assert.Equal("security", withheld.Comment.OriginPassLens);
        Assert.Equal("Execute", withheld.Comment.OriginSymbolName);
        Assert.Equal(ReviewCommentReadGrounding.Covered, withheld.Comment.SourceReadGrounding);
        Assert.Equal(ReviewCommentScopeRelation.OnChangedLine, withheld.Comment.ScopeRelation);
    }

    [Fact]
    public void AFinalGateDropAfterSupportedVerification_UsesTheGateReason()
    {
        var supported = new VerificationOutcome(
            "claim", "finding", VerificationOutcome.SupportedKind, FinalGateDecision.PublishDisposition,
            ["supported"], [], VerificationOutcome.StrongEvidence, null, VerificationOutcome.AiMicroVerifierEvaluator, false) { JudgeVerdict = "true" };
        var decision = new FinalGateDecision("finding", FinalGateDecision.DropDisposition, ["reread_contradicted"], "reread", [], null, null);

        Assert.Equal("reread_contradicted", WithheldReviewFindingMapper.Reason(supported, decision));
    }
}
