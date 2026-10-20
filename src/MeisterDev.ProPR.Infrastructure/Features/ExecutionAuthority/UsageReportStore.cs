// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Extensibility.Execution;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.ExecutionAuthority;

/// <summary>
///     PostgreSQL store of the usage reports kept for the registered execution authority. Every operation opens its
///     own context, because the AI calls of one execution complete on concurrent tasks.
/// </summary>
/// <remarks>
///     The sequence is an identity column. Every append holds one advisory lock until its transaction ends, so the
///     appends commit in the order of their sequences: a reader that sees a report has seen every report with a lower
///     sequence and can move its cursor past what it read.
/// </remarks>
public sealed class UsageReportStore(
    IDbContextFactory<MeisterProPRDbContext> contexts,
    TimeProvider timeProvider) : IUsageReportStore
{
    /// <summary>The key of the advisory lock every append holds until its transaction ends.</summary>
    internal const string AppendLockKey = "ai-usage-report-append";

    /// <summary>The most reports one read returns.</summary>
    public const int MaxReadLimit = 1000;

    /// <inheritdoc />
    public async Task<UsageReport> AppendAsync(UsageReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var record = new AiUsageReportRecord
        {
            ExecutionKind = report.Execution is { } execution ? (int)execution.Kind : null,
            ExecutionId = report.Execution?.Id,
            ExecutionAttempt = report.Execution?.Attempt,
            TenantId = report.TenantId,
            ClientId = report.ClientId,
            ConnectionId = report.Connection.ConnectionId,
            ConnectionRevision = report.Connection.Revision,
            ProviderKind = report.ProviderKind,
            ModelId = report.ModelId,
            Operation = (int)report.Operation,
            InputTokens = report.InputTokens,
            OutputTokens = report.OutputTokens,
            CachedInputTokens = report.CachedInputTokens,
            CacheWriteTokens = report.CacheWriteTokens,
            ReasoningTokens = report.ReasoningTokens,
            UsageIncomplete = report.UsageIncomplete,
            DurationMilliseconds = (long)report.Duration.TotalMilliseconds,
            CompletedAt = report.CompletedAt,
            RecordedAt = timeProvider.GetUtcNow(),
        };

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await PostgresAdvisoryLocks.AcquireTransactionAsync(db, AppendLockKey, ct).ConfigureAwait(false);
        db.AiUsageReports.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return report with { Sequence = record.Sequence };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageReport>> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxReadLimit);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var records = await db.AiUsageReports.AsNoTracking()
            .Where(r => r.Sequence > afterSequence)
            .OrderBy(r => r.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(ToReport).ToList();
    }

    private static UsageReport ToReport(AiUsageReportRecord record)
    {
        return new UsageReport(
            record.Sequence,
            record is { ExecutionKind: { } kind, ExecutionId: { } id, ExecutionAttempt: { } attempt }
                ? new ExecutionIdentity((ExecutionKind)kind, id, attempt)
                : null,
            record.TenantId,
            record.ClientId,
            new AiConnectionRevision(record.ConnectionId, record.ConnectionRevision),
            record.ProviderKind,
            record.ModelId,
            (AiOperation)record.Operation,
            record.InputTokens,
            record.OutputTokens,
            record.CachedInputTokens,
            record.CacheWriteTokens,
            record.ReasoningTokens,
            record.UsageIncomplete,
            TimeSpan.FromMilliseconds(record.DurationMilliseconds),
            record.CompletedAt);
    }
}
