// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>A bounded model-to-concern decision tied to immutable candidate-set and source fingerprints.</summary>
public sealed class CodeInsightModelConcernEvidence
{
    public Guid Id { get; set; }
    public Guid HumanConcernId { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string LogicalModelName { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string CandidateSetFingerprint { get; set; } = string.Empty;
    public string ClassifierVersion { get; set; } = string.Empty;
    public string Decision { get; set; } = "unknown";
    public IReadOnlyList<string> CandidateIds { get; set; } = [];
    public IReadOnlyList<string> EquivalentConcernIds { get; set; } = [];
    public double? Confidence { get; set; }
    public string? Rationale { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string? UnavailableReason { get; set; }
    public int FailedAttempts { get; set; }
}
