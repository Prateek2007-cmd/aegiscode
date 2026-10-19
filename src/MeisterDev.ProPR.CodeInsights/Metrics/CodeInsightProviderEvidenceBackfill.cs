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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Coordinates claimed historical source observations without holding database locks during reads.</summary>
public sealed partial class CodeInsightProviderEvidenceBackfill(
    ICodeInsightHistoricalRefreshStore store,
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ICodeInsightHistoricalObserver observer,
    IScmProviderRegistry providerRegistry,
    IOptionsMonitor<CodeInsightsOptions> options,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    TimeProvider? timeProvider = null,
    ILogger<CodeInsightProviderEvidenceBackfill>? logger = null) : ICodeInsightProviderEvidenceBackfill
{
    private readonly ILogger _logger = logger ?? NullLogger<CodeInsightProviderEvidenceBackfill>.Instance;

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
                        CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded,
                        Stage: CodeInsightObservationStage.ProcessingDeadline);
                }
                else
                {
                    result = await this.ObserveCandidateAsync(candidate, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (clock.GetUtcNow() >= candidateEndsAt)
                    {
                        expired = true;
                        result = new CodeInsightObservationResult(
                            CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded,
                            Stage: CodeInsightObservationStage.ProcessingDeadline);
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
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded,
                    Stage: CodeInsightObservationStage.ProcessingDeadline);
            }
            catch (Exception ex)
            {
                LogUnexpectedFailure(this._logger, candidate.AggregateId, candidate.AttemptId, ex.GetType().FullName ?? ex.GetType().Name, ex.HResult);
                result = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProviderReadFailed,
                    Stage: CodeInsightObservationStage.ProviderRead);
            }

            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || clock.GetUtcNow() >= candidateEndsAt)
            {
                expired = true;
                result = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.ProcessingDeadlineExceeded,
                    Stage: CodeInsightObservationStage.ProcessingDeadline);
            }

            var recordedAt = clock.GetUtcNow();
            var acknowledged = await store.RecordResultAsync(
                candidate.AggregateId, candidate.AttemptId, result,
                recordedAt, settings.ProviderEvidenceRetryCooldown, ct);
            if (!acknowledged || result.Status != CodeInsightObservationStatus.Observed)
            {
                var reason = result.Status == CodeInsightObservationStatus.Disabled
                    ? "CollectionDisabled"
                    : (result.UnavailableReason ?? CodeInsightObservationUnavailableReason.EvidenceRetentionFailed).ToString();
                var disposition = result.Status == CodeInsightObservationStatus.Disabled ? "collection-disabled"
                    : CodeInsightHistoricalRecoveryPolicy.RequiresRepair(reason) ? "repair-required" : "retryable";
                LogAttemptUnavailable(
                    this._logger, candidate.AggregateId, candidate.AttemptId, candidate.Key.ClientId,
                    candidate.Key.RepositoryId, candidate.Key.PullRequestId, result.Stage ?? CodeInsightObservationStage.Completion,
                    reason, disposition, acknowledged,
                    acknowledged && disposition == "retryable" ? recordedAt + settings.ProviderEvidenceRetryCooldown : null);
            }

            if (acknowledged && result.Status == CodeInsightObservationStatus.Observed)
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
                CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable,
                Stage: CodeInsightObservationStage.SourceLookup);
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
                var missing = new CodeInsightObservationResult(
                    CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable,
                    Stage: CodeInsightObservationStage.SourceLookup);
                unavailable = CodeInsightHistoricalRecoveryPolicy.PreferFailure(unavailable, missing);
                this.LogSourceFailure(candidate, source.ProviderScope, missing);
                continue;
            }

            var result = await observer.RefreshAsync(
                new CodeInsightProviderEvidenceTarget(candidate.Key, job.Provider, job.OrganizationUrl, job.ProjectId, source.ProviderScope), ct);
            ct.ThrowIfCancellationRequested();
            if (result.Status != CodeInsightObservationStatus.Observed || result.ObservedAt is null)
            {
                var failure = result.Status == CodeInsightObservationStatus.Observed
                    ? new CodeInsightObservationResult(
                        CodeInsightObservationStatus.Unavailable, UnavailableReason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed,
                        Stage: CodeInsightObservationStage.ObservationAcknowledgment)
                    : result;
                unavailable = CodeInsightHistoricalRecoveryPolicy.PreferFailure(unavailable, failure);
                this.LogSourceFailure(candidate, source.ProviderScope, failure);
            }
            else
            {
                observedAt = observedAt is null || result.ObservedAt < observedAt ? result.ObservedAt : observedAt;
            }
        }

        return unavailable ?? new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, observedAt);
    }

    private void LogSourceFailure(CodeInsightHistoricalRefreshCandidate candidate, string scope, CodeInsightObservationResult result)
    {
        if (result.Status != CodeInsightObservationStatus.Unavailable)
        {
            return;
        }

        var reason = result.UnavailableReason ?? CodeInsightObservationUnavailableReason.ProviderReadFailed;
        LogSourceUnavailable(
            this._logger, candidate.AggregateId, candidate.AttemptId, candidate.Key.ClientId, candidate.Key.RepositoryId,
            candidate.Key.PullRequestId, SafeScope(scope), result.Stage ?? CodeInsightObservationStage.ProviderRead, reason,
            CodeInsightHistoricalRecoveryPolicy.RequiresRepair(reason.ToString()) ? "repair-required" : "retryable");
    }

    private static string SafeScope(string scope)
    {
        var separator = scope.IndexOf(':');
        return separator > 0 && Enum.TryParse<MeisterDev.ProPR.Domain.Enums.ScmProvider>(scope[..separator], out var provider) && Enum.IsDefined(provider)
               && Uri.TryCreate(scope[(separator + 1)..], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? $"{provider}:{uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/')}"
            : "Unavailable";
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Historical source observation failed for aggregate {AggregateId}, attempt {AttemptId}, client {ClientId}, repository {RepositoryId}, PR {PullRequestId}, source {ProviderScope}, stage {Stage}: {Reason}; recovery {RecoveryDisposition}")]
    private static partial void LogSourceUnavailable(
        ILogger logger, Guid aggregateId, Guid attemptId, Guid clientId, string repositoryId,
        long pullRequestId, string providerScope, CodeInsightObservationStage stage, CodeInsightObservationUnavailableReason reason,
        string recoveryDisposition);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Historical attempt {AttemptId} for aggregate {AggregateId}, client {ClientId}, repository {RepositoryId}, PR {PullRequestId} is unavailable at {Stage}: {Reason}; recovery {RecoveryDisposition}, result retained {Acknowledged}, next retry {NextRetryAt}")]
    private static partial void LogAttemptUnavailable(
        ILogger logger, Guid aggregateId, Guid attemptId, Guid clientId, string repositoryId,
        long pullRequestId, CodeInsightObservationStage stage, string reason, string recoveryDisposition, bool acknowledged, DateTimeOffset? nextRetryAt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Historical attempt {AttemptId} for aggregate {AggregateId} failed; exception type {ExceptionType}, error code {ErrorCode}")]
    private static partial void LogUnexpectedFailure(ILogger logger, Guid aggregateId, Guid attemptId, string exceptionType, int errorCode);
}
