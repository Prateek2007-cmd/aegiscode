// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Builds bounded analytics checkpoints from actual generation inputs and outputs.</summary>
public static class ModelReviewCapture
{
    public static string Fingerprint(ChangedFile file) => Digest(file.Path + "\0" + file.FullContent + "\0" + file.UnifiedDiff);
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static ModelReviewEvidence Scope(ReviewJob job, ChangedFile file, ReviewSystemContext context)
    {
        var ranges = new List<ModelReviewRange>();
        foreach (Match match in Regex.Matches(
                     file.UnifiedDiff, @"(?m)^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            AddRange(1, 2, "LEFT");
            AddRange(3, 4, "RIGHT");
            if (ranges.Count > ModelReviewEvidence.MaximumRanges)
            {
                break;
            }

            void AddRange(int startIndex, int countIndex, string side)
            {
                if (int.TryParse(match.Groups[startIndex].Value, out var start)
                    && int.TryParse(match.Groups[countIndex].Success ? match.Groups[countIndex].Value : "1", out var count)
                    && start > 0 && count > 0 && (long)start + count - 1 <= int.MaxValue)
                {
                    ranges.Add(new(start, start + count - 1, side));
                }
            }
        }

        return new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = job.RetryCount + 1,
            LeaseGeneration = context.CapturedLeaseGeneration ?? (job.LeaseGeneration > 0 ? job.LeaseGeneration : null), ClientId = job.ClientId,
            RepositoryId = job.RepositoryId,
            PullRequestId = job.PullRequestId, ProviderScope = context.ProviderScope ?? string.Empty,
            SourceConnectionId = context.SourceConnectionId,
            RevisionKey = context.CapturedRevisionKey ?? ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
            HeadSha = job.RevisionHeadSha, BaseSha = job.RevisionBaseSha, FilePath = file.Path,
            AssignedRanges = ranges, InputFingerprint = Fingerprint(file), InventoryComplete = context.ModelInventoryComplete,
            InputComplete = !file.IsBinary && ranges.Count > 0 && ranges.Count <= ModelReviewEvidence.MaximumRanges,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ModelReviewPassEvidence Pass(
        ReviewSystemContext context, int ordinal, DateTimeOffset startedAt,
        ModelGenerationDisposition disposition, IReadOnlyList<ReviewComment> candidates, bool shadow = false, string kind = "generation")
    {
        var scope = context.ModelEvidenceScope ?? context.ModelEvidenceCheckpoint?.ModelEvidence;
        var key = Digest(
            $"{scope?.JobId ?? context.ModelEvidenceCheckpoint?.JobId}\0{scope?.FilePath ?? context.ModelEvidenceCheckpoint?.FilePath}\0{scope?.Attempt}\0{scope?.LeaseGeneration}\0{scope?.InputFingerprint}\0{ordinal}\0{context.ModelId}\0{context.LogicalModelName}\0{context.ActiveLens}\0{kind}\0{shadow}");
        var boundedCandidates = candidates.Count <= ModelReviewEvidence.MaximumCandidates
                                && candidates.All(comment => comment is not null && comment.Message is { Length: > 0 and <= 16_384 } &&
                                                             !string.IsNullOrWhiteSpace(comment.Message)
                                                             && comment.FilePath is null or { Length: <= 2048 })
                                && candidates.Sum(comment =>
                                    (long)comment.Message.Length + (comment.FilePath ?? scope?.FilePath ?? string.Empty).Length + 64) <=
                                ModelReviewEvidence.MaximumCandidateCharacters;
        return new ModelReviewPassEvidence
        {
            JobId = scope?.JobId ?? context.ModelEvidenceCheckpoint?.JobId ?? Guid.Empty,
            PassKey = key, ModelId = context.ModelId ?? string.Empty, LogicalModelName = context.LogicalModelName ?? string.Empty,
            ConnectionId = context.RuntimeConnectionId, ConfiguredModelId = context.ConfiguredModelId,
            Attempt = scope?.Attempt ?? 0, LeaseGeneration = scope?.LeaseGeneration,
            Ordinal = ordinal, Lens = context.ActiveLens, Shadow = shadow, Kind = kind, StartedAt = startedAt,
            CompletedAt = disposition == ModelGenerationDisposition.Unknown ? null : DateTimeOffset.UtcNow,
            Disposition = boundedCandidates ? disposition : ModelGenerationDisposition.Unknown,
            CandidateSetComplete = boundedCandidates && disposition == ModelGenerationDisposition.Completed,
            Candidates = boundedCandidates
                ? candidates.Select((comment, index) => new ModelReviewCandidate(
                    Digest(key + ":" + index),
                    comment.FilePath ?? scope?.FilePath ?? string.Empty, comment.LineNumber, comment.Message)).ToArray()
                : [],
        };
    }
}
