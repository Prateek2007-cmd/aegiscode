// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Stores live metadata without changing the initial disposition or a sealed metric.</summary>
public sealed class CodeInsightPerformanceEvidenceStore(MeisterProPRDbContext dbContext, IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
    : ICodeInsightPerformanceEvidenceStore
{
    private const int MaximumNativeStatusCharacters = 64;

    public async Task<bool> RecordHistoricalInitialDispositionAsync(
        Guid findingId, CodeInsightDispositionRecord outcome, CodeInsightPublicationReceipt receipt,
        string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt)
                            || finding.OutcomeObservedAt != observedAt || outcome.NativeStatus is null
                            || (outcome.ClassifierVersion is not null && outcome.ClassifierConfidence is null)
                            || !CodeInsightCurrentOutcomePolicy.MatchesOutcome(finding, outcome.NativeStatus, outcome, sourceFingerprint)
                            || await db.CodeInsightFindingDispositions.AnyAsync(d => d.CodeInsightFindingId == findingId, ct))
        {
            return false;
        }

        db.CodeInsightFindingDispositions.Add(
            new CodeInsightFindingDisposition
            {
                Id = Guid.CreateVersion7(), CodeInsightFindingId = findingId, Disposition = outcome.Disposition,
                SourceIntent = outcome.SourceIntent, SourceCodeChange = outcome.SourceCodeChange,
                ClassifierVersion = outcome.ClassifierVersion, ClassifierConfidence = outcome.ClassifierConfidence,
                RejectionReason = outcome.RejectionReason, NativeStatus = outcome.NativeStatus, DecidedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    public async Task<CodeInsightEvidenceAcknowledgment> TryRepairHistoricalThreadIdentityAsync(
        Guid findingId, CodeInsightPublicationReceipt receipt, string correlatedThreadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt)
                            || !string.IsNullOrWhiteSpace(finding.ProviderThreadId) || string.IsNullOrWhiteSpace(correlatedThreadId)
                            || string.IsNullOrWhiteSpace(finding.ProviderCommentId))
        {
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);
        }

        finding.ProviderThreadId = correlatedThreadId;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new CodeInsightEvidenceAcknowledgment(true, true);
    }

    public async Task<CodeInsightEvidenceAcknowledgment> TryPreserveHistoricalOutcomeAsync(
        Guid findingId, CodeInsightHistoricalOutcomeObservation observation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, observation.Receipt))
        {
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);
        }

        if (observation.NativeStatus is null)
        {
            return new CodeInsightEvidenceAcknowledgment(false);
        }

        if (finding.OutcomeObservedAt > observation.ObservedAt)
        {
            var currentIntent = CodeInsightNativeResolution.InterpretKnown(finding.NativeStatus);
            return currentIntent == ThreadResolutionIntent.Active || (currentIntent is not null && finding.CurrentDisposition is not null
                                                                                                && (finding.CurrentClassifierVersion is null ||
                                                                                                    finding.CurrentClassifierConfidence is not null))
                ? new CodeInsightEvidenceAcknowledgment(true)
                : new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ClassificationUnavailable);
        }

        if (!observation.ResolutionKnown || observation.NativeStatus.Length > MaximumNativeStatusCharacters ||
            CodeInsightNativeResolution.InterpretKnown(observation.NativeStatus) != observation.Intent)
        {
            return new CodeInsightEvidenceAcknowledgment(false);
        }

        CodeInsightDispositionRecord? retained;
        if (finding.CurrentDisposition is { } current)
        {
            var intent = CodeInsightNativeResolution.InterpretKnown(finding.NativeStatus);
            retained = intent is null
                ? null
                : new CodeInsightDispositionRecord(
                    current, intent.Value, finding.CurrentCodeChange,
                    finding.CurrentClassifierVersion, finding.CurrentClassifierConfidence);
        }
        else if (finding.OutcomeObservedAt is not null)
        {
            // A retained reopen or unavailable current observation supersedes the first-close disposition.
            retained = null;
        }
        else
        {
            var initial = await db.CodeInsightFindingDispositions.AsNoTracking().FirstOrDefaultAsync(d => d.CodeInsightFindingId == findingId, ct);
            retained = initial is null
                ? null
                : new CodeInsightDispositionRecord(
                    initial.Disposition, initial.SourceIntent, initial.SourceCodeChange,
                    initial.ClassifierVersion, initial.ClassifierConfidence, initial.RejectionReason);
        }

        if (retained is null || (retained.ClassifierVersion is not null && retained.ClassifierConfidence is null) ||
            retained.SourceIntent != observation.Intent || retained.SourceCodeChange != observation.CodeChange ||
            (observation.Intent == ThreadResolutionIntent.ClaimsFix && observation.CodeChange == ThreadAnchorCodeChange.Unknown))
        {
            return new CodeInsightEvidenceAcknowledgment(false);
        }

        var changed = false;
        // The receipt-family lock prevents concurrent changes; each retained finding keeps its own judgment.
        if (db.Database.IsRelational())
        {
            await db.Entry(finding).ReloadAsync(ct);
        }

        if (finding.OutcomeObservedAt is null || finding.OutcomeObservedAt <= observation.ObservedAt)
        {
            var scoreChanged = finding.NativeStatus != observation.NativeStatus || finding.CurrentDisposition != retained.Disposition ||
                               finding.CurrentClassifierVersion != retained.ClassifierVersion ||
                               finding.CurrentClassifierConfidence != retained.ClassifierConfidence ||
                               finding.CurrentCodeChange != observation.CodeChange;
            finding.NativeStatus = observation.NativeStatus;
            finding.CurrentDisposition = retained.Disposition;
            finding.CurrentClassifierVersion = retained.ClassifierVersion;
            finding.CurrentClassifierConfidence = retained.ClassifierConfidence;
            finding.CurrentCodeChange = observation.CodeChange;
            finding.OutcomeSourceFingerprint = observation.SourceFingerprint;
            finding.OutcomeObservedAt = observation.ObservedAt;
            if (scoreChanged)
            {
                finding.PerformanceEvidenceUpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new CodeInsightEvidenceAcknowledgment(true, changed);
    }

    public async Task<CodeInsightEvidenceAcknowledgment> AcknowledgeOutcomeAsync(
        Guid findingId, CodeInsightPublicationReceipt receipt, string nativeStatus, string fingerprint,
        DateTimeOffset observedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var finding = await db.CodeInsightFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt))
        {
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);
        }

        if (finding.OutcomeObservedAt is null || finding.OutcomeObservedAt < observedAt ||
            (finding.OutcomeObservedAt == observedAt && (finding.NativeStatus != nativeStatus || finding.OutcomeSourceFingerprint != fingerprint)))
        {
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed);
        }

        var intent = CodeInsightNativeResolution.InterpretKnown(finding.NativeStatus);
        if (intent is null || (intent != ThreadResolutionIntent.Active &&
                               (finding.CurrentDisposition is null ||
                                (finding.CurrentClassifierVersion is not null && finding.CurrentClassifierConfidence is null))))
        {
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.ClassificationUnavailable);
        }

        return new CodeInsightEvidenceAcknowledgment(true);
    }

    /// <inheritdoc />
    public async Task<bool> ObserveUnchangedOutcomeAsync(
        Guid findingId, string nativeStatus, string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default,
        CodeInsightPublicationReceipt? receipt = null)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null)
        {
            return false;
        }

        if (!CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt))
        {
            return true;
        }

        if (finding.OutcomeObservedAt > observedAt)
        {
            return true;
        }

        if (!CodeInsightCurrentOutcomePolicy.MatchesCompleted(finding, nativeStatus, sourceFingerprint))
        {
            return false;
        }

        foreach (var publication in await LoadPublicationsAsync(db, finding, ct))
        {
            if (db.Database.IsRelational())
            {
                await db.Entry(publication).ReloadAsync(ct);
            }

            if (CodeInsightCurrentOutcomePolicy.MatchesCompleted(publication, nativeStatus, sourceFingerprint) &&
                (publication.OutcomeObservedAt is null || publication.OutcomeObservedAt < observedAt))
            {
                publication.OutcomeObservedAt = observedAt;
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    /// <inheritdoc />
    public Task<bool> RecordCurrentOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null)
    {
        return this.RecordOutcomeAsync(findingId, nativeStatus, outcome, observedAt, ct, sourceFingerprint, receipt, false);
    }

    public Task<bool> RecordHistoricalOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null)
    {
        return this.RecordOutcomeAsync(findingId, nativeStatus, outcome, observedAt, ct, sourceFingerprint, receipt, true);
    }

    private async Task<bool> RecordOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct,
        string? sourceFingerprint, CodeInsightPublicationReceipt? receipt, bool historical)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt) || finding.OutcomeObservedAt > observedAt ||
            nativeStatus.Length > MaximumNativeStatusCharacters)
        {
            return false;
        }

        var publications = historical ? new List<CodeInsightFinding> { finding } : await LoadPublicationsAsync(db, finding, ct);
        var unchanged = CodeInsightCurrentOutcomePolicy.MatchesOutcome(finding, nativeStatus, outcome, sourceFingerprint);
        foreach (var publication in publications.Where(row => row.OutcomeObservedAt is null || row.OutcomeObservedAt <= observedAt))
        {
            if (db.Database.IsRelational())
            {
                await db.Entry(publication).ReloadAsync(ct);
            }

            if (publication.OutcomeObservedAt > observedAt)
            {
                continue;
            }

            if (unchanged)
            {
                if (CodeInsightCurrentOutcomePolicy.MatchesOutcome(publication, nativeStatus, outcome, sourceFingerprint) &&
                    publication.OutcomeObservedAt != observedAt)
                {
                    publication.OutcomeObservedAt = observedAt;
                }

                continue;
            }

            CodeInsightCurrentOutcomePolicy.Apply(publication, nativeStatus, outcome, observedAt, sourceFingerprint);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return !unchanged;
    }

    private static async Task<CodeInsightFinding?> LoadLockedFindingAsync(MeisterProPRDbContext db, Guid findingId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var identity = await db.CodeInsightFindings.AsNoTracking().Where(row => row.Id == findingId)
                .Select(row => new
                {
                    row.CodeInsightPullRequestId,
                    row.ProviderScope,
                    row.ProviderThreadId,
                    row.ProviderCommentId,
                }).FirstOrDefaultAsync(ct);
            if (identity is null)
            {
                return null;
            }

            // Lock the complete receipt family in a stable order before checking evidence and observedAt.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM code_insight_findings WHERE id = {findingId} OR (code_insight_pull_request_id = {identity.CodeInsightPullRequestId} AND \"ProviderScope\" = {identity.ProviderScope} AND provider_thread_id IS NOT DISTINCT FROM {identity.ProviderThreadId} AND provider_comment_id IS NOT DISTINCT FROM {identity.ProviderCommentId}) ORDER BY id FOR UPDATE",
                ct);
        }

        var finding = await db.CodeInsightFindings.FirstOrDefaultAsync(row => row.Id == findingId, ct);
        if (finding is not null && db.Database.IsRelational())
        {
            await db.Entry(finding).ReloadAsync(ct);
        }

        return finding;
    }

    private static async Task<List<CodeInsightFinding>> LoadPublicationsAsync(MeisterProPRDbContext db, CodeInsightFinding finding, CancellationToken ct)
    {
        return finding.ProviderThreadId is null
            ? [finding]
            : await db.CodeInsightFindings.Where(row =>
                row.CodeInsightPullRequestId == finding.CodeInsightPullRequestId && row.ProviderScope == finding.ProviderScope
                                                                                 && row.ProviderThreadId == finding.ProviderThreadId &&
                                                                                 row.ProviderCommentId == finding.ProviderCommentId).ToListAsync(ct);
    }
}
