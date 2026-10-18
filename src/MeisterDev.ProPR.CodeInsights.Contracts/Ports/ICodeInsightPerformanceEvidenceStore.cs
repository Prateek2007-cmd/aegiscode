// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Receipt identity observed before current-outcome processing.</summary>
public sealed record CodeInsightPublicationReceipt(string ProviderScope, string? ThreadId, string? CommentId);

/// <summary>Records current analytics evidence independently of immutable initial dispositions.</summary>
public interface ICodeInsightPerformanceEvidenceStore
{
    /// <summary>Creates an initial historical decision only from the exact successful outcome retained under the receipt lock.</summary>
    Task<bool> RecordHistoricalInitialDispositionAsync(
        Guid findingId, CodeInsightDispositionRecord outcome, CodeInsightPublicationReceipt receipt,
        string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <summary>Repairs only a missing native thread identifier after comparing the captured receipt under its lock.</summary>
    Task<CodeInsightEvidenceAcknowledgment> TryRepairHistoricalThreadIdentityAsync(
        Guid findingId, CodeInsightPublicationReceipt receipt, string correlatedThreadId, CancellationToken ct = default)
    {
        return Task.FromResult(new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed));
    }

    /// <summary>Preserves successful historical judgments when locked source signals remain consistent.</summary>
    Task<CodeInsightEvidenceAcknowledgment> TryPreserveHistoricalOutcomeAsync(
        Guid findingId, CodeInsightHistoricalOutcomeObservation observation, CancellationToken ct = default)
    {
        return Task.FromResult(new CodeInsightEvidenceAcknowledgment(false));
    }

    /// <summary>Confirms durable current evidence after processing, including superseding observations.</summary>
    Task<CodeInsightEvidenceAcknowledgment> AcknowledgeOutcomeAsync(
        Guid findingId, CodeInsightPublicationReceipt receipt, string nativeStatus, string fingerprint,
        DateTimeOffset observedAt, CancellationToken ct = default, bool allowFailedAttemptCap = false)
    {
        return Task.FromResult(new CodeInsightEvidenceAcknowledgment(false));
    }

    /// <summary>Advances an unchanged, completed outcome observation under the same receipt-family lock.</summary>
    /// <returns>True when the observation matches retained completed evidence or is older than its watermark; false when current evidence needs processing.</returns>
    Task<bool> ObserveUnchangedOutcomeAsync(
        Guid findingId, string nativeStatus, string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default,
        CodeInsightPublicationReceipt? receipt = null);

    /// <summary>Replaces current outcome evidence, including a reopened thread without a verdict.</summary>
    Task<bool> RecordCurrentOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null);

    /// <summary>Replaces only the intended historical finding while locking its receipt family.</summary>
    Task<bool> RecordHistoricalOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null)
    {
        return RecordCurrentOutcomeAsync(findingId, nativeStatus, outcome, observedAt, ct, sourceFingerprint, receipt);
    }
}
