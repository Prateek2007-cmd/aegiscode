// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Misses;

/// <summary>Retains human review threads and evaluates missed findings after structural and ownership checks.</summary>
/// <remarks>Collection failures are retained when source ownership is known; cancellation propagates to the caller.</remarks>
public sealed partial class CodeInsightMissHarvester(
    IScmProviderCompatibilityCodec compatibilityCodec,
    ICodeInsightFindingStore findingStore,
    ICodeInsightMissStore missStore,
    IHumanMissClassifier classifier,
    ICodeInsightsCollectionGate gate,
    IPostedCommentComposer postedCommentComposer,
    ILogger<CodeInsightMissHarvester> logger,
    IFindingTypeClassifier? typeClassifier = null,
    ICodeInsightTaxonomyService? taxonomy = null,
    ReviewerPerformanceCountProjector? performanceProjector = null) : ICodeInsightMissHarvester
{
    private CodeInsightDiscussionBatch? _batch;
    private IReadOnlyDictionary<string, CodeInsightMissObservation>? _batchObservations;
    private readonly Dictionary<string, CodeInsightThreadEligibilityObservation> _batchEligibilities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidated = new(StringComparer.Ordinal);
    private CodeInsightTaxonomyDto? _batchTaxonomy;

    private bool MatchesBatch(ThreadUpdatedEvent evt) => this._batch is not null && this._batch.Key.ClientId == evt.ClientId &&
                                                         this._batch.Key.RepositoryId == evt.RepositoryId &&
                                                         this._batch.Key.PullRequestId == evt.PullRequestId &&
                                                         this._batch.ProviderScope == evt.ProviderScope && this._batch.ConnectionId == evt.ConnectionId;

    public string DependencyVersion => classifier.ClassifierVersion + ":" + typeClassifier?.ClassifierVersion;

    public async Task PrepareDiscussionBatchAsync(CodeInsightDiscussionBatch batch, CancellationToken ct = default)
    {
        this._batch = batch;
        this._batchObservations = await missStore.GetObservationsAsync(batch.Key, batch.ProviderScope, ct);
        this._batchEligibilities.Clear();
        foreach (var item in await missStore.GetEligibilitiesAsync(batch.Key, batch.ProviderScope, ct))
        {
            this._batchEligibilities.Add(item.Key, item.Value);
        }

        this._invalidated.Clear();
        this._batchTaxonomy = taxonomy is null ? null : await taxonomy.GetAssignableTaxonomyAsync(batch.Key.ClientId, ct);
    }

    public async Task<CodeInsightEvidenceAcknowledgment> ObserveCurrentAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        if (!await this.HandleAsync(evt, false, ct))
        {
            return new(false);
        }

        var source = await this.ReadObservationAsync(
            new(evt.ClientId, evt.RepositoryId, evt.PullRequestId), evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        return new(
            source is null || source.ExcludedAsOwnFinding || (!source.JudgementFailed || source.FailedAttempts >= 3) &&
            (!source.DimensionJudgementFailed || source.FailedDimensionAttempts >= 3));
    }

    private Task<CodeInsightMissObservation?> ReadObservationAsync(
        CodeInsightPullRequestKey key, string threadId, CancellationToken ct, Guid? connectionId, string? providerScope)
    {
        return this._batchObservations is not null && this._batch?.Key == key && this._batch.ProviderScope == providerScope &&
               this._batch.ConnectionId == connectionId && !this._invalidated.Contains(threadId)
            ? Task.FromResult(this._batchObservations.GetValueOrDefault(threadId))
            : missStore.GetObservationAsync(key, threadId, ct, connectionId, providerScope);
    }

    private Task<CodeInsightThreadEligibilityObservation?> ReadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, Guid connectionId, CancellationToken ct)
    {
        return this._batch?.Key == key && this._batch.ProviderScope == providerScope && this._batch.ConnectionId == connectionId
            ? Task.FromResult(this._batchEligibilities.GetValueOrDefault(threadId))
            : missStore.GetThreadEligibilityAsync(key, threadId, providerScope, ct);
    }

    public async Task<CodeInsightEvidenceAcknowledgment> ObserveHistoricalAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return new(false);
        }

        this._batch = null;
        this._batchObservations = null;
        this._batchEligibilities.Clear();
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return new(false);
        }

        if (!evt.Comments.Any(comment => comment.IsAiAuthored) && string.IsNullOrWhiteSpace(BuildDiscussion(evt))
                                                               && await this.ReadObservationAsync(
                                                                   key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope) is not null)
        {
            return await missStore.TryPreserveHistoricalMissAsync(
                key,
                new CodeInsightHistoricalMissObservation(
                    evt.ProviderScope ?? string.Empty, evt.ThreadId, evt.FilePath, evt.Line, "",
                    this.IsResolved(evt.Status), "", evt.ObservedAt ?? DateTimeOffset.UtcNow), ct);
        }

        if (!evt.Comments.Any(comment => comment.IsAiAuthored)
            && HarvestedThreadEligibility.IsHumanThread(BuildDiscussion(evt), postedCommentComposer.Text)
            && CodeInsightNativeResolution.InterpretKnown(evt.Status) is null)
        {
            return new CodeInsightEvidenceAcknowledgment(
                false, Reason: CodeInsightObservationUnavailableReason.ClassificationUnavailable,
                Stage: CodeInsightObservationStage.HumanClassification);
        }

        if (!await this.HandleAsync(evt, true, ct))
        {
            return new CodeInsightEvidenceAcknowledgment(
                false, Reason: CodeInsightObservationUnavailableReason.EvidenceRetentionFailed,
                Stage: CodeInsightObservationStage.HumanEvidenceRetention);
        }

        var retained = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        return retained is { ExcludedAsOwnFinding: false } && (retained.JudgementFailed || retained.DimensionJudgementFailed)
            ? new CodeInsightEvidenceAcknowledgment(
                false, Reason: CodeInsightObservationUnavailableReason.ClassificationUnavailable,
                Stage: CodeInsightObservationStage.HumanClassification)
            : new CodeInsightEvidenceAcknowledgment(true);
    }

    public Task<bool> HandleThreadObservedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        return this.HandleAsync(evt, false, ct);
    }

    private async Task<bool> HandleAsync(ThreadUpdatedEvent evt, bool historical, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var observedAt = evt.ObservedAt ?? DateTimeOffset.UtcNow;
        observedAt = new DateTimeOffset(observedAt.UtcTicks / 10 * 10, TimeSpan.Zero);

        var attempt = new HarvestAttempt(evt) { Historical = historical };
        try
        {
            return await this.ObserveCoreAsync(attempt, observedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, ex.GetType().FullName ?? ex.GetType().Name, ex.HResult);
            try
            {
                return await this.RetainFailedObservationAsync(attempt, observedAt, ct);
            }
            catch (Exception recordingException) when (!ct.IsCancellationRequested)
            {
                LogHandlingFailed(
                    logger, evt.ThreadId, evt.ClientId, recordingException.GetType().FullName ?? recordingException.GetType().Name, recordingException.HResult);
            }

            return false;
        }
    }

    private async Task<ThreadUpdatedEvent> ResolveEventScopeAsync(HarvestAttempt attempt, CancellationToken ct)
    {
        var evt = attempt.Event;
        if (evt.ProviderScope is null)
        {
            evt = evt with { ProviderScope = await findingStore.ResolveProviderScopeAsync(evt.ClientId, evt.ConnectionId, ct) ?? string.Empty };
            attempt.Event = evt;
        }

        return evt;
    }

    private async Task<bool> ObserveCoreAsync(HarvestAttempt attempt, DateTimeOffset observedAt, CancellationToken ct)
    {
        var evt = attempt.Event;
        var historical = attempt.Historical;
        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return false;
        }

        if (evt.IsDeleted || evt.Comments.Count == 0)
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return false;
            }

            evt = await this.ResolveEventScopeAsync(attempt, ct);
            var deletionKey = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
            var deletion = await missStore.ObserveThreadEligibilityAsync(deletionKey, evt.ThreadId, evt.ProviderScope ?? "", true, observedAt, ct);
            if (!deletion.Retained)
            {
                return false;
            }

            this._invalidated.Add(evt.ThreadId);
            var retained = await this.ReadObservationAsync(deletionKey, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
            return await this.ExcludeOwnFindingAsync(deletionKey, evt, "", observedAt, retained, ct);
        }

        // The producer captures authorship; own-thread outcomes belong to disposition collection.
        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var threadResolved = this.IsResolved(evt.Status);
        var discussion = BuildDiscussion(evt);
        if (!evt.Comments.Any(comment => comment.IsAiAuthored) &&
            (discussion.Length == 0 || !HarvestedThreadEligibility.IsHumanThread(discussion, postedCommentComposer.Text)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return false;
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return evt.Comments.Any(comment => comment.IsAiAuthored);
        }

        evt = await this.ResolveEventScopeAsync(attempt, ct);
        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return false;
        }

        var eligibility = await missStore.ObserveThreadEligibilityAsync(
            key, evt.ThreadId, evt.ProviderScope ?? string.Empty, evt.Comments.Any(comment => comment.IsAiAuthored), observedAt, ct,
            historical ? CodeInsightObservationMode.HistoricalBackfill : CodeInsightObservationMode.Current);
        if (!eligibility.Retained)
        {
            return false;
        }

        if (this.MatchesBatch(evt))
        {
            this._batchEligibilities[evt.ThreadId] = eligibility;
        }

        if (evt.Comments.Any(comment => comment.IsAiAuthored))
        {
            if (eligibility.SourceObservedAt > observedAt)
            {
                return await this.AcknowledgedAsync(key, evt, observedAt, false, ct);
            }

            var retainedSource = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
            return await this.ExcludeOwnFindingAsync(key, evt, "", observedAt, retainedSource, ct);
        }

        if (this.MatchesBatch(evt))
        {
            this._batchEligibilities[evt.ThreadId] = eligibility;
        }

        var batchSource = this.MatchesBatch(evt) ? this._batchObservations?.GetValueOrDefault(evt.ThreadId) : null;
        var judgedThreadResolved = !this.MatchesBatch(evt)
            ? await missStore.GetJudgedThreadResolvedAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope)
            : batchSource is null
                ? null
                : (bool?)(batchSource.JudgementFailed ? batchSource.FailedAttempts >= 3 : batchSource.JudgedThreadResolved);
        var alreadyHarvested = judgedThreadResolved is not null;
        var fingerprint = SourceFingerprint(
            evt, discussion, this.MatchesBatch(evt) ? classifier.ClassifierVersion + ":" + this._batch!.DependencyVersion : classifier.ClassifierVersion);
        var source = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        var sameSource = source?.SourceFingerprint == fingerprint &&
                         (historical || source.ClassifierVersion is null || source.ClassifierVersion == classifier.ClassifierVersion);

        var findings = this.MatchesBatch(evt) ? this._batch!.Findings : await findingStore.GetFindingsForPullRequestAsync(key, ct);
        attempt.OwnFindingsObserved = true;
        if (!string.IsNullOrEmpty(evt.ProviderScope))
        {
            findings = findings.Where(finding => finding.ProviderScope == evt.ProviderScope).ToList();
        }

        if (this.MatchesOwnFinding(evt, discussion, findings))
        {
            return await this.ExcludeOwnFindingAsync(key, evt, fingerprint, observedAt, source, ct);
        }

        if (eligibility.SourceObservedAt > observedAt || eligibility.ExcludedFromHumanMisses)
        {
            return await this.AcknowledgedAsync(key, evt, observedAt, false, ct);
        }

        if (source is not null)
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return false;
            }

            if (attempt.Historical)
            {
                var preserved = await missStore.TryPreserveHistoricalMissAsync(
                    key,
                    new CodeInsightHistoricalMissObservation(
                        evt.ProviderScope ?? string.Empty, evt.ThreadId, evt.FilePath, evt.Line, discussion,
                        threadResolved, fingerprint, observedAt), ct);
                if (preserved.Retained)
                {
                    await this.ProjectIfChangedAsync(key, preserved.Changed, ct);
                    return true;
                }

                if (preserved.Reason == CodeInsightObservationUnavailableReason.ClassificationUnavailable && source.SourceObservedAt > observedAt)
                {
                    return true;
                }
            }

            if (historical || source.ClassifierVersion is null || source.ClassifierVersion == classifier.ClassifierVersion)
            {
                this._invalidated.Add(evt.ThreadId);
                var acknowledgment = await missStore.ObserveUnchangedMissAsync(
                    key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
                    typeClassifier is not null && taxonomy is not null ? typeClassifier.ClassifierVersion : null, providerScope: evt.ProviderScope);
                if (acknowledgment.Retained)
                {
                    await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
                    return true;
                }
            }

            source = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
            sameSource = source?.SourceFingerprint == fingerprint &&
                         (historical || source.ClassifierVersion is null || source.ClassifierVersion == classifier.ClassifierVersion);
        }
        else if (alreadyHarvested && (judgedThreadResolved!.Value || !threadResolved))
        {
            return true;
        }

        var judgment = await this.ResolveHumanJudgmentAsync(evt, discussion, threadResolved, sameSource, source, attempt, ct);
        if (judgment is null)
        {
            // Retain the failed observation without inventing substantive, scope or acted-on judgments.
            LogUnjudged(logger, evt.ThreadId, evt.ClientId);
            var failedRecord = FailedRecord(evt, discussion, classifier.ClassifierVersion, threadResolved, fingerprint, observedAt, attempt);
            return await this.RetainAndAcknowledgeAsync(key, evt, failedRecord, alreadyHarvested, observedAt, ct, historical);
        }

        var classification = await this.ClassifyDimensionsAsync(evt, discussion, judgment, ct);
        var record = this.JudgedRecord(evt, discussion, judgment, classification, threadResolved, fingerprint, observedAt, attempt);
        if (attempt.Historical && sameSource && !attempt.HumanModelWasAsked && source is { JudgementFailed: false, ClassifierVersion: not null })
        {
            record = record with { ClassifierVersion = source.ClassifierVersion };
        }

        return await this.RetainAndAcknowledgeAsync(key, evt, record, alreadyHarvested, observedAt, ct, historical);
    }

    private bool MatchesOwnFinding(ThreadUpdatedEvent evt, string discussion, IReadOnlyList<CodeInsightFindingView> findings)
    {
        // Provider identity also excludes findings posted under an incorrectly attributed author account.
        if (findings.Any(finding => string.Equals(finding.ProviderThreadId, evt.ThreadId, StringComparison.Ordinal)))
        {
            LogOwnThread(logger, evt.ThreadId, evt.ClientId);
            return true;
        }

        var overlaps = HumanFindingOverlap.DuplicatesAnyFinding(
            evt.FilePath, evt.Line, discussion,
            findings.Select(finding => new FindingOverlapCandidate(finding.FilePath, finding.LineNumber, finding.Message)).ToList());
        if (overlaps)
        {
            LogDuplicateOfFinding(logger, evt.ThreadId, evt.ClientId);
        }

        return overlaps;
    }

    private CodeInsightMissRecord JudgedRecord(
        ThreadUpdatedEvent evt, string discussion, HumanMissJudgement judgment, MissDimensionClassification classification,
        bool threadResolved, string fingerprint, DateTimeOffset observedAt, HarvestAttempt attempt)
    {
        var dimensions = classification.Verdict;
        return new CodeInsightMissRecord(
            evt.ThreadId,
            evt.FilePath,
            evt.Line,
            discussion,
            judgment.IsSubstantive,
            judgment.WasActedOn,
            judgment.IsInScope,
            judgment.Confidence,
            classifier.ClassifierVersion,
            threadResolved,
            dimensions is null ? "" : string.Join('|', dimensions.CoreSlugs.Distinct().Order(StringComparer.Ordinal)),
            dimensions?.Qualifier,
            classification.Attempted ? typeClassifier!.ClassifierVersion : null,
            dimensions?.Confidence,
            ConnectionId: evt.ConnectionId,
            SourceFingerprint: fingerprint,
            DimensionJudgementFailed: classification.Attempted && dimensions is null,
            DimensionModelWasAsked: classification.ModelWasAsked,
            SourceObservedAt: observedAt,
            JudgementModelWasAsked: attempt.HumanModelWasAsked,
            ProviderScope: evt.ProviderScope);
    }

    private static CodeInsightMissRecord FailedRecord(
        ThreadUpdatedEvent evt, string discussion, string version, bool threadResolved,
        string fingerprint, DateTimeOffset observedAt, HarvestAttempt attempt)
    {
        return new CodeInsightMissRecord(
            evt.ThreadId, evt.FilePath, evt.Line, discussion, false, false, false, null, version, threadResolved,
            JudgementFailed: true,
            ConnectionId: evt.ConnectionId,
            SourceFingerprint: fingerprint,
            SourceObservedAt: observedAt,
            JudgementModelWasAsked: attempt.HumanModelWasAsked,
            ProviderScope: evt.ProviderScope);
    }

    private async Task<bool> RetainAndAcknowledgeAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, CodeInsightMissRecord record, bool alreadyHarvested,
        DateTimeOffset observedAt, CancellationToken ct, bool historical)
    {
        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return false;
        }

        record = record with { ObservationMode = historical ? CodeInsightObservationMode.HistoricalBackfill : CodeInsightObservationMode.Current };
        this._invalidated.Add(evt.ThreadId);
        var retained = await this.RetainAsync(key, record, alreadyHarvested, ct);
        await this.ProjectIfChangedAsync(key, retained, ct);
        if (retained && record.CountsAsMiss)
        {
            LogMissHarvested(logger, evt.ThreadId, evt.ClientId);
        }

        return await this.AcknowledgedAsync(key, evt, observedAt, retained, ct);
    }

    private static string SourceFingerprint(ThreadUpdatedEvent evt, string discussion, string version)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{evt.Status}\n{evt.FilePath}\n{evt.Line}\n{discussion}\n{version}")));
    }

    private async Task<HumanMissJudgement?> ResolveHumanJudgmentAsync(
        ThreadUpdatedEvent evt, string discussion, bool threadResolved, bool sameSource,
        CodeInsightMissObservation? source, HarvestAttempt attempt, CancellationToken ct)
    {
        HumanMissJudgement? judgment;
        if (sameSource && source is { JudgementFailed: false })
        {
            judgment = new HumanMissJudgement(source.IsSubstantive, source.WasActedOn, source.IsInScope, source.Confidence ?? 0, "Retained human judgment");
        }
        else
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return null;
            }

            var request = new HumanMissJudgementRequest(evt.ClientId, evt.ThreadId, evt.FilePath, discussion, threadResolved);
            if (classifier is IHumanMissClassifierAttemptReporter attemptReporter)
            {
                var result = await attemptReporter.JudgeWithAttemptAsync(request, ct);
                judgment = result.Judgement;
                attempt.HumanModelWasAsked = result.ModelWasAsked;
            }
            else
            {
                attempt.HumanModelWasAsked = true;
                judgment = await classifier.JudgeAsync(request, ct);
            }
        }

        return judgment;
    }

    private async Task<MissDimensionClassification> ClassifyDimensionsAsync(
        ThreadUpdatedEvent evt, string discussion, HumanMissJudgement judgment, CancellationToken ct)
    {
        FindingTypeVerdict? dimensions = null;
        var dimensionAttempted = false;
        var dimensionModelWasAsked = false;
        if (judgment.IsSubstantive && typeClassifier is not null && taxonomy is not null)
        {
            try
            {
                dimensionAttempted = true;
                if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
                {
                    return new MissDimensionClassification(null, false, false);
                }

                var vocabulary = this.MatchesBatch(evt) && this._batchTaxonomy is not null
                    ? this._batchTaxonomy
                    : await taxonomy.GetAssignableTaxonomyAsync(evt.ClientId, ct);
                var result = await typeClassifier.ClassifyAsync(
                    new FindingClassificationRequest(
                        evt.ClientId,
                        Guid.NewGuid(), discussion, evt.FilePath, evt.Line, CommentSeverity.Warning,
                        null, vocabulary), ct);
                dimensions = result.Verdict;
                dimensionModelWasAsked = result.ModelWasAsked;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, exception.GetType().FullName ?? exception.GetType().Name, exception.HResult);
            }
        }

        return new MissDimensionClassification(dimensions, dimensionAttempted, dimensionModelWasAsked);
    }

    private async Task<bool> RetainFailedObservationAsync(HarvestAttempt attempt, DateTimeOffset observedAt, CancellationToken ct)
    {
        var evt = attempt.Event;
        if (evt.IsDeleted || evt.Comments.Count == 0)
        {
            return false;
        }

        if (evt.Comments.Any(comment => comment.IsAiAuthored))
        {
            return false;
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return evt.Comments.Any(comment => comment.IsAiAuthored);
        }

        var discussion = BuildDiscussion(evt);
        if (discussion.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return false;
        }

        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        if (!attempt.OwnFindingsObserved)
        {
            var eligibility = await this.ReadEligibilityAsync(key, evt.ThreadId, evt.ProviderScope ?? string.Empty, evt.ConnectionId, ct);
            // A completed judgment cannot establish that the reviewer still has no overlapping finding.
            if (eligibility is not { ExcludedFromHumanMisses: true } || eligibility.SourceObservedAt < observedAt)
            {
                return false;
            }
        }

        var version = string.IsNullOrWhiteSpace(classifier.ClassifierVersion) ? "collection-failed" : classifier.ClassifierVersion;
        var fingerprint = SourceFingerprint(evt, discussion, version);
        var source = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        if (source is not null)
        {
            this._invalidated.Add(evt.ThreadId);
            var acknowledgment = await missStore.ObserveUnchangedMissAsync(
                key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
                typeClassifier is not null && taxonomy is not null ? typeClassifier.ClassifierVersion : null, providerScope: evt.ProviderScope);
            if (acknowledgment.Retained)
            {
                await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
                return true;
            }
        }

        var record = FailedRecord(evt, discussion, version, this.IsResolved(evt.Status), fingerprint, observedAt, attempt);
        return await this.RetainAndAcknowledgeAsync(key, evt, record, source is not null, observedAt, ct, attempt.Historical);
    }

    private async Task<bool> RetainAsync(CodeInsightPullRequestKey key, CodeInsightMissRecord record, bool alreadyHarvested, CancellationToken ct)
    {
        return alreadyHarvested ? await missStore.RejudgeMissAsync(key, record, ct) : await missStore.RecordMissAsync(key, record, ct);
    }

    private async Task ProjectIfChangedAsync(CodeInsightPullRequestKey key, bool changed, CancellationToken ct)
    {
        if (changed && performanceProjector is not null)
        {
            await performanceProjector.ProjectAsync(key, ct);
        }
    }

    private async Task<bool> AcknowledgedAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, DateTimeOffset observedAt, bool changed, CancellationToken ct)
    {
        if (changed)
        {
            return true;
        }

        var current = await this.ReadObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        var eligibility = await this.ReadEligibilityAsync(key, evt.ThreadId, evt.ProviderScope ?? string.Empty, evt.ConnectionId, ct);
        if (eligibility is { ExcludedFromHumanMisses: true } && eligibility.SourceObservedAt >= observedAt)
        {
            return current is null || (current.ExcludedAsOwnFinding && current.SourceObservedAt >= eligibility.SourceObservedAt);
        }

        return current?.SourceObservedAt >= observedAt;
    }

    private async Task<bool> ExcludeOwnFindingAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, string fingerprint,
        DateTimeOffset observedAt, CodeInsightMissObservation? source, CancellationToken ct)
    {
        if (source is null)
        {
            return true;
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return false;
        }

        this._invalidated.Add(evt.ThreadId);
        var acknowledgment = await missStore.ObserveUnchangedMissAsync(
            key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
            excludedAsOwnFinding: true, providerScope: evt.ProviderScope);
        await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
        return acknowledgment.Retained;
    }

    /// <summary>Joins non-system comments into uncapped author-and-text lines in provider order.</summary>
    /// <remarks>
    ///     Retained text is used to reassess human-thread eligibility. Truncation could omit the only human
    ///     comment after provider activity and exclude the thread from recall. The classifier caps its own input.
    /// </remarks>
    private static string BuildDiscussion(ThreadUpdatedEvent evt)
    {
        var builder = new StringBuilder();
        foreach (var comment in evt.Comments)
        {
            if (comment.IsSystemGenerated || string.IsNullOrWhiteSpace(comment.Text))
            {
                continue;
            }

            builder.Append(comment.AuthorIdentity).Append(": ").Append(comment.Text.Trim()).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Uses the shared provider-status interpreter to identify terminal thread states.</summary>
    private bool IsResolved(string? status)
    {
        return ThreadResolutionStatusInterpreter.IsResolved(compatibilityCodec.DecodeStoredThreadResolution(status));
    }

    private sealed record MissDimensionClassification(FindingTypeVerdict? Verdict, bool Attempted, bool ModelWasAsked);

    private sealed class HarvestAttempt(ThreadUpdatedEvent evt)
    {
        internal ThreadUpdatedEvent Event { get; set; } = evt;
        internal bool Historical { get; init; }
        internal bool HumanModelWasAsked { get; set; }
        internal bool OwnFindingsObserved { get; set; }
    }
}
