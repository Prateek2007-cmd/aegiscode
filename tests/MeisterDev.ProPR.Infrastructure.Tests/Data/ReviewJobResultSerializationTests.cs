// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Tests.Data;

public sealed class ReviewJobResultSerializationTests
{
    [Fact]
    public void PersistedEvidence_DoesNotChangeDefaultOrPublicJsonSerialization()
    {
        using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var converter = db.Model.FindEntityType(typeof(ReviewJob))!.FindProperty(nameof(ReviewJob.Result))!.GetValueConverter()!;
        var withheld = new WithheldReviewFinding(new ReviewComment("src/Withheld.cs", 12, CommentSeverity.Warning, "Potential defect."), "judge_output_limit");
        var result = new ReviewResult("Summary", [new ReviewComment("src/Published.cs", 9, CommentSeverity.Info, "Published finding.")])
        {
            WithheldFindings = [withheld],
        };

        var storedJson = Assert.IsType<string>(converter.ConvertToProvider(result));
        var restored = Assert.IsType<ReviewResult>(converter.ConvertFromProvider(storedJson));

        Assert.Equal(withheld, Assert.Single(restored.WithheldFindings));
        foreach (var options in new[] { JsonSerializerOptions.Default, new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(restored, options));
            var propertyName = options.PropertyNamingPolicy?.ConvertName(nameof(ReviewResult.WithheldFindings)) ?? nameof(ReviewResult.WithheldFindings);
            Assert.False(payload.RootElement.TryGetProperty(propertyName, out _));
            Assert.Equal(JsonSerializer.Serialize(result, options), JsonSerializer.Serialize(restored, options));
        }
    }
}
