// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Dispositions;

/// <summary>Records finding outcomes from resolution signals or a bounded discussion judgment.</summary>
/// <remarks>Collection failures do not stop the crawl. Initial dispositions and current outcomes have separate persistence guards.</remarks>
public sealed partial class CodeInsightDispositionService(
    IScmProviderCompatibilityCodec compatibilityCodec,
    ICodeInsightFindingStore findingStore,
    ICodeInsightDispositionStore dispositionStore,
    IDisregardedFindingClassifier classifier,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightDispositionService> logger,
    ICodeInsightRollupProjector? rollupProjector = null,
    ICodeInsightPerformanceEvidenceStore? performanceEvidence = null) : ICodeInsightDispositionService
{
    private CodeInsightDiscussionBatch? _batch;
    private IReadOnlyDictionary<Guid, CodeInsightDispositionRecord?>? _batchDispositions;

    private bool MatchesBatch(ThreadResolvedDomainEvent evt) => this._batch is not null &&
                                                                this._batch.Key.ClientId == evt.ClientId && this._batch.Key.RepositoryId == evt.RepositoryId &&
                                                                this._batch.Key.PullRequestId == evt.PullRequestId &&
                                                                this._batch.ProviderScope == evt.ProviderScope;

    public string DependencyVersion => classifier.ClassifierVersion;

    public async Task PrepareDiscussionBatchAsync(CodeInsightDiscussionBatch batch, CancellationToken ct = default)
    {
        this._batch = batch;
        this._batchDispositions = await dispositionStore.GetDispositionsAsync(batch.Findings.Select(finding => finding.Id).ToArray(), ct);
    }

    public Task<CodeInsightEvidenceAcknowledgment> ObserveCurrentAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
        => this.ObserveAsync(evt, null, ct);

    public Task<CodeInsightEvidenceAcknowledgment> ObserveHistoricalAsync(
        CodeInsightNativeFindingSnapshot finding, ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        this._batch = null;
        this._batchDispositions = null;
        return this.ObserveAsync(evt, finding, ct);
    }

    public async Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        await this.ObserveAsync(evt, null, ct);
    }

    private async Task<CodeInsightEvidenceAcknowledgment> ObserveAsync(
        ThreadResolvedDomainEvent evt, CodeInsightNativeFindingSnapshot? historicalFinding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return new CodeInsightEvidenceAcknowledgment(false);
            }

            var source = await this.ResolveFindingAsync(evt, historicalFinding, ct);
            if (source.Failure is not null)
            {
                return source.Failure;
            }

            var finding = source.Finding!;
            var historical = historicalFinding is not null;
            var existing = this.MatchesBatch(evt) && this._batchDispositions is not null && historicalFinding is null
                ? this._batchDispositions.GetValueOrDefault(finding.Id)
                : await dispositionStore.GetDispositionAsync(finding.Id, ct);
            var receipt = new CodeInsightPublicationReceipt(finding.ProviderScope, finding.ProviderThreadId, finding.ProviderCommentId);
            var fingerprint = this.OutcomeFingerprint(evt);
            var reused = await this.TryReuseOutcomeAsync(evt, finding, existing, receipt, fingerprint, historical, ct);
            if (reused is not null)
            {
                return reused;
            }

            var isOpen = evt.NativeStatus is not null &&
                         !ThreadResolutionStatusInterpreter.IsResolved(compatibilityCodec.DecodeStoredThreadResolution(evt.NativeStatus));
            var record = isOpen ? null : await this.ResolveDispositionAsync(evt, finding, ct) with { NativeStatus = evt.NativeStatus };
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return new CodeInsightEvidenceAcknowledgment(false);
            }

            return await this.RetainOutcomeAsync(evt, finding, record, receipt, fingerprint, historical, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, ex);
            return new CodeInsightEvidenceAcknowledgment(false, Reason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed);
        }
    }

    private async Task<DispositionSource> ResolveFindingAsync(
        ThreadResolvedDomainEvent evt, CodeInsightNativeFindingSnapshot? historicalFinding, CancellationToken ct)
    {
        var finding = historicalFinding?.Finding ?? (this.MatchesBatch(evt)
            ? this._batch!.Findings.Where(finding => finding.ProviderThreadId == evt.ThreadId && finding.ProviderScope == evt.ProviderScope)
                .OrderBy(finding => finding.CreatedAt ?? finding.ObservedAt).ThenBy(finding => finding.Id).FirstOrDefault()
            : await findingStore.FindByProviderThreadAsync(evt.ClientId, evt.RepositoryId, evt.PullRequestId, evt.ThreadId, ct, evt.ProviderScope));
        if (finding is null)
        {
            LogNoMatchingFinding(logger, evt.ThreadId, evt.ClientId);
            return new DispositionSource(null, IdentityUnavailable());
        }

        if (historicalFinding is null)
        {
            return new DispositionSource(finding, null);
        }

        if (finding.ProviderScope != evt.ProviderScope || CodeInsightNativeResolution.InterpretKnown(evt.NativeStatus) != evt.Intent ||
            performanceEvidence is null)
        {
            return new DispositionSource(null, IdentityUnavailable());
        }

        if (string.IsNullOrWhiteSpace(finding.ProviderThreadId))
        {
            if (historicalFinding.CorrelatedThreadId != evt.ThreadId || string.IsNullOrWhiteSpace(evt.ThreadId))
            {
                return new DispositionSource(null, IdentityUnavailable());
            }

            var repaired = await performanceEvidence.TryRepairHistoricalThreadIdentityAsync(
                finding.Id,
                new CodeInsightPublicationReceipt(finding.ProviderScope, finding.ProviderThreadId, finding.ProviderCommentId), evt.ThreadId, ct);
            if (!repaired.Retained)
            {
                return new DispositionSource(null, repaired);
            }

            finding = finding with { ProviderThreadId = evt.ThreadId };
        }

        return finding.ProviderThreadId == evt.ThreadId ? new DispositionSource(finding, null) : new DispositionSource(null, IdentityUnavailable());
    }

    private string OutcomeFingerprint(ThreadResolvedDomainEvent evt)
    {
        var source = new
        {
            evt.NativeStatus,
            evt.CodeChangedSinceRaised,
            evt.CommentHistory,
            evt.ChangeExcerpt,
            classifier.ClassifierVersion,
        };
        var payload = this.MatchesBatch(evt)
            ? JsonSerializer.Serialize(new { Source = source, BatchDependencies = this._batch!.DependencyVersion })
            : JsonSerializer.Serialize(source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<CodeInsightEvidenceAcknowledgment?> TryReuseOutcomeAsync(
        ThreadResolvedDomainEvent evt, CodeInsightFindingView finding, CodeInsightDispositionRecord? existing,
        CodeInsightPublicationReceipt receipt, string fingerprint, bool historical, CancellationToken ct)
    {
        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return new(false);
        }

        if (historical)
        {
            var preserved = await performanceEvidence!.TryPreserveHistoricalOutcomeAsync(
                finding.Id,
                new CodeInsightHistoricalOutcomeObservation(receipt, evt.NativeStatus!, evt.Intent, evt.CodeChangedSinceRaised, fingerprint, evt.ObservedAt),
                ct);
            if (preserved.Retained)
            {
                if (preserved.Changed && rollupProjector is not null)
                {
                    await rollupProjector.ProjectJobAsync(finding.JobId, ct);
                }

                return preserved;
            }

            if (preserved.Reason is CodeInsightObservationUnavailableReason.ClassificationUnavailable
                or CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable)
            {
                return preserved;
            }
        }

        if (existing is not null && evt.NativeStatus is null)
        {
            return new CodeInsightEvidenceAcknowledgment(true);
        }

        if ((existing is not null || historical) && evt.NativeStatus is not null && performanceEvidence is not null
            && (historical || finding.CurrentClassifierVersion is null || finding.CurrentClassifierVersion == classifier.ClassifierVersion)
            && await performanceEvidence.ObserveUnchangedOutcomeAsync(finding.Id, evt.NativeStatus, fingerprint, evt.ObservedAt, ct, receipt))
        {
            return await performanceEvidence.AcknowledgeOutcomeAsync(
                finding.Id, receipt, evt.NativeStatus, fingerprint, evt.ObservedAt, ct,
                allowFailedAttemptCap: !historical);
        }

        return existing is not null && performanceEvidence is null && this.HasCompletedLocalOutcome(evt, finding, fingerprint)
            ? new CodeInsightEvidenceAcknowledgment(true)
            : null;
    }

    private bool HasCompletedLocalOutcome(ThreadResolvedDomainEvent evt, CodeInsightFindingView finding, string fingerprint)
    {
        return evt.NativeStatus is null || (string.Equals(evt.NativeStatus, finding.NativeStatus, StringComparison.OrdinalIgnoreCase)
                                            && finding.OutcomeSourceFingerprint == fingerprint && (finding.CurrentClassifierVersion is null ||
                                                                                                   finding.CurrentClassifierVersion ==
                                                                                                   classifier.ClassifierVersion)
                                            && finding.CurrentCodeChange == evt.CodeChangedSinceRaised
                                            && (finding.CurrentClassifierVersion is null || finding.CurrentClassifierConfidence is not null ||
                                                finding.OutcomeJudgementAttempts >= 3));
    }

    private async Task<CodeInsightEvidenceAcknowledgment> RetainOutcomeAsync(
        ThreadResolvedDomainEvent evt, CodeInsightFindingView finding, CodeInsightDispositionRecord? record,
        CodeInsightPublicationReceipt receipt, string fingerprint, bool historical, CancellationToken ct)
    {
        var changed = performanceEvidence is not null && evt.NativeStatus is not null && (historical
            ? await performanceEvidence.RecordHistoricalOutcomeAsync(finding.Id, evt.NativeStatus, record, evt.ObservedAt, ct, fingerprint, receipt)
            : await performanceEvidence.RecordCurrentOutcomeAsync(finding.Id, evt.NativeStatus, record, evt.ObservedAt, ct, fingerprint, receipt));
        if (historical)
        {
            var retained = await performanceEvidence!.AcknowledgeOutcomeAsync(finding.Id, receipt, evt.NativeStatus!, fingerprint, evt.ObservedAt, ct);
            if (!retained.Retained)
            {
                return retained;
            }
        }

        var decided = record is not null && (historical
            ? await performanceEvidence!.RecordHistoricalInitialDispositionAsync(finding.Id, record, receipt, fingerprint, evt.ObservedAt, ct)
            : await dispositionStore.RecordDispositionAsync(finding.Id, record, ct));
        if (decided)
        {
            LogDispositionRecorded(logger, finding.Id, record!.Disposition);
        }

        if ((changed || decided) && rollupProjector is not null)
        {
            await rollupProjector.ProjectJobAsync(finding.JobId, ct);
        }

        if (performanceEvidence is not null && evt.NativeStatus is not null)
        {
            var retained = await performanceEvidence.AcknowledgeOutcomeAsync(
                finding.Id, receipt, evt.NativeStatus, fingerprint, evt.ObservedAt, ct,
                allowFailedAttemptCap: !historical);
            return retained with { Changed = changed || decided };
        }

        return new(
            record?.ClassifierVersion is null || record.ClassifierConfidence is not null || finding.OutcomeJudgementAttempts >= 3,
            changed || decided);
    }

    private static CodeInsightEvidenceAcknowledgment IdentityUnavailable() =>
        new(false, Reason: CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);

    private sealed record DispositionSource(CodeInsightFindingView? Finding, CodeInsightEvidenceAcknowledgment? Failure);

    private async Task<CodeInsightDispositionRecord> ResolveDispositionAsync(
        ThreadResolvedDomainEvent evt,
        CodeInsightFindingView finding,
        CancellationToken ct)
    {
        var fromSignals = FindingDispositionMapper.MapFromSignals(evt.Intent, evt.CodeChangedSinceRaised);
        if (fromSignals is not null)
        {
            // Determined by the signals alone, no model call, and no classifier version to record.
            return new CodeInsightDispositionRecord(
                fromSignals.Value,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                null,
                null);
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            throw new InvalidOperationException("Code Insights collection permission changed before classification.");
        }

        var judgment = await classifier.JudgeAsync(
            new DisregardedFindingJudgementRequest(
                evt.ClientId,
                finding.Id,
                finding.Message,
                finding.FilePath,
                evt.CommentHistory,
                evt.ChangeExcerpt),
            ct);

        if (judgment is null)
        {
            // A failed judgment does not establish a false positive.
            LogSplitUndecided(logger, finding.Id);
            return new CodeInsightDispositionRecord(
                CodeInsightDisposition.Dismissed,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                classifier.ClassifierVersion,
                null);
        }

        if (judgment.IsUnresolved)
        {
            // An unresolved discussion has no acceptance or rejection verdict.
            return new CodeInsightDispositionRecord(
                CodeInsightDisposition.Discussed,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                classifier.ClassifierVersion,
                judgment.Confidence);
        }

        return new CodeInsightDispositionRecord(
            judgment.WasWrong ? CodeInsightDisposition.FalsePositive : CodeInsightDisposition.Dismissed,
            evt.Intent,
            evt.CodeChangedSinceRaised,
            classifier.ClassifierVersion,
            judgment.Confidence,
            // Preserve an unavailable rejection reason without inventing a category.
            judgment.Reason);
    }
}
