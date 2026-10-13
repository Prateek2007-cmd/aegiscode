// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class WithheldFindingPublicationMapperTests
{
    [Fact]
    public void ChangedPublishedPopulation_PreservesSeparateWithheldIdentity()
    {
        var first = new ReviewComment("src/P.cs", 1, CommentSeverity.Warning, "First published finding.");
        var second = new ReviewComment("src/P.cs", 2, CommentSeverity.Warning, "Second published finding.");
        var withheld = Withheld("src/A.cs");
        var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty();

        var original = ReviewFindingPublicationMapper.BuildProducedFindings([first], [first], diagnostics, true, "provider", [withheld]);
        var replay = ReviewFindingPublicationMapper.BuildProducedFindings([first, second], [first, second], diagnostics, true, "provider", [withheld]);

        var originalWithheld = Assert.Single(original, row => row.PublicationState == CodeInsightPublicationState.PolicyWithheld);
        var replayWithheld = Assert.Single(replay, row => row.PublicationState == CodeInsightPublicationState.PolicyWithheld);
        Assert.Equal(originalWithheld.Ordinal, replayWithheld.Ordinal);
        Assert.DoesNotContain(
            replay.Where(row => row.PublicationState != CodeInsightPublicationState.PolicyWithheld), row => row.Ordinal == originalWithheld.Ordinal);
    }

    [Fact]
    public void ReorderedCheckpoints_PreservePublishedAndWithheldOrdinals()
    {
        var published = new ReviewComment("src/P.cs", 1, CommentSeverity.Warning, "Published.");
        var first = Withheld("src/A.cs");
        var second = Withheld("src/B.cs");
        var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty() with
        {
            PostedComments = [new("comment", "thread", published.FilePath, published.LineNumber, FindingOrdinal: 0)],
        };

        var original = ReviewFindingPublicationMapper.BuildProducedFindings([published], [published], diagnostics, true, "provider", [second, first]);
        var replay = ReviewFindingPublicationMapper.BuildProducedFindings([published], [published], diagnostics, true, "provider", [first, second]);

        Assert.Equal(original, replay);
        Assert.Equal(CodeInsightPublicationState.Published, original[0].PublicationState);
        Assert.Equal("comment", original[0].ProviderCommentId);
        Assert.Equal(-1, original[1].Ordinal);
        Assert.Equal("src/A.cs", original[1].FilePath);
        Assert.Equal(-2, original[2].Ordinal);
        Assert.All(
            original.Skip(1), row =>
            {
                Assert.Equal(CodeInsightPublicationState.PolicyWithheld, row.PublicationState);
                Assert.Null(row.ProviderThreadId);
                Assert.Null(row.ProviderCommentId);
            });
    }

    private static WithheldReviewFinding Withheld(string path) => new(
        new ReviewComment(path, 1, CommentSeverity.Warning, "Potential issue."), "judge_output_limit");
}
