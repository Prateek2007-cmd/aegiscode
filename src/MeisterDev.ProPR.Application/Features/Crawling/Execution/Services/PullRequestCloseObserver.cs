// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Collects final thread observations and finding dispositions before pull-request measurement sealing.</summary>
/// <remarks>Active-pass archive retention is independent of this close-only collection operation.</remarks>
public sealed partial class PullRequestCloseObserver(
    IScmProviderRegistry providerRegistry,
    ILogger<PullRequestCloseObserver> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ICodeInsightMissHarvester? missHarvester = null,
    ICodeInsightFindingStore? findingStore = null,
    ICodeInsightsCollectionGate? collectionGate = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IReviewerThreadStatusFetcher? reviewerThreadStatuses = null,
    ICodeInsightDispositionService? dispositionService = null,
    ICodeInsightHarvestCoverageRecorder? harvestCoverageRecorder = null,
    ICodeInsightProviderFeedbackReader? feedbackReader = null) : ICodeInsightCloseObserver, ICodeInsightHistoricalObserver
{
    public async Task ObserveAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Optional dependencies represent module or host availability; licensing remains a runtime collection gate.
        if (pullRequestFetcher is null
            || missHarvester is null
            || findingStore is null
            || collectionGate is null
            || scmConnectionRepository is null
            || harvestCoverageRecorder is null)
        {
            return;
        }

        string? harvestScope = null;
        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!await collectionGate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return;
            }

            // Sealing candidates require collected findings. A human miss alone can create an aggregate.
            var findings = await findingStore.GetFindingsForPullRequestAsync(key, ct);
            if (findings.Count == 0)
            {
                LogNothingCollected(logger, key.PullRequestId, key.ClientId);
                return;
            }

            // Resolve the provider family before interpreting provider comment identities.
            var connection = await this.ResolveConnectionAsync(key.ClientId, providerScopePath, ct);
            if (connection is null)
            {
                // Log only the authority because source paths may contain credentials.
                LogNoConnection(
                    logger,
                    key.PullRequestId,
                    key.ClientId,
                    ScmConnectionHostMatch.ToAuthority(providerScopePath) ?? "(no host)");
                return;
            }

            var ownership = await this.ResolveOwnershipAsync(key, connection.ProviderFamily, ct);
            harvestScope = ProviderSourceIdentity.FromReviewSource(
                connection.ProviderFamily,
                providerRegistry.GetSourceIdentityPolicy(connection.ProviderFamily).SelectCapturedSource(providerScopePath, connection.HostBaseUrl),
                providerRegistry.GetSourceIdentityPolicy(connection.ProviderFamily)).Value;

            // The adapter contributes its account identity before human-thread authorship is resolved.
            var reviewerObservedAt = DateTimeOffset.UtcNow;
            IReadOnlyList<PrThreadStatusEntry> reviewerThreads;
            IReadOnlyList<PrCommentThread> threads;
            var reviewerEnumerationComplete = true;
            if (feedbackReader is not null)
            {
                var feedback = await feedbackReader.ReadAsync(
                    new CodeInsightProviderEvidenceTarget(
                        key, connection.ProviderFamily,
                        providerScopePath, providerProjectKey, harvestScope), ownership, ct, CodeInsightObservationMode.Current);
                reviewerThreads = feedback.ReviewerThreads;
                threads = feedback.Threads;
                reviewerEnumerationComplete = feedback.ReviewerEnumerationComplete;
                observedAt = reviewerObservedAt;
            }
            else
            {
                var reviewerRead = await this.ReadReviewerThreadsAsync(key, providerScopePath, providerProjectKey, ownership, ct);
                reviewerThreads = reviewerRead.Threads;
                reviewerEnumerationComplete = reviewerRead.EnumerationComplete;
                observedAt = DateTimeOffset.UtcNow;
                threads = await pullRequestFetcher.FetchThreadsAsync(
                    providerScopePath, providerProjectKey, key.RepositoryId,
                    (int)key.PullRequestId, key.ClientId, ct);
            }

            var coverage = new HarvestCoverageState();
            foreach (var thread in threads)
            {
                var evt = ThreadUpdatedEventFactory.Build(
                    key.ClientId, connection.Id, key.RepositoryId, key.PullRequestId, thread, ownership, observedAt, harvestScope);
                coverage.Observe(
                    evt, providerRegistry.GetCodeReviewPreparationPolicy(connection.ProviderFamily)
                        .InterpretThreadResolution(evt.Status));
                if (string.IsNullOrWhiteSpace(thread.ThreadId))
                {
                    if (ThreadUpdatedEventFactory.IsHumanThread(evt))
                    {
                        coverage.RecordRetention(false);
                    }

                    continue;
                }

                coverage.RecordRetention(await missHarvester.HandleThreadObservedAsync(evt, ct));
            }

            await this.RecordDispositionsAsync(
                key,
                providerScopePath,
                providerProjectKey,
                reviewerThreads,
                findings,
                harvestScope, reviewerObservedAt, connection.ProviderFamily,
                ct);
            await harvestCoverageRecorder.RecordAsync(
                key, harvestScope, coverage.AllHumanThreadsResolved, observedAt, ct, coverage.AllHumanObservationsRetained && reviewerEnumerationComplete);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (harvestScope is not null)
            {
                await harvestCoverageRecorder.RecordAsync(key, harvestScope, false, observedAt, ct, false);
            }

            LogObservationFailed(logger, key.PullRequestId, key.ClientId, ex);
        }
    }

    public async Task<CodeInsightObservationResult> RefreshAsync(CodeInsightProviderEvidenceTarget target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (collectionGate is null || feedbackReader is null || findingStore is null || missHarvester is null
            || dispositionService is null || harvestCoverageRecorder is null || scmConnectionRepository is null)
        {
            return Unavailable(CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable);
        }

        if (!await collectionGate.IsCollectionEnabledAsync(target.Key.ClientId, ct))
        {
            return new CodeInsightObservationResult(CodeInsightObservationStatus.Disabled);
        }

        var failure = CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable;
        try
        {
            if (!this.HasCapturedCoordinates(target))
            {
                return Unavailable(CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable);
            }

            var connection = await this.ResolveHistoricalConnectionAsync(target, ct);
            if (connection is null)
            {
                return Unavailable(failure);
            }

            failure = CodeInsightObservationUnavailableReason.OwnershipUnavailable;
            if (postedCommentOriginStore is null)
            {
                return Unavailable(failure);
            }

            var source = await this.ReadHistoricalSourceAsync(target, ct);
            var observedAt = NormalizeDatabaseTimestamp(DateTimeOffset.UtcNow);
            failure = CodeInsightObservationUnavailableReason.ProviderReadFailed;
            var feedback = await feedbackReader.ReadAsync(target, source.Ownership, ct);
            ct.ThrowIfCancellationRequested();
            var readFailure = ValidateHistoricalFeedback(source, feedback);
            if (readFailure is not null)
            {
                return Unavailable(readFailure.Value);
            }

            if (!await collectionGate.IsCollectionEnabledAsync(target.Key.ClientId, ct))
            {
                return new CodeInsightObservationResult(CodeInsightObservationStatus.Disabled);
            }

            failure = CodeInsightObservationUnavailableReason.EvidenceRetentionFailed;
            var identityFailure = CorrelateHistoricalOutcomes(target, source, feedback, observedAt, out var outcomes);
            if (identityFailure is not null)
            {
                return Unavailable(identityFailure.Value);
            }

            var harvest = await this.RetainHistoricalHumanThreadsAsync(target, connection.Id, source.Ownership, feedback.Threads, observedAt, ct);
            if (harvest.Failure is not null)
            {
                return Unavailable(harvest.Failure.Value);
            }

            var outcomeFailure = await this.RetainHistoricalOutcomesAsync(outcomes, ct);
            if (outcomeFailure is not null)
            {
                return Unavailable(outcomeFailure.Value);
            }

            return await this.AcknowledgeHistoricalCoverageAsync(target, harvest.Coverage, observedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CodeInsightIncompleteEnumerationException)
        {
            return Unavailable(CodeInsightObservationUnavailableReason.IncompleteEnumeration);
        }
        catch (CodeInsightProviderIdentityException)
        {
            return Unavailable(CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);
        }
        catch (Exception ex)
        {
            // Provider exception messages can contain credentials; retain only the type and numeric code.
            var exceptionType = ex.GetType();
            LogHistoricalObservationUnavailable(
                logger, target.Key.PullRequestId, target.Key.ClientId, failure, exceptionType.FullName ?? exceptionType.Name, ex.HResult);
            return Unavailable(failure);
        }
    }

    private bool HasCapturedCoordinates(CodeInsightProviderEvidenceTarget target)
    {
        var captured = ProviderSourceIdentity.FromReviewSource(
            target.Provider, target.ProviderScopePath,
            providerRegistry.GetSourceIdentityPolicy(target.Provider)).Value;
        return captured == target.ProviderScope && target.Key.PullRequestId is > 0 and <= int.MaxValue;
    }

    private async Task<ClientScmConnectionDto?> ResolveHistoricalConnectionAsync(CodeInsightProviderEvidenceTarget target, CancellationToken ct)
    {
        var authority = ScmConnectionHostMatch.ToAuthority(target.ProviderScopePath);
        var connections = await scmConnectionRepository!.GetByClientIdAsync(target.Key.ClientId, ct);
        return connections.Where(row => row.IsActive && row.ProviderFamily == target.Provider && authority is not null
                                        && providerRegistry.GetConnectionConfigurationPolicy(target.Provider)
                                            .MatchesObservedConnectionHost(row.HostBaseUrl, authority)
                                        && CapturedPathMatches(row.HostBaseUrl, target.ProviderScopePath))
            .OrderByDescending(row => row.HostBaseUrl.Length).FirstOrDefault();
    }

    private async Task<HistoricalSource> ReadHistoricalSourceAsync(CodeInsightProviderEvidenceTarget target, CancellationToken ct)
    {
        var provenance = await postedCommentOriginStore!.GetOriginsForProviderSourceAsync(
            target.Key.ClientId, target.Key.RepositoryId, target.Key.PullRequestId, target.Provider, target.ProviderScope, ct);
        var findings = await findingStore!.GetNativeFeedbackFindingsAsync(target.Key, target.ProviderScope, ct);
        var commentScope = providerRegistry.GetIdentityPolicy(target.Provider).CommentIdScope;
        var capturedFindings = findings.Where(row => row.Finding.ProviderScope == target.ProviderScope).Select(row => row.Finding).ToList();
        var receipts = capturedFindings.Where(row => !string.IsNullOrWhiteSpace(row.ProviderCommentId)
                                                     && (commentScope == ProviderCommentIdScope.PullRequest ||
                                                         !string.IsNullOrWhiteSpace(row.ProviderThreadId)))
            .Select(row => new PostedCommentOriginRow(row.ProviderThreadId, row.ProviderCommentId!, row.JobId));
        var ownership = ThreadOwnershipResolver.Create(
            provenance.Matched.Concat(receipts).ToList(), ThreadOwnerIdentity.None, commentScope,
            capturedFindings.Where(row => !string.IsNullOrWhiteSpace(row.ProviderThreadId)).Select(row => row.ProviderThreadId!)
                .ToHashSet(StringComparer.Ordinal));
        var unverifiable = ThreadOwnershipResolver.Create(provenance.Unverifiable, ThreadOwnerIdentity.None, commentScope);
        var missThreads = await findingStore.GetHistoricalMissThreadIdsAsync(target.Key, target.ProviderScope, ct);
        return new HistoricalSource(findings, missThreads, ownership, unverifiable, commentScope);
    }

    private static CodeInsightObservationUnavailableReason? ValidateHistoricalFeedback(HistoricalSource source, CodeInsightProviderFeedback feedback)
    {
        if (feedback.Threads.Any(thread => thread.Comments.Any(comment =>
            {
                var reference = new ThreadCommentRef(
                    thread.ThreadId, comment.CommentId.ToString(CultureInfo.InvariantCulture), comment.AuthorId, comment.AuthorName);
                return source.Unverifiable.OwnsComment(reference) && !source.Ownership.OwnsComment(reference);
            })))
        {
            return CodeInsightObservationUnavailableReason.OwnershipUnavailable;
        }

        if (!feedback.ReviewerEnumerationComplete || feedback.ReviewerThreads.Any(reviewer => feedback.Threads.Any(thread =>
                thread.ThreadId == reviewer.ThreadId
                && CodeInsightNativeResolution.InterpretKnown(thread.Status) is { } rawIntent
                && CodeInsightNativeResolution.InterpretKnown(reviewer.Status) is { } reviewerIntent && rawIntent != reviewerIntent)))
        {
            return CodeInsightObservationUnavailableReason.IncompleteEnumeration;
        }

        return source.MissThreads.Any(id => string.IsNullOrWhiteSpace(id) || !feedback.Threads.Any(thread => thread.ThreadId == id))
            ? CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable
            : null;
    }

    private static CodeInsightObservationUnavailableReason? CorrelateHistoricalOutcomes(
        CodeInsightProviderEvidenceTarget target, HistoricalSource source, CodeInsightProviderFeedback feedback, DateTimeOffset observedAt,
        out List<HistoricalOutcome> outcomes)
    {
        outcomes = [];
        var required = source.Findings.Where(row => row.PublicationState == CodeInsightPublicationState.Published
                                                    || (row.PublicationState == CodeInsightPublicationState.Unknown
                                                        && (!string.IsNullOrWhiteSpace(row.Finding.ProviderThreadId) ||
                                                            !string.IsNullOrWhiteSpace(row.Finding.ProviderCommentId))));
        foreach (var finding in required)
        {
            var threadId = finding.Finding.ProviderThreadId;
            var observedIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                observedIds.Add(threadId);
            }

            if (source.CommentScope == ProviderCommentIdScope.PullRequest && !string.IsNullOrWhiteSpace(finding.Finding.ProviderCommentId))
            {
                var commentMatches = feedback.Threads.Where(thread => thread.Comments.Any(comment => comment.CommentId > 0
                                                                                                     && comment.CommentId.ToString(
                                                                                                         CultureInfo.InvariantCulture) ==
                                                                                                     finding.Finding.ProviderCommentId)).ToList();
                if (commentMatches.Count != 1 || string.IsNullOrWhiteSpace(commentMatches[0].ThreadId))
                {
                    return CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable;
                }

                observedIds.Clear();
                observedIds.Add(commentMatches[0].ThreadId!);
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    threadId = commentMatches[0].ThreadId;
                }
            }

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable;
            }

            var matches = feedback.ReviewerThreads.Where(thread => thread.ThreadId is not null && observedIds.Contains(thread.ThreadId)).ToList();
            if (matches.Count != 1 || CodeInsightNativeResolution.InterpretKnown(matches[0].Status) is not { } intent)
            {
                return CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable;
            }

            var thread = matches[0];
            if (feedback.Threads.Any(raw =>
                    observedIds.Contains(raw.ThreadId ?? string.Empty) && CodeInsightNativeResolution.InterpretKnown(raw.Status) != intent))
            {
                return CodeInsightObservationUnavailableReason.IncompleteEnumeration;
            }

            outcomes.Add(
                new HistoricalOutcome(
                    finding with { CorrelatedThreadId = string.IsNullOrWhiteSpace(finding.Finding.ProviderThreadId) ? threadId : null },
                    new ThreadResolvedDomainEvent(
                        target.Key.ClientId, target.ProviderScopePath, target.ProviderProjectKey, target.Key.RepositoryId,
                        (int)target.Key.PullRequestId, threadId, thread.FilePath, null, thread.CommentHistory, observedAt, intent,
                        thread.CodeChangedSinceRaised,
                        thread.Status, target.ProviderScope)));
        }

        return null;
    }

    private async Task<HistoricalHarvest> RetainHistoricalHumanThreadsAsync(
        CodeInsightProviderEvidenceTarget target, Guid connectionId, ThreadOwnershipResolver ownership, IReadOnlyList<PrCommentThread> threads,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        var coverage = new HarvestCoverageState();
        foreach (var thread in threads)
        {
            ct.ThrowIfCancellationRequested();
            var evt = ThreadUpdatedEventFactory.Build(
                target.Key.ClientId, connectionId, target.Key.RepositoryId,
                target.Key.PullRequestId, thread, ownership, observedAt, target.ProviderScope);
            var human = ThreadUpdatedEventFactory.IsHumanThread(evt);
            if (human && string.IsNullOrWhiteSpace(thread.ThreadId))
            {
                return new HistoricalHarvest(coverage, CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable);
            }

            if (string.IsNullOrWhiteSpace(thread.ThreadId))
            {
                continue;
            }

            var intent = CodeInsightNativeResolution.InterpretKnown(thread.Status);
            if (human && intent is null)
            {
                return new HistoricalHarvest(coverage, CodeInsightObservationUnavailableReason.ClassificationUnavailable);
            }

            coverage.Observe(evt, intent ?? ThreadResolutionIntent.Active);
            var ack = await missHarvester!.ObserveHistoricalAsync(evt, ct);
            ct.ThrowIfCancellationRequested();
            if (!ack.Retained)
            {
                return new HistoricalHarvest(coverage, ack.Reason ?? CodeInsightObservationUnavailableReason.EvidenceRetentionFailed);
            }
        }

        return new HistoricalHarvest(coverage, null);
    }

    private async Task<CodeInsightObservationUnavailableReason?> RetainHistoricalOutcomesAsync(IReadOnlyList<HistoricalOutcome> outcomes, CancellationToken ct)
    {
        foreach (var outcome in outcomes)
        {
            ct.ThrowIfCancellationRequested();
            var ack = await dispositionService!.ObserveHistoricalAsync(outcome.Finding, outcome.Event, ct);
            ct.ThrowIfCancellationRequested();
            if (!ack.Retained)
            {
                return ack.Reason ?? CodeInsightObservationUnavailableReason.EvidenceRetentionFailed;
            }
        }

        return null;
    }

    private async Task<CodeInsightObservationResult> AcknowledgeHistoricalCoverageAsync(
        CodeInsightProviderEvidenceTarget target, HarvestCoverageState coverage, DateTimeOffset observedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ack = await harvestCoverageRecorder!.RecordAcknowledgedAsync(target.Key, target.ProviderScope, coverage.AllHumanThreadsResolved, observedAt, ct);
        ct.ThrowIfCancellationRequested();
        return ack.Retained
            ? new CodeInsightObservationResult(CodeInsightObservationStatus.Observed, observedAt)
            : Unavailable(ack.Reason ?? CodeInsightObservationUnavailableReason.EvidenceRetentionFailed);
    }

    // PostgreSQL stores microseconds; acknowledgement comparisons must use the retained precision.
    private static DateTimeOffset NormalizeDatabaseTimestamp(DateTimeOffset stamp) =>
        new(stamp.UtcTicks - stamp.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private sealed record HistoricalSource(
        IReadOnlyList<CodeInsightNativeFindingSnapshot> Findings,
        IReadOnlyList<string> MissThreads,
        ThreadOwnershipResolver Ownership,
        ThreadOwnershipResolver Unverifiable,
        ProviderCommentIdScope CommentScope);

    private sealed record HistoricalOutcome(CodeInsightNativeFindingSnapshot Finding, ThreadResolvedDomainEvent Event);

    private sealed record HistoricalHarvest(HarvestCoverageState Coverage, CodeInsightObservationUnavailableReason? Failure);

    private static CodeInsightObservationResult Unavailable(CodeInsightObservationUnavailableReason reason)
    {
        return new CodeInsightObservationResult(CodeInsightObservationStatus.Unavailable, UnavailableReason: reason);
    }

    private static bool CapturedPathMatches(string configured, string captured)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) || !Uri.TryCreate(captured, UriKind.Absolute, out var capturedUri))
        {
            return false;
        }

        var prefix = configuredUri.AbsolutePath.TrimEnd('/');
        return capturedUri.AbsolutePath.TrimEnd('/').Equals(prefix, StringComparison.OrdinalIgnoreCase)
               || capturedUri.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads own-thread statuses and allows the adapter to contribute its authenticated account identity.</summary>
    /// <remarks>Without a status fetcher, ownership uses retained provenance and dispositions are unavailable.</remarks>
    private async Task<(IReadOnlyList<PrThreadStatusEntry> Threads, bool EnumerationComplete)> ReadReviewerThreadsAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        ThreadOwnershipResolver ownership,
        CancellationToken ct)
    {
        if (reviewerThreadStatuses is null)
        {
            return ([], true);
        }

        try
        {
            var threads = await reviewerThreadStatuses.GetReviewerThreadStatusesAsync(
                providerScopePath,
                providerProjectKey,
                key.RepositoryId,
                (int)key.PullRequestId,
                ownership,
                key.ClientId,
                ct);
            return (threads, true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogReviewerThreadsUnavailable(logger, key.PullRequestId, key.ClientId, ex);
            return ([], false);
        }
    }

    /// <summary>Records final outcomes for collected findings before the measurement is sealed.</summary>
    /// <remarks>Every known thread is offered; the disposition service handles replay and unresolved outcomes.</remarks>
    private async Task RecordDispositionsAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        IReadOnlyList<PrThreadStatusEntry> reviewerThreads,
        IReadOnlyList<CodeInsightFindingView> findings,
        string providerScope,
        DateTimeOffset observedAt,
        ScmProvider provider,
        CancellationToken ct)
    {
        if (dispositionService is null || reviewerThreads.Count == 0)
        {
            return;
        }

        // Restrict disposition lookups to threads represented by collected findings.
        var findingThreads = findings
            .Select(finding => finding.ProviderThreadId)
            .Where(threadId => !string.IsNullOrWhiteSpace(threadId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var thread in reviewerThreads)
        {
            if (string.IsNullOrWhiteSpace(thread.ThreadId) || !findingThreads.Contains(thread.ThreadId))
            {
                continue;
            }

            var intent = providerRegistry.GetCodeReviewPreparationPolicy(provider).InterpretThreadResolution(thread.Status);
            await dispositionService.HandleThreadResolvedAsync(
                new ThreadResolvedDomainEvent(
                    key.ClientId,
                    providerScopePath,
                    providerProjectKey,
                    key.RepositoryId,
                    (int)key.PullRequestId,
                    thread.ThreadId,
                    thread.FilePath,
                    null,
                    thread.CommentHistory,
                    observedAt,
                    intent,
                    thread.CodeChangedSinceRaised,
                    thread.Status, providerScope),
                ct);
        }
    }

    /// <summary>Finds the client's active connection for the host the pull request lives on.</summary>
    private async Task<ClientScmConnectionDto?> ResolveConnectionAsync(
        Guid clientId,
        string providerScopePath,
        CancellationToken ct)
    {
        var authority = ScmConnectionHostMatch.ToAuthority(providerScopePath);
        if (authority is null)
        {
            return null;
        }

        var connections = await scmConnectionRepository!.GetByClientIdAsync(clientId, ct);

        var matches = connections
            .Where(connection => connection.IsActive
                                 && !string.IsNullOrWhiteSpace(connection.HostBaseUrl)
                                 && providerRegistry.GetConnectionConfigurationPolicy(connection.ProviderFamily)
                                     .MatchesObservedConnectionHost(connection.HostBaseUrl, authority))
            .ToList();

        // Conflicting provider families on one authority prevent unambiguous comment interpretation.
        if (matches.Select(connection => connection.ProviderFamily).Distinct().Count() > 1)
        {
            LogAmbiguousConnection(logger, clientId, authority);
            return null;
        }

        return matches
            // Prefer the most specific host match when several connections share an authority.
            .OrderByDescending(connection => connection.HostBaseUrl.Length)
            .FirstOrDefault();
    }

    /// <summary>Resolves own-comment provenance without invoking a provider adapter.</summary>
    /// <remarks>The harvester checks retained finding identities when provenance is unavailable.</remarks>
    private async Task<ThreadOwnershipResolver> ResolveOwnershipAsync(
        CodeInsightPullRequestKey key,
        ScmProvider provider,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null)
        {
            return ThreadOwnershipResolver.None;
        }

        try
        {
            var provenance = await postedCommentOriginStore.GetJobIdsForPullRequestAsync(
                key.ClientId,
                key.RepositoryId,
                key.PullRequestId,
                ct);

            return ThreadOwnershipResolver.Create(
                provenance,
                ThreadOwnerIdentity.None,
                providerRegistry.GetIdentityPolicy(provider).CommentIdScope);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogOwnershipUnavailable(logger, key.PullRequestId, key.ClientId, ex);
            return ThreadOwnershipResolver.None;
        }
    }

    [LoggerMessage(
        EventId = 6106, Level = LogLevel.Warning,
        Message =
            "Historical observation for PR {PullRequestId} of client {ClientId} is unavailable: {Reason}. Exception type: {ExceptionType}; error code: {ErrorCode}.")]
    private static partial void LogHistoricalObservationUnavailable(
        ILogger logger, long pullRequestId, Guid clientId, CodeInsightObservationUnavailableReason reason, string exceptionType, int errorCode);

    [LoggerMessage(
        EventId = 6105,
        Level = LogLevel.Warning,
        Message = "Reading ProPR's own threads on PR {PullRequestId} of client {ClientId} failed; its findings keep whatever outcome earlier passes recorded.")]
    private static partial void LogReviewerThreadsUnavailable(ILogger logger, long pullRequestId, Guid clientId, Exception ex);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Debug,
        Message = "PR {PullRequestId} of client {ClientId} has no collected findings; its close is not observed.")]
    private static partial void LogNothingCollected(ILogger logger, long pullRequestId, Guid clientId);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Warning,
        Message =
            "No active SCM connection of client {ClientId} matches {Authority}; the close of PR {PullRequestId} is sealed without observing its threads.")]
    private static partial void LogNoConnection(ILogger logger, long pullRequestId, Guid clientId, string authority);

    [LoggerMessage(
        EventId = 6104,
        Level = LogLevel.Warning,
        Message =
            "Client {ClientId} has active connections of more than one provider family on {Authority}; closes there are sealed without observing their threads.")]
    private static partial void LogAmbiguousConnection(ILogger logger, Guid clientId, string authority);

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "Close observation failed for PR {PullRequestId} of client {ClientId}; the measurement is sealed from the judgments already recorded.")]
    private static partial void LogObservationFailed(ILogger logger, long pullRequestId, Guid clientId, Exception ex);

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "Comment-origin lookup failed for PR {PullRequestId} of client {ClientId}; observing threads without ProPR's own provenance.")]
    private static partial void LogOwnershipUnavailable(ILogger logger, long pullRequestId, Guid clientId, Exception ex);
}
