// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Identifies the captured provider source whose retained evidence needs observation.</summary>
public sealed record CodeInsightProviderEvidenceTarget(
    CodeInsightPullRequestKey Key,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    string ProviderScope,
    Guid ConnectionId = default);

/// <summary>Distinguishes completed source retention from unavailable or disabled collection.</summary>
public enum CodeInsightObservationStatus
{
    Observed,
    Unavailable,
    Disabled,
}

/// <summary>Bounded reasons that historical source observation could not complete.</summary>
public enum CodeInsightObservationUnavailableReason
{
    ProviderConnectionUnavailable,
    ProviderReadFailed,
    IncompleteEnumeration,
    OwnershipUnavailable,
    ProviderIdentityUnavailable,
    EvidenceRetentionFailed,
    ClassificationUnavailable,
    ProcessingDeadlineExceeded,
    SourceCoordinatesUnavailable,
    HistoricalCapabilityUnsupported,
    ObserverUnavailable,
}

/// <summary>Identifies the bounded operation that could not retain historical evidence.</summary>
public enum CodeInsightObservationStage
{
    SourceLookup,
    ObserverDependencies,
    CapturedCoordinates,
    ConnectionLookup,
    OwnershipLookup,
    ProviderRead,
    ThreadCorrelation,
    HumanClassification,
    HumanEvidenceRetention,
    OutcomeEvidenceRetention,
    OutcomeAcknowledgment,
    CoverageRetention,
    ObservationAcknowledgment,
    ProcessingDeadline,
    Completion,
}

/// <summary>Reports whether every required observation was positively retained.</summary>
public sealed record CodeInsightObservationResult(
    CodeInsightObservationStatus Status,
    DateTimeOffset? ObservedAt = null,
    CodeInsightObservationUnavailableReason? UnavailableReason = null,
    CodeInsightObservationStage? Stage = null);

/// <summary>Retains current observation behavior and supports preservation during historical collection.</summary>
public enum CodeInsightObservationMode
{
    Current,
    HistoricalBackfill,
}

/// <summary>Acknowledges evidence retention independently of changes to score inputs.</summary>
public sealed record CodeInsightEvidenceAcknowledgment(
    bool Retained,
    bool Changed = false,
    CodeInsightObservationUnavailableReason? Reason = null,
    CodeInsightObservationStage? Stage = null);

/// <summary>Collects provider evidence for a captured historical source without modifying review output.</summary>
public interface ICodeInsightHistoricalObserver
{
    Task<CodeInsightObservationResult> RefreshAsync(CodeInsightProviderEvidenceTarget target, CancellationToken ct = default);
}
