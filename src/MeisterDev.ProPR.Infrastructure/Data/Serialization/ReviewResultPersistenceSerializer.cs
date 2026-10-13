// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Data.Serialization;

/// <summary>Serializes durable review result annotations, including withheld evidence.</summary>
internal static class ReviewResultPersistenceSerializer
{
    internal static string Serialize(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
        payload[nameof(ReviewResult.WithheldFindings)] = JsonSerializer.SerializeToNode(result.WithheldFindings);
        return payload.ToJsonString();
    }

    internal static ReviewResult? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.Deserialize<ReviewResult>();
        if (result is null || !document.RootElement.TryGetProperty(nameof(ReviewResult.WithheldFindings), out var withheld))
        {
            return result;
        }

        return result with
        {
            WithheldFindings = withheld.ValueKind == JsonValueKind.Null
                ? []
                : withheld.Deserialize<IReadOnlyList<WithheldReviewFinding>>() ?? [],
        };
    }
}
