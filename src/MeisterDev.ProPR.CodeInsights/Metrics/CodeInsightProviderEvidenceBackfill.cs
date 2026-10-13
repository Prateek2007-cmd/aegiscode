// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Coordinates claimed historical source observations without holding database locks during reads.</summary>
public sealed class CodeInsightProviderEvidenceBackfill(
    ICodeInsightHistoricalRefreshStore store,
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ICodeInsightHistoricalObserver observer,
    IScmProviderRegistry providerRegistry,
    IOptionsMonitor<CodeInsightsOptions> options,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    TimeProvider? timeProvider = null) : ICodeInsightProviderEvidenceBackfill
{
    public async Task<CodeInsightProviderEvidenceBackfillResult> SweepAsync(int maximumPullRequests, CancellationToken ct = default)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var settings = options.CurrentValue;
        var cap = Math.Clamp(maximumPullRequests, 1, 250);
        var attempted = 0;
        var completed = 0;
        var unavailable = 0;
        var disabled = 0;
        var sweepEndsAt = clock.GetUtcNow() + settings.ProviderEvidenceSweepBudget;
        using var sweepDeadline = new CancellationTokenSource(settings.ProviderEvidenceSweepBudget, clock);
        using var sweepCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, sweepDeadline.Token);
        while (attempted < cap)
        {
            ct.ThrowIfCancellationRequested();
            if (sweepDeadline.IsCancellationRequested || clock.GetUtcNow() >= sweepEndsAt)
            {
                break;
            }

            IReadOnlyList<CodeInsightHistoricalRefreshCandidate> candidates;
            try
            {
                candidates = await store.ClaimAsync(1, clock.GetUtcNow(), settings.ProviderEvidenceLease, sweepCancellation.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (sweepDeadline.IsCancellationRequested)
            {
                break;
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var candidate = candidates[0];
            attempted++;
            var leaseEndsAt = candidate.LeaseUntil - TimeSpan.FromSeconds(5);
            var candidateEndsAt = leaseEndsAt < sweepEndsAt ? leaseEndsAt : sweepEndsAt;
            var remaining = candidateEndsAt - clock.GetUtcNow();
            var expired = remaining <= TimeSpan.Zero;
            using var candidateDeadline = new CancellationTokenSource(expired ? TimeSpan.Zero : remaining, clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sweepCancellation.Token, candidateDeadline.Token);
            CodeInsightObservationResult result;
            try
            {
                if (expired)
                {
                    result = new CodeInsightObservationResult(
                        CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded);
                }
                else
                {
                    result = await this.ObserveCandidateAsync(candidate, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (clock.GetUtcNow() >= candidateEndsAt)
                    {
                        expired = true;
                        result = new CodeInsightObservationResult(
                            CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                expired = true;
                result = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded);
            }
            catch (Exception)
            {
                result = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed);
            }

            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || clock.GetUtcNow() >= candidateEndsAt)
            {
                expired = true;
                result = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded);
            }

            if (await store.RecordResultAsync(
                    candidate.AggregateId, candidate.AttemptId, result,
                    clock.GetUtcNow(), settings.ProviderEvidenceRetryCooldown, ct) && result.Status == CodeInsightObservationStatus.Observed)
            {
                completed++;
            }
            else if (result.Status == CodeInsightObservationStatus.Disabled)
            {
                disabled++;
            }
            else
            {
                unavailable++;
            }

            if (expired || sweepDeadline.IsCancellationRequested || clock.GetUtcNow() >= sweepEndsAt)
            {
                break;
            }
        }

        return new CodeInsightProviderEvidenceBackfillResult(attempted, completed, unavailable, disabled);
    }

    private async Task<CodeInsightObservationResult> ObserveCandidateAsync(CodeInsightHistoricalRefreshCandidate candidate, CancellationToken ct)
    {
        var enabled = await gate.IsCollectionEnabledAsync(candidate.Key.ClientId, ct);
        ct.ThrowIfCancellationRequested();
        if (!enabled)
        {
            return new CodeInsightObservationResult(CodeInsightObservationStatus.Disabled);
        }

        if (candidate.Sources.Count == 0)
        {
            return new CodeInsightObservationResult(
                CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable);
        }

        CodeInsightObservationResult? unavailable = null;
        DateTimeOffset? observedAt = null;
        foreach (var source in candidate.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!await gate.IsCollectionEnabledAsync(candidate.Key.ClientId, ct))
            {
                return new CodeInsightObservationResult(CodeInsightObservationStatus.Disabled);
            }

            await using var created = contextFactory is null ? null : await contextFactory.CreateDbContextAsync(ct);
            var db = created ?? dbContext;
            var job = source.RepresentativeJobId is null
                ? null
                : await db.ReviewJobs.AsNoTracking()
                    .FirstOrDefaultAsync(
                        row => row.Id == source.RepresentativeJobId && row.ClientId == candidate.Key.ClientId
                                                                    && row.RepositoryId == candidate.Key.RepositoryId &&
                                                                    row.PullRequestId == candidate.Key.PullRequestId, ct);
            if (job is null || string.IsNullOrWhiteSpace(source.ProviderScope)
                            || ProviderSourceIdentity.FromReviewJob(job, providerRegistry.GetSourceIdentityPolicy(job.Provider)).Value != source.ProviderScope)
            {
                unavailable ??= new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable);
                continue;
            }

            var result = await observer.RefreshAsync(
                new CodeInsightProviderEvidenceTarget(candidate.Key, job.Provider, job.OrganizationUrl, job.ProjectId, source.ProviderScope), ct);
            ct.ThrowIfCancellationRequested();
            if (result.Status != CodeInsightObservationStatus.Observed || result.ObservedAt is null)
            {
                unavailable ??= result.Status == CodeInsightObservationStatus.Observed
                    ? new CodeInsightObservationResult(
                        CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed)
                    : result;
            }
            else
            {
                observedAt = observedAt is null || result.ObservedAt < observedAt ? result.ObservedAt : observedAt;
            }
        }

        return unavailable ?? new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, observedAt);
    }
}
