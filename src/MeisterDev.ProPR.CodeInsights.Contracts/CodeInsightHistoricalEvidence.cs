// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Internal source feedback snapshot independent of public browse DTOs.</summary>
public sealed record CodeInsightNativeFindingSnapshot(
    CodeInsightFindingView Finding,
    CodeInsightPublicationState PublicationState,
    CodeInsightDisposition? CurrentDisposition,
    DateTimeOffset? OutcomeObservedAt)
{
    /// <summary>Real thread observed through unique PR-scoped correlation of the retained comment receipt.</summary>
    public string? CorrelatedThreadId { get; init; }
}

/// <summary>Fresh source signals checked against locked current or initial outcome evidence.</summary>
public sealed record CodeInsightHistoricalOutcomeObservation(
    CodeInsightPublicationReceipt Receipt,
    string NativeStatus,
    ThreadResolutionIntent Intent,
    ThreadAnchorCodeChange CodeChange,
    string SourceFingerprint,
    DateTimeOffset ObservedAt,
    bool ResolutionKnown = true);

/// <summary>Fresh concern evidence checked against a locked retained human judgment.</summary>
public sealed record CodeInsightHistoricalMissObservation(
    string ProviderScope,
    string ThreadId,
    string? FilePath,
    int? LineNumber,
    string Discussion,
    bool ThreadResolved,
    string SourceFingerprint,
    DateTimeOffset ObservedAt,
    bool ResolutionKnown = true);
