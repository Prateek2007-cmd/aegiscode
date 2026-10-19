// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Loads model metadata within the existing authorized reporting transaction.</summary>
internal static class ReviewerPerformanceModelEvidenceReader
{
    private const int MaximumMetadataBytes = 32 * 1024 * 1024;

    internal static async Task<ReviewerPerformanceModelEvidence> LoadAsync(
        MeisterProPRDbContext db,
        IReadOnlyDictionary<Guid, CodeInsightPullRequest> aggregates, IReadOnlyList<ReviewerPerformanceDailyCount> cells,
        DateTimeOffset capturedAt, CancellationToken ct)
    {
        var ids = aggregates.Keys.ToArray();
        var sources = cells.Select(row => new ModelReviewSource(row.ClientId, row.RepositoryId, row.PullRequestId, row.ProviderScope)).Distinct().ToArray();
        var proof = await new ModelReviewEvidenceReader(db).ReadAsync(sources, capturedAt, ct);
        var bytes = 0L;
        if (db.Database.IsNpgsql() && ids.Length > 0)
        {
            bytes = await db.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(octet_length("Anchor"::text)),0)::bigint AS "Value"
                 FROM code_insight_human_concerns WHERE "CodeInsightPullRequestId"=ANY({ids})
                 """).SingleAsync(ct);
            bytes += await db.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(octet_length(e."CandidateIds"::text)+octet_length(e."EquivalentConcernIds"::text)),0)::bigint AS "Value"
                 FROM code_insight_model_concern_evidence e JOIN code_insight_human_concerns c ON c."Id"=e."HumanConcernId"
                 WHERE c."CodeInsightPullRequestId"=ANY({ids})
                 """).SingleAsync(ct);
        }

        if (bytes > MaximumMetadataBytes)
        {
            return new(new(proof.Executions, false, "model-proof-limit-exceeded"), aggregates, [], [], [], cells);
        }

        var concerns = new List<CodeInsightHumanConcern>();
        var query = db.CodeInsightHumanConcerns.AsNoTracking().Where(row => ids.Contains(row.CodeInsightPullRequestId) && row.ObservedAt <= capturedAt)
            .OrderBy(row => row.Id).Select(row => new CodeInsightHumanConcern
            {
                Id = row.Id, CodeInsightPullRequestId = row.CodeInsightPullRequestId, ProviderScope = row.ProviderScope, ConnectionId = row.ConnectionId,
                CanonicalConcernId = row.CanonicalConcernId, EquivalenceProof = row.EquivalenceProof, Anchor = row.Anchor,
                SourceFingerprint = row.SourceFingerprint, HarvestedAt = row.HarvestedAt, ObservedAt = row.ObservedAt, IsDeleted = row.IsDeleted,
                IsSubstantive = row.IsSubstantive, IsInScope = row.IsInScope, WasActedOn = row.WasActedOn, ThreadResolved = row.ThreadResolved,
                IsHumanConfirmed = row.IsHumanConfirmed, IsSingleConcern = row.IsSingleConcern, JudgementFailed = row.JudgementFailed,
                UnavailableReason = row.UnavailableReason, TypeMembership = row.TypeMembership, Qualifier = row.Qualifier,
                DimensionClassifierVersion = row.DimensionClassifierVersion, DimensionJudgementFailed = row.DimensionJudgementFailed,
                RetainedSourceComplete = row.RetainedSourceComplete,
                LastModelEvidenceRetryAt = row.LastModelEvidenceRetryAt,
            }).Take(ReviewerPerformanceRangeReader.MaximumCells + 1);
        await foreach (var concern in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(concern).Length;
            if (bytes > MaximumMetadataBytes || concerns.Count == ReviewerPerformanceRangeReader.MaximumCells)
            {
                return new(new(proof.Executions, false, "model-proof-limit-exceeded"), aggregates, [], [], [], cells);
            }

            concerns.Add(concern);
        }

        var concernIds = concerns.Select(row => row.Id).ToArray();
        var decisions = new List<CodeInsightModelConcernEvidence>();
        await foreach (var decision in db.CodeInsightModelConcernEvidence.AsNoTracking().Where(row => concernIds.Contains(row.HumanConcernId)
                                                                                                      && row.ObservedAt <= capturedAt).OrderBy(row => row.Id)
                           .Take(ReviewerPerformanceRangeReader.MaximumCells + 1).AsAsyncEnumerable().WithCancellation(ct))
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(decision).Length;
            if (bytes > MaximumMetadataBytes || decisions.Count == ReviewerPerformanceRangeReader.MaximumCells)
            {
                return new(new(proof.Executions, false, "model-proof-limit-exceeded"), aggregates, [], [], [], cells);
            }

            decisions.Add(decision);
        }

        var coverage = await db.CodeInsightHarvestCoverage.AsNoTracking().Where(row => ids.Contains(row.CodeInsightPullRequestId))
            .Take(ReviewerPerformanceRangeReader.MaximumCells + 1).ToListAsync(ct);
        if (coverage.Count > ReviewerPerformanceRangeReader.MaximumCells)
        {
            return new(new(proof.Executions, false, "model-proof-limit-exceeded"), aggregates, [], [], [], cells);
        }

        return new(proof, aggregates, concerns, decisions, coverage, cells);
    }
}
