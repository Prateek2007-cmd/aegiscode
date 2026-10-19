// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Infrastructure.Data.Serialization;

/// <summary>Serializes durable review result annotations, including withheld evidence.</summary>
internal static class ReviewResultPersistenceSerializer
{
    internal static string Serialize(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
        payload[nameof(ReviewResult.WithheldFindings)] = JsonSerializer.SerializeToNode(result.WithheldFindings);
        payload[nameof(ReviewResult.ModelEvidence)] = JsonSerializer.SerializeToNode(result.ModelEvidence?.BoundedOrUnknown());
        payload[nameof(ReviewResult.ModelEvidenceManifest)] = JsonSerializer.SerializeToNode(result.ModelEvidenceManifest);
        payload[nameof(ReviewResult.GenerationDisposition)] = JsonSerializer.SerializeToNode(result.GenerationDisposition);
        return payload.ToJsonString();
    }

    internal static ReviewResult? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.Deserialize<ReviewResult>();
        if (result is null)
        {
            return result;
        }

        return result with
        {
            WithheldFindings = !document.RootElement.TryGetProperty(nameof(ReviewResult.WithheldFindings), out var withheld) ||
                               withheld.ValueKind == JsonValueKind.Null
                ? []
                : withheld.Deserialize<IReadOnlyList<WithheldReviewFinding>>() ?? [],
            ModelEvidence = document.RootElement.TryGetProperty(nameof(ReviewResult.ModelEvidence), out var proof) && proof.ValueKind != JsonValueKind.Null
                ? ModelEvidenceJson.ReadEvidence(proof.GetRawText())
                : null,
            ModelEvidenceManifest = document.RootElement.TryGetProperty(nameof(ReviewResult.ModelEvidenceManifest), out var manifest) &&
                                    manifest.ValueKind != JsonValueKind.Null
                ? ModelEvidenceJson.ReadManifest(manifest.GetRawText())
                : null,
            GenerationDisposition = document.RootElement.TryGetProperty(nameof(ReviewResult.GenerationDisposition), out var disposition) &&
                                    disposition.ValueKind != JsonValueKind.Null
                ? disposition.Deserialize<ModelGenerationDisposition?>()
                : null,
        };
    }
}
