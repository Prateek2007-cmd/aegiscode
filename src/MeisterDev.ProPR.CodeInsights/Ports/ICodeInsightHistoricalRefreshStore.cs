// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>Source-qualified evidence gaps eligible for bounded historical observation.</summary>
[Flags]
public enum CodeInsightHistoricalGap
{
    None = 0,
    NativeOutcome = 1,
    HarvestCoverage = 2,
    MissFingerprint = 4,
}

/// <summary>A retained namespace and a job carrying its captured routing coordinates.</summary>
public sealed record CodeInsightHistoricalRefreshSource(string ProviderScope, Guid? RepresentativeJobId, CodeInsightHistoricalGap Gaps);

/// <summary>Durably claimed work whose attempt token fences delayed completion.</summary>
public sealed record CodeInsightHistoricalRefreshCandidate(
    Guid AggregateId,
    Guid AttemptId,
    DateTimeOffset AttemptedAt,
    DateTimeOffset LeaseUntil,
    CodeInsightPullRequestKey Key,
    CodeInsightHistoricalGap Gaps,
    IReadOnlyList<CodeInsightHistoricalRefreshSource> Sources);

/// <summary>Claims historical work in short transactions and records positively acknowledged completion.</summary>
public interface ICodeInsightHistoricalRefreshStore
{
    Task<IReadOnlyList<CodeInsightHistoricalRefreshCandidate>> ClaimAsync(
        int maximum, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default);

    Task<bool> RecordResultAsync(
        Guid aggregateId, Guid attemptId, CodeInsightObservationResult observation,
        DateTimeOffset completedAt, TimeSpan retryCooldown, CancellationToken ct = default);
}
