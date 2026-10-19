// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace MeisterDev.ProPR.CodeInsights.Misses;

internal sealed record ModelOpportunityAssessment(
    bool Applicable,
    bool NotApplicable,
    bool NegativeComplete,
    IReadOnlyList<ModelReviewCandidate> Candidates,
    string Fingerprint,
    string? UnavailableReason);

/// <summary>Qualifies original concern scope against retained execution receipts without I/O.</summary>
internal static class ModelOpportunityEvaluator
{
    private static readonly ConditionalWeakTable<ModelReviewEvidenceSnapshot, ProofSignatures> Signatures = new();

    internal static ModelOpportunityAssessment Assess(
        ModelReviewEvidenceSnapshot snapshot, ModelReviewSource source,
        ReviewConcernAnchor? anchor, string modelId, string logicalName)
    {
        if (anchor is not { IsKnown: true, OriginAt: { } origin })
        {
            return Unknown("model-concern-anchor-unavailable");
        }

        var executions = snapshot.Executions.Where(job => SameSource(job.Source, source) && SameRevision(job, anchor)).ToArray();
        var bindings = Bindings(executions);
        var candidates = new Dictionary<string, ModelReviewCandidate>(StringComparer.Ordinal);
        var identities = new List<string>();
        var signatures = Signatures.GetValue(snapshot, _ => new());
        var applicable = false;
        var complete = snapshot.Complete;
        var assigned = false;
        var allOutside = true;
        var hasEarlierOpportunity = executions.SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? []).Where(file => SameRevision(file, job)))
            .SelectMany(file => file.Passes).Any(pass => CouldBeModel(pass, modelId, logicalName, bindings)
                                                         && pass.AcceptedAt <= origin && pass.CompletedAt <= origin
                                                         && (pass.IsCertified || pass.Disposition ==
                                                             ModelGenerationDisposition.NotApplicable));
        string? unavailable = snapshot.UnavailableReason;
        foreach (var job in executions)
        {
            identities.Add("inventory:" + job.JobId + ":" + job.Manifest?.InventoryComplete);
            foreach (var file in job.Files.Concat(job.Manifest?.PullRequestPasses ?? []))
            {
                var passes = file.Passes.Where(pass => CouldBeModel(pass, modelId, logicalName, bindings)).ToArray();
                if (passes.Length > 0 && !SameRevision(file, job))
                {
                    identities.Add("incompatible-revision:" + signatures.File(file));
                    complete = false;
                    allOutside = false;
                    unavailable ??= "model-proof-revision-unavailable";
                    continue;
                }

                foreach (var pass in passes)
                {
                    // Only this pass's first durable server receipt establishes its observation cutoff.
                    var timely = pass.AcceptedAt is { } accepted && accepted <= origin && pass.CompletedAt is { } completed && completed <= origin;
                    if (hasEarlierOpportunity && IsProvenLater(pass, origin))
                    {
                        continue;
                    }

                    var scopeKnown = job.Manifest is { InventoryComplete: true } && file.InventoryComplete && file.InputComplete;
                    var fileMatches = file.FilePath == anchor.FilePath;
                    var rangeMatches = fileMatches && file.AssignedRanges.Any(range => range.Side == anchor.Side
                                                                                       && range.Start <= anchor.StartLine &&
                                                                                       range.End >= (anchor.EndLine ?? anchor.StartLine));
                    identities.Add(signatures.File(file) + ":" + signatures.Pass(pass));
                    if (timely && pass.IsCertified)
                    {
                        foreach (var candidate in pass.Candidates)
                        {
                            if (candidates.TryGetValue(candidate.Id, out var prior) && prior != candidate)
                            {
                                return Unknown("model-candidate-identity-unavailable");
                            }

                            candidates[candidate.Id] = candidate;
                        }
                    }

                    if (timely && scopeKnown && (pass.IsCertified || pass.Disposition == ModelGenerationDisposition.NotApplicable))
                    {
                        assigned = true;
                    }

                    if (!fileMatches)
                    {
                        // Another positively closed file assignment can establish absence only in a closed file inventory.
                        var target = job.Files.FirstOrDefault(candidate => candidate.FilePath == anchor.FilePath);
                        var closedTarget = target is { InventoryComplete: true, InputComplete: true } && target.AcceptedAt <= origin
                                                                                                      && !target.Passes.Any(candidate =>
                                                                                                          CouldBeModel(
                                                                                                              candidate, modelId, logicalName, bindings));
                        if (job.Manifest is not { InventoryComplete: true }
                            || job.Manifest.Files.Contains(anchor.FilePath!, StringComparer.Ordinal) && !closedTarget)
                        {
                            allOutside = false;
                        }

                        continue;
                    }

                    if (scopeKnown && !rangeMatches && timely && (pass.IsCertified || pass.Disposition == ModelGenerationDisposition.NotApplicable))
                    {
                        continue;
                    }

                    if (pass.Disposition == ModelGenerationDisposition.NotApplicable && timely && scopeKnown)
                    {
                        continue;
                    }

                    allOutside = false;
                    applicable = true;
                    if (!scopeKnown || !rangeMatches)
                    {
                        complete = false;
                        unavailable ??= file.UnavailableReason ?? "model-input-scope-unavailable";
                    }

                    if (!timely || !pass.IsCertified)
                    {
                        complete = false;
                        unavailable ??= timely ? "model-generation-incomplete" : "model-execution-cutoff-unavailable";
                    }
                }
            }
        }

        var fingerprint = ModelReviewCapture.Digest(JsonSerializer.Serialize(new { source, anchor, modelId, logicalName, snapshot.Complete, identities }));
        if (!applicable && assigned && allOutside)
        {
            return new(false, true, false, candidates.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), fingerprint, null);
        }

        return new(
            applicable, false, applicable && complete, candidates.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), fingerprint,
            applicable && complete ? null : unavailable ?? "model-proof-unavailable");

        ModelOpportunityAssessment Unknown(string reason) => new(
            false, false, false, [],
            ModelReviewCapture.Digest(JsonSerializer.Serialize(new { source, anchor, modelId, logicalName, reason })), reason);
    }

    internal static bool SameSource(ModelReviewSource left, ModelReviewSource right) => left.ClientId == right.ClientId
                                                                                        && left.RepositoryId == right.RepositoryId &&
                                                                                        left.PullRequestId == right.PullRequestId &&
                                                                                        left.ProviderScope == right.ProviderScope
                                                                                        && left.ConnectionId is not null &&
                                                                                        left.ConnectionId == right.ConnectionId;

    internal static bool IsProvenLater(ModelReviewPassEvidence pass, DateTimeOffset origin) =>
        pass.StartedAt > origin || pass.CompletedAt > origin || pass.AcceptedAt > origin;

    internal static ModelReviewModelBinding[] Bindings(IEnumerable<ModelReviewExecution> executions) => executions
        .SelectMany(job => job.Files.Concat(job.Manifest?.PullRequestPasses ?? []).SelectMany(file => file.Passes)
            .Where(pass => !string.IsNullOrWhiteSpace(pass.ModelId))
            .Select(pass => new ModelReviewModelBinding(pass.ModelId, pass.LogicalModelName, pass.ConnectionId, pass.ConfiguredModelId))
            .Concat(job.Manifest?.AllowedModels ?? [])).Distinct().ToArray();

    internal static bool CouldBeModel(ModelReviewPassEvidence pass, string model, string logical, IReadOnlyList<ModelReviewModelBinding> bindings)
    {
        if (!string.IsNullOrWhiteSpace(pass.ModelId))
        {
            return pass.ModelId == model && pass.LogicalModelName == logical;
        }

        if (pass.ConfiguredModelId is { } id && id != Guid.Empty)
        {
            var known = bindings.Where(binding => binding.ConfiguredModelId == id
                                                  && (pass.ConnectionId is null || binding.ConnectionId == pass.ConnectionId)).ToArray();
            if (known.Length > 0)
            {
                return known.Any(binding => binding.ModelId == model && binding.LogicalModelName == logical);
            }
        }

        return pass.LogicalModelName.Length == 0 || pass.LogicalModelName == logical;
    }

    private static bool SameRevision(ModelReviewEvidence file, ModelReviewExecution execution) => file.RevisionKey == execution.RevisionKey
                                                                                                  && file.HeadSha == execution.HeadSha &&
                                                                                                  file.BaseSha == execution.BaseSha;

    private static bool SameRevision(ModelReviewExecution execution, ReviewConcernAnchor anchor)
    {
        if (!string.IsNullOrWhiteSpace(anchor.CommitSha))
        {
            return anchor.CommitSha == (anchor.Side == "RIGHT" ? execution.HeadSha : execution.BaseSha)
                   && (anchor.HeadSha is null || execution.HeadSha == anchor.HeadSha)
                   && (anchor.BaseSha is null || execution.BaseSha == anchor.BaseSha);
        }

        return !string.IsNullOrWhiteSpace(anchor.NativeRevisionId) && anchor.NativeRevisionId == execution.RevisionKey;
    }

    private sealed class ProofSignatures
    {
        private readonly Dictionary<ModelReviewEvidence, string> _files = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ModelReviewPassEvidence, string> _passes = new(ReferenceEqualityComparer.Instance);

        internal string Pass(ModelReviewPassEvidence pass)
        {
            if (!this._passes.TryGetValue(pass, out var signature))
            {
                this._passes[pass] = signature = ModelReviewCapture.Digest(JsonSerializer.Serialize(pass));
            }

            return signature;
        }

        internal string File(ModelReviewEvidence file)
        {
            if (!this._files.TryGetValue(file, out var signature))
            {
                this._files[file] = signature = ModelReviewCapture.Digest(
                    JsonSerializer.Serialize(
                        new
                        {
                            file.JobId, file.ProviderScope, file.SourceConnectionId, file.RevisionKey, file.HeadSha, file.BaseSha,
                            file.FilePath, file.InputFingerprint, file.InventoryComplete, file.InputComplete, file.UnavailableReason, file.AssignedRanges,
                        }));
            }

            return signature;
        }
    }
}
