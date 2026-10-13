// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>Reasoning effort explicitly advertised for OpenAI models in the bundled catalog.</summary>
public static class BundledModelReasoningSupport
{
    private static readonly Lazy<HashSet<string>> LowEffortModels = new(
        LoadLowEffortModels,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether the exact model identifier advertises low reasoning effort.</summary>
    /// <param name="remoteModelId">The provider model identifier, without alias inference.</param>
    public static bool SupportsLowEffort(string remoteModelId)
    {
        return LowEffortModels.Value.Contains(remoteModelId);
    }

    private static HashSet<string> LoadLowEffortModels()
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var snapshot = BundledCatalogSnapshot.Open();
            using var document = JsonDocument.Parse(snapshot);
            if (!document.RootElement.TryGetProperty("openai", out var provider)
                || !provider.TryGetProperty("models", out var models))
            {
                return supported;
            }

            foreach (var model in models.EnumerateObject())
            {
                if (!model.Value.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.True
                                                                                || !model.Value.TryGetProperty("reasoning_options", out var options) ||
                                                                                options.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var option in options.EnumerateArray())
                {
                    if (!option.TryGetProperty("type", out var type) || type.GetString() != "effort"
                                                                     || !option.TryGetProperty("values", out var values) ||
                                                                     values.ValueKind != JsonValueKind.Array
                                                                     || !values.EnumerateArray().Any(value =>
                                                                         value.ValueKind == JsonValueKind.String && value.GetString() == "low"))
                    {
                        continue;
                    }

                    var modelId = model.Value.TryGetProperty("id", out var id) ? id.GetString() : model.Name;
                    if (!string.IsNullOrWhiteSpace(modelId))
                    {
                        supported.Add(modelId);
                    }
                }
            }

            return supported;
        }
        catch (Exception)
        {
            // Missing capability evidence leaves provider defaults in effect.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
