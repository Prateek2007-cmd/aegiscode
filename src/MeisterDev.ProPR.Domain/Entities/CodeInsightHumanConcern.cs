// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>A retained human concern independent of global reviewer-finding overlap exclusion.</summary>
public sealed class CodeInsightHumanConcern
{
    public Guid Id { get; set; }
    public Guid CodeInsightPullRequestId { get; set; }
    public string ProviderScope { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public string NativeIdentity { get; set; } = string.Empty;
    public string ProviderThreadId { get; set; } = string.Empty;
    public Guid CanonicalConcernId { get; set; }
    public string? EquivalenceProof { get; set; }
    public ReviewConcernAnchor? Anchor { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public string EncryptedDiscussion { get; set; } = string.Empty;
    public bool RetainedSourceComplete { get; set; }
    public DateTimeOffset HarvestedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public bool IsSubstantive { get; set; }
    public bool IsInScope { get; set; }
    public bool WasActedOn { get; set; }
    public bool ThreadResolved { get; set; }
    public bool JudgementFailed { get; set; }
    public bool IsDeleted { get; set; }
    public string? ClassifierVersion { get; set; }
    public double? ClassifierConfidence { get; set; }
    public bool? IsHumanConfirmed { get; set; }
    public bool? IsSingleConcern { get; set; }
    public string? UnavailableReason { get; set; }
    public string? DimensionClassifierVersion { get; set; }
    public int FailedDimensionAttempts { get; set; }
    public string TypeMembership { get; set; } = string.Empty;
    public CodeInsightFindingQualifier? Qualifier { get; set; }
    public bool DimensionJudgementFailed { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LastModelEvidenceRetryAt { get; set; }
}
