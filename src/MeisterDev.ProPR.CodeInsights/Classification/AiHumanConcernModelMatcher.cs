// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Encodings.Web;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Classification.Prompts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Classification;

internal sealed class AiHumanConcernModelMatcher(
    IAiRuntimeResolver resolver,
    IModelUsageRecorder usage,
    ILogger<AiHumanConcernModelMatcher> logger) : IHumanConcernModelMatcher
{
    private static readonly JsonSerializerOptions InputJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ClassifierVersion => "human-concern-model-v1";

    public async Task<HumanConcernModelMatchResult> MatchAsync(HumanConcernModelMatchRequest request, CancellationToken ct = default)
    {
        var asked = false;
        if (request.Candidates is null || request.Candidates.Count > ModelReviewEvidence.MaximumCandidates || request.Discussion is null
            || request.Candidates.Any(candidate => candidate is null || candidate.Id is not { Length: > 0 and <= 64 }
                                                                     || candidate.FilePath is null || candidate.Message is null)
            || request.Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count)
        {
            return Unknown(false, "model-concern-input-limit-exceeded");
        }

        var others = request.OtherConcerns ?? [];
        if (others.Count > 16 || others.Any(concern => concern is null || concern.Id is not { Length: > 0 and <= 64 } || concern.Discussion is null)
                              || others.Select(concern => concern.Id).Distinct(StringComparer.Ordinal).Count() != others.Count)
        {
            return Unknown(false, "model-concern-input-limit-exceeded");
        }

        if (request.Candidates.Count == 0 && others.Count == 0)
        {
            return new(HumanConcernMatchState.NoMatch, [], [], [], false, Confidence: 1);
        }

        var completeInput = request.Discussion.Length <= 6000 && request.Candidates.All(candidate => candidate.Message.Length <= 2000)
                                                              && others.All(concern => concern.Discussion.Length <= 2000);
        var discussion = Clip(request.Discussion, 6000);
        var equivalents = others.Select(concern => new HumanConcernReference(concern.Id, Clip(concern.Discussion, 2000))).ToArray();
        var candidates = request.Candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(candidate => candidate with { Message = Clip(candidate.Message, 2000) }).ToArray();
        var evaluated = new HashSet<string>(StringComparer.Ordinal);
        var equivalentIds = new HashSet<string>(StringComparer.Ordinal);
        var allKnown = true;
        var offset = 0;
        var chunks = 0;

        HumanConcernModelMatchResult Unavailable(string reason) => equivalentIds.Count == 0
            ? Unknown(asked, reason)
            : new(
                HumanConcernMatchState.Match, [], equivalentIds.Order(StringComparer.Ordinal).ToArray(),
                evaluated.Order(StringComparer.Ordinal).ToArray(), asked, reason);

        try
        {
            var runtime = await resolver.ResolveChatRuntimeAsync(request.ClientId, AiPurpose.InsightsClassification, ct);
            do
            {
                if (++chunks > 64)
                {
                    return Unavailable("model-concern-input-limit-exceeded");
                }

                var take = Math.Min(16, candidates.Length - offset);
                var chunk = candidates.Skip(offset).Take(take).ToArray();
                var input = SerializeInput(discussion, chunk, equivalents);
                while (input.Length > 32_000 && take > 1)
                {
                    take--;
                    chunk = candidates.Skip(offset).Take(take).ToArray();
                    input = SerializeInput(discussion, chunk, equivalents);
                }

                if (input.Length > 32_000)
                {
                    return Unavailable("model-concern-input-limit-exceeded");
                }

                ChatMessage[] messages = [new(ChatRole.System, InsightPrompts.HumanConcernModelSystem()), new(ChatRole.User, input)];
                asked = true;
                var response = await runtime.ChatClient.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 2048 }, ct);
                await usage.RecordAsync(request.ClientId, runtime, response, ct);
                var parsed = Parse(response.Text, chunk, equivalents);
                if (parsed is { State: HumanConcernMatchState.Match, MatchedFindingIds.Count: > 0 })
                {
                    evaluated.UnionWith(parsed.EvaluatedCandidateIds);
                    equivalentIds.UnionWith(parsed.EquivalentConcernIds);
                    return parsed with
                    {
                        ModelWasAsked = true, EquivalentConcernIds = equivalentIds.Order(StringComparer.Ordinal).ToArray(),
                        EvaluatedCandidateIds = evaluated.Order(StringComparer.Ordinal).ToArray()
                    };
                }

                if (parsed is { State: HumanConcernMatchState.Match })
                {
                    equivalentIds.UnionWith(parsed.EquivalentConcernIds);
                }

                if (parsed is null || parsed.State == HumanConcernMatchState.Unknown
                                   || !chunk.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal).SetEquals(parsed.EvaluatedCandidateIds))
                {
                    allKnown = false;
                }
                else
                {
                    evaluated.UnionWith(parsed.EvaluatedCandidateIds);
                }

                offset += take;
            } while (offset < candidates.Length);

            if (equivalentIds.Count > 0)
            {
                return new(
                    HumanConcernMatchState.Match, [], equivalentIds.Order(StringComparer.Ordinal).ToArray(),
                    evaluated.Order(StringComparer.Ordinal).ToArray(), asked,
                    allKnown && completeInput && evaluated.Count == candidates.Length ? null
                    : completeInput ? "model-concern-match-unavailable" : "model-concern-input-limit-exceeded");
            }

            return allKnown && completeInput && evaluated.Count == candidates.Length
                ? new(HumanConcernMatchState.NoMatch, [], [], evaluated.Order(StringComparer.Ordinal).ToArray(), asked)
                : Unknown(asked, completeInput ? "model-concern-match-unavailable" : "model-concern-input-limit-exceeded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Human concern model matching unavailable: {FailureType}", exception.GetType().Name);
            return Unavailable("model-concern-match-unavailable");
        }
    }

    private static string SerializeInput(string discussion, IReadOnlyList<ModelReviewCandidate> candidates, IReadOnlyList<HumanConcernReference> others) =>
        JsonSerializer.Serialize(new { discussion, candidates, otherConcerns = others }, InputJson);

    private static HumanConcernModelMatchResult? Parse(
        string? text, IReadOnlyList<ModelReviewCandidate> candidates, IReadOnlyList<HumanConcernReference> others)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var stateElement) || stateElement.ValueKind != JsonValueKind.String
                || !ReadIds(root, "matchedFindingIds", out var matched) || !ReadIds(root, "equivalentConcernIds", out var equivalent)
                || !ReadIds(root, "evaluatedCandidateIds", out var evaluated)
                || !root.TryGetProperty("confidence", out var confidenceElement) || confidenceElement.ValueKind != JsonValueKind.Number
                || !confidenceElement.TryGetDouble(out var confidence) || !double.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                return null;
            }

            var candidateIds = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            var concernIds = others.Select(concern => concern.Id).ToHashSet(StringComparer.Ordinal);
            if (matched.Any(id => !candidateIds.Contains(id) || !evaluated.Contains(id, StringComparer.Ordinal))
                || equivalent.Any(id => !concernIds.Contains(id)) || evaluated.Any(id => !candidateIds.Contains(id)))
            {
                return null;
            }

            var state = stateElement.GetString() switch
            {
                "Match" => HumanConcernMatchState.Match,
                "NoMatch" => HumanConcernMatchState.NoMatch,
                _ => HumanConcernMatchState.Unknown,
            };
            if (state == HumanConcernMatchState.Match && matched.Length + equivalent.Length == 0
                || state == HumanConcernMatchState.NoMatch && (matched.Length + equivalent.Length != 0 || !candidateIds.SetEquals(evaluated)))
            {
                return null;
            }

            var rationale = root.TryGetProperty("rationale", out var reason) && reason.ValueKind == JsonValueKind.String
                ? Clip(reason.GetString() ?? "", 300)
                : null;
            return new(state, matched, equivalent, evaluated, true, Confidence: confidence, Rationale: rationale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadIds(JsonElement root, string name, out string[] ids)
    {
        ids = [];
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 1024
            || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 }))
        {
            return false;
        }

        ids = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }

    private static HumanConcernModelMatchResult Unknown(bool asked, string reason) => new(HumanConcernMatchState.Unknown, [], [], [], asked, reason);
    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
