// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Retains collection completeness independently of qualifying miss counts.</summary>
public sealed partial class CodeInsightHarvestCoverageRecorder(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ReviewerPerformanceCountProjector projector,
    ILogger<CodeInsightHarvestCoverageRecorder> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    IServiceScopeFactory? scopeFactory = null) : ICodeInsightHarvestCoverageRecorder
{
    private const int MaximumWriteAttempts = 3;

    public async Task<CodeInsightEvidenceAcknowledgment> RecordAcknowledgedAsync(
        CodeInsightPullRequestKey key, string providerScope, bool allHumanThreadsResolved, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        if (!await gate.IsCollectionEnabledAsync(key.ClientId, ct))
        {
            return new CodeInsightEvidenceAcknowledgment(false);
        }

        await this.RecordCoreAsync(key, providerScope, allHumanThreadsResolved, observedAt, ct, true, true);
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var aggregateId = await lease.Context.CodeInsightPullRequests.AsNoTracking().Where(row => row.ClientId == key.ClientId
                                                                                                  && row.RepositoryId == key.RepositoryId &&
                                                                                                  row.PullRequestId == key.PullRequestId)
            .Select(row => (Guid?)row.Id).SingleOrDefaultAsync(ct);
        var coverage = await lease.Context.CodeInsightHarvestCoverage.AsNoTracking().FirstOrDefaultAsync(
            row =>
                row.CodeInsightPullRequestId == aggregateId && row.ProviderScope == providerScope, ct);
        return new CodeInsightEvidenceAcknowledgment(coverage is { EnumerationComplete: true } && coverage.ObservedAt >= observedAt);
    }

    public Task RecordAsync(
        CodeInsightPullRequestKey key, string providerScope, bool allHumanThreadsResolved, DateTimeOffset observedAt, CancellationToken ct = default,
        bool enumerationComplete = true)
    {
        return this.RecordCoreAsync(key, providerScope, allHumanThreadsResolved, observedAt, ct, enumerationComplete, false);
    }

    private async Task RecordCoreAsync(
        CodeInsightPullRequestKey key, string providerScope, bool allHumanThreadsResolved, DateTimeOffset observedAt, CancellationToken ct,
        bool enumerationComplete, bool requireExistingAggregate)
    {
        try
        {
            await using var childScope = scopeFactory?.CreateAsyncScope();
            var callGate = childScope?.ServiceProvider.GetRequiredService<ICodeInsightsCollectionGate>() ?? gate;
            if (!await callGate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return;
            }

            for (var attempt = 0; attempt < MaximumWriteAttempts; attempt++)
            {
                await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
                var db = lease.Context;
                Guid? aggregate;
                try
                {
                    aggregate = await RetainAsync(
                        db, key, providerScope, allHumanThreadsResolved, observedAt, enumerationComplete, requireExistingAggregate, ct);
                }
                catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                          exception.InnerException is PostgresException { SqlState: "23505" })
                {
                    if (contextFactory is null)
                    {
                        db.ChangeTracker.Clear();
                    }

                    continue;
                }

                if (aggregate is not null)
                {
                    await projector.ProjectPullRequestAsync(aggregate.Value, ct);
                }

                return;
            }
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, key.ClientId, exception);
        }
    }

    private static async Task<Guid?> RetainAsync(
        MeisterProPRDbContext db, CodeInsightPullRequestKey key, string providerScope,
        bool allHumanThreadsResolved, DateTimeOffset observedAt, bool enumerationComplete, bool requireExistingAggregate, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await CodeInsightAggregateWriteLock.AcquireAsync(db, key, ct);

        var pr = await LoadAggregateAsync(db, key, observedAt, requireExistingAggregate, ct);
        if (pr is null)
        {
            return null;
        }

        var coverage = await db.CodeInsightHarvestCoverage.FirstOrDefaultAsync(
            row => row.CodeInsightPullRequestId == pr.Id && row.ProviderScope == providerScope, ct);
        if (coverage is not null && db.Database.IsRelational())
        {
            await db.Entry(coverage).ReloadAsync(ct);
        }

        if (coverage is not null && (coverage.ObservedAt > observedAt || (coverage.ObservedAt == observedAt
                                                                          && coverage.AllHumanThreadsResolved == allHumanThreadsResolved &&
                                                                          coverage.EnumerationComplete == enumerationComplete)))
        {
            return null;
        }

        if (coverage is null)
        {
            coverage = new CodeInsightHarvestCoverage
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = pr.Id,
                ProviderScope = providerScope,
            };
            db.CodeInsightHarvestCoverage.Add(coverage);
        }

        ApplyObservation(pr, coverage, providerScope, allHumanThreadsResolved, observedAt, enumerationComplete);

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return pr.Id;
    }

    private static async Task<CodeInsightPullRequest?> LoadAggregateAsync(
        MeisterProPRDbContext db, CodeInsightPullRequestKey key,
        DateTimeOffset observedAt, bool requireExistingAggregate, CancellationToken ct)
    {
        var pr = await db.CodeInsightPullRequests.FirstOrDefaultAsync(
            row => row.ClientId == key.ClientId && row.RepositoryId == key.RepositoryId && row.PullRequestId == key.PullRequestId, ct);
        if (pr is null)
        {
            if (requireExistingAggregate)
            {
                return null;
            }

            pr = new CodeInsightPullRequest
            {
                Id = Guid.CreateVersion7(),
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                CreatedAt = observedAt,
                LastActivityAt = observedAt,
                UpdatedAt = observedAt,
            };
            db.CodeInsightPullRequests.Add(pr);
        }
        else if (db.Database.IsRelational())
        {
            await db.Entry(pr).ReloadAsync(ct);
        }

        return pr;
    }

    private static void ApplyObservation(
        CodeInsightPullRequest pr, CodeInsightHarvestCoverage coverage, string providerScope,
        bool allHumanThreadsResolved, DateTimeOffset observedAt, bool enumerationComplete)
    {
        coverage.ObservedAt = observedAt;
        coverage.AllHumanThreadsResolved = allHumanThreadsResolved;
        coverage.EnumerationComplete = enumerationComplete;
        if (pr.MissHarvestObservedAt is null || pr.MissHarvestObservedAt <= observedAt)
        {
            pr.MissHarvestObservedAt = observedAt;
            pr.MissHarvestSettled = allHumanThreadsResolved;
            pr.MissHarvestProviderScope = providerScope;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer performance harvest coverage could not be retained for client {ClientId}")]
    private static partial void LogFailed(ILogger logger, Guid clientId, Exception exception);
}
