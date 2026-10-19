// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Isolates malformed optional analytics proof from the review result carrying it.</summary>
public static class ModelEvidenceJson
{
    public static ModelReviewEvidence? ReadEvidence(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelReviewEvidence>(json, options)?.BoundedOrUnknown();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException)
        {
            return new() { UnavailableReason = "model-proof-limit-exceeded" };
        }
    }

    public static ModelReviewProofFragment? ReadFragment(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            var fragment = JsonSerializer.Deserialize<ModelReviewProofFragment>(json, options);
            // Preserve rejected analytics presence so its carrier cannot reset an ordinary checkpoint.
            return fragment is null || fragment.IsBounded ? fragment : new(string.Empty, 0, 0, string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException)
        {
            return new(string.Empty, 0, 0, string.Empty);
        }
    }

    public static ModelReviewManifest? ReadManifest(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ModelReviewManifest>(json, options);
            return manifest is null || manifest.IsBounded ? manifest : new() { JobId = manifest.JobId, UnavailableReason = "model-proof-limit-exceeded" };
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException)
        {
            return new() { UnavailableReason = "model-proof-limit-exceeded" };
        }
    }
}

public sealed class ModelReviewEvidenceJsonConverter : JsonConverter<ModelReviewEvidence>
{
    public override ModelReviewEvidence? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var element = JsonDocument.ParseValue(ref reader);
        return ModelEvidenceJson.ReadEvidence(element.RootElement.GetRawText(), options);
    }

    public override void Write(Utf8JsonWriter writer, ModelReviewEvidence value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.BoundedOrUnknown(), options);
}

public sealed class ModelReviewManifestJsonConverter : JsonConverter<ModelReviewManifest>
{
    public override ModelReviewManifest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var element = JsonDocument.ParseValue(ref reader);
        return ModelEvidenceJson.ReadManifest(element.RootElement.GetRawText(), options);
    }

    public override void Write(Utf8JsonWriter writer, ModelReviewManifest value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.IsBounded ? value : new ModelReviewManifest { UnavailableReason = "model-proof-limit-exceeded" }, options);
}

public sealed class ModelReviewManifestListJsonConverter : JsonConverter<IReadOnlyList<ModelReviewManifest>>
{
    public override IReadOnlyList<ModelReviewManifest> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var element = JsonDocument.ParseValue(ref reader);
        if (element.RootElement.ValueKind != JsonValueKind.Array || element.RootElement.GetArrayLength() > ModelReviewEvidence.MaximumPasses)
        {
            return [new() { UnavailableReason = "model-proof-limit-exceeded" }];
        }

        return element.RootElement.EnumerateArray().Select(value => ModelEvidenceJson.ReadManifest(value.GetRawText(), options)
                                                                    ?? new ModelReviewManifest { UnavailableReason = "model-proof-limit-exceeded" }).ToArray();
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ModelReviewManifest> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var manifest in value.Take(ModelReviewEvidence.MaximumPasses))
        {
            JsonSerializer.Serialize(
                writer, manifest?.IsBounded == true ? manifest : new ModelReviewManifest { UnavailableReason = "model-proof-limit-exceeded" }, options);
        }

        writer.WriteEndArray();
    }
}

public sealed class ModelReviewProofFragmentJsonConverter : JsonConverter<ModelReviewProofFragment>
{
    public override ModelReviewProofFragment? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var element = JsonDocument.ParseValue(ref reader);
        return ModelEvidenceJson.ReadFragment(element.RootElement.GetRawText(), options);
    }

    public override void Write(Utf8JsonWriter writer, ModelReviewProofFragment value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.IsBounded ? value : null, options);
}
