// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed partial class FileReviewer
{
    private async Task PrepareModelInventoryAsync(
        ReviewJob job, ChangedFile file, ReviewSystemContext context, FileComplexityTier tier, bool securityFlagged, CancellationToken ct)
    {
        if (context.ModelEvidenceCheckpoint is not { } checkpoint)
        {
            return;
        }

        try
        {
            context.ModelInventoryComplete = false;
            var preparedRuntimes = new Dictionary<int, PreparedReviewPassRuntime>();
            context.PreparedModelPassRuntimes = preparedRuntimes;
            var scope = ModelReviewCapture.Scope(job, file, context);
            context.ModelEvidenceScope = scope;
            var passes = new List<ModelReviewPassEvidence>
                { ModelReviewCapture.Pass(context, 1, DateTimeOffset.UtcNow, ModelGenerationDisposition.Unknown, []) };
            var ordinal = 2;
            var complete = true;
            if (context.EnableMultiPassUnion && context.MultiPassUnionPassCount is { } count)
            {
                var diversity = context.MultiPassDiversity ?? MultiPassDiversity.Default;
                var arms = diversity.ResolveResamplePasses(Math.Max(0, count - 1));
                complete = arms.Count < ModelReviewEvidence.MaximumPasses;
                foreach (var arm in arms.Take(ModelReviewEvidence.MaximumPasses - 1))
                {
                    var pass = context.CloneForPass();
                    pass.ModelId = arm.ModelId ?? context.ModelId;
                    pass.LogicalModelName = null;
                    pass.ActiveLens = arm.Lens;
                    pass.ModelEvidenceScope = scope;
                    passes.Add(
                        ModelReviewCapture.Pass(
                            pass, ordinal++, DateTimeOffset.UtcNow,
                            (arm.Lens is not null ? securityFlagged : IsMultiPassUnionTier(tier))
                                ? ModelGenerationDisposition.Unknown
                                : ModelGenerationDisposition.NotApplicable, []));
                }
            }
            else if (context.EnableMultiPassUnion)
            {
                complete = context.ReviewPasses.Count < ModelReviewEvidence.MaximumPasses;
                foreach (var configured in context.ReviewPasses.Take(ModelReviewEvidence.MaximumPasses - 1))
                {
                    if (configured.Scope == ReviewPassScope.PrWide)
                    {
                        ordinal++;
                        continue;
                    }

                    var resolution = await this.TryResolvePassRuntimeAsync(job, file.Path, configured, ordinal, ct);
                    preparedRuntimes.Add(ordinal, resolution);
                    var pass = context.CloneForPass();
                    pass.ModelId = resolution.Runtime?.Model.RemoteModelId;
                    pass.LogicalModelName = resolution.Runtime?.LogicalModelName ?? configured.LogicalModelName;
                    pass.RuntimeConnectionId = resolution.Runtime?.Connection?.Id;
                    pass.ConfiguredModelId = resolution.Runtime?.Model.Id ?? configured.ConfiguredModelId;
                    pass.ActiveLens = configured.Lens;
                    pass.ModelEvidenceScope = scope;
                    var inScope = configured.Lens switch
                    {
                        ReviewPassLens.Security => securityFlagged,
                        ReviewPassLens.ProRV => proRvPrefilter is not null && IsProRvEligible(file),
                        ReviewPassLens.Inventory => IsProRvEligible(file),
                        _ => IsMultiPassUnionTier(tier),
                    };
                    passes.Add(
                        ModelReviewCapture.Pass(
                            pass, ordinal++, DateTimeOffset.UtcNow,
                            resolution.Runtime is null || inScope ? ModelGenerationDisposition.Unknown : ModelGenerationDisposition.NotApplicable, [],
                            configured.Shadow));
                }
            }

            checkpoint.RecordModelEvidence(
                scope with
                {
                    InventoryComplete = complete, Passes = passes,
                    UnavailableReason = complete ? null : "model-proof-limit-exceeded"
                });
            await jobRepository.UpdateFileResultAsync(checkpoint, ct);
            context.ModelInventoryComplete = complete;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            context.ModelInventoryComplete = false;
            logger.LogWarning("Model assignment inventory unavailable for job {JobId}: {FailureType}", job.Id, exception.GetType().Name);
        }
    }

    private async Task CheckpointGenerationAsync(
        ReviewJob job, ChangedFile file, ReviewSystemContext context,
        int ordinal, DateTimeOffset startedAt, ModelGenerationDisposition disposition, IReadOnlyList<ReviewComment> candidates, bool shadow,
        CancellationToken ct)
    {
        if (context.ModelEvidenceCheckpoint is not { } checkpoint)
        {
            return;
        }

        try
        {
            var scope = ModelReviewCapture.Scope(job, file, context);
            context.ModelEvidenceScope = scope;
            var proof = scope with
            {
                Passes = [ModelReviewCapture.Pass(context, ordinal, startedAt, disposition, candidates, shadow)],
            };
            checkpoint.RecordModelEvidence(proof);
            await jobRepository.UpdateFileResultAsync(checkpoint, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Model generation checkpoint unavailable for job {JobId} at stage {Stage}: {FailureType}",
                job.Id, "generation-retention", exception.GetType().Name);
        }
    }
}
