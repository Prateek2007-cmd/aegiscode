// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One stored usage report for the registered execution authority. Rows are only appended; the identity column
///     is the report's sequence.
/// </summary>
public sealed class AiUsageReportRecord
{
    public long Sequence { get; set; }

    /// <summary>The execution kind as its contract value, or null for a call outside an execution.</summary>
    public int? ExecutionKind { get; set; }

    public Guid? ExecutionId { get; set; }

    public int? ExecutionAttempt { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? ClientId { get; set; }

    public Guid ConnectionId { get; set; }

    public long ConnectionRevision { get; set; }

    public string ProviderKind { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    /// <summary>The operation as its contract value.</summary>
    public int Operation { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long CacheWriteTokens { get; set; }

    public long ReasoningTokens { get; set; }

    public bool UsageIncomplete { get; set; }

    public long DurationMilliseconds { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
