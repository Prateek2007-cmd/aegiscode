// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.Entities;

public sealed class WithheldReviewFindingTests
{
    [Fact]
    public void CheckpointCompletionAndRecovery_PreserveIndependentWithheldEvidence()
    {
        var evidence = new List<WithheldReviewFinding> { Finding() };
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        result.MarkCompleted("Summary", [], ["baseline"], evidence);
        evidence.Clear();

        Assert.Empty(result.Comments!);
        Assert.Single(result.WithheldFindings!);
        var resumed = ReviewFileResult.CreateResumed(Guid.NewGuid(), result);
        Assert.False(resumed.IsCarriedForward);
        Assert.Equal(result.WithheldFindings, resumed.WithheldFindings);
        var carried = ReviewFileResult.CreateCarriedForward(Guid.NewGuid(), result);
        Assert.True(carried.IsCarriedForward);
        Assert.Equal(result.WithheldFindings, carried.WithheldFindings);
    }

    [Fact]
    public void RetryReset_ClearsPreviousWithheldEvidence()
    {
        var result = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        result.MarkCompleted("Summary", [], withheldFindings: [Finding()]);

        result.ResetForRetry();

        Assert.Null(result.WithheldFindings);
        result.MarkCompleted("New summary", []);
        Assert.Empty(result.WithheldFindings!);
    }

    [Fact]
    public void ReviewResultHttpSerialization_ExcludesInternalEvidence()
    {
        var result = new ReviewResult("Summary", []) { WithheldFindings = [Finding()] };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.False(json.RootElement.TryGetProperty("withheldFindings", out _));
        Assert.Empty(json.RootElement.GetProperty("comments").EnumerateArray());
    }

    [Fact]
    public void WithheldFindingJson_RoundTripsItsMetadata()
    {
        var finding = Finding();
        var restored = JsonSerializer.Deserialize<WithheldReviewFinding>(JsonSerializer.Serialize(finding));

        Assert.Equal(finding, restored);
    }

    private static WithheldReviewFinding Finding() => new(
        new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential defect.")
        {
            OriginModelId = "review-model", OriginPassIndex = 2, OriginSymbolName = "Execute", ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
        }, "judge_output_limit");
}
