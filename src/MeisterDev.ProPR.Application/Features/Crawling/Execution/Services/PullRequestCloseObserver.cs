// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
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
    ICodeInsightMissHarvester? missHarvester = null,
    ICodeInsightFindingStore? findingStore = null,
    ICodeInsightsCollectionGate? collectionGate = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    ICodeInsightDispositionService? dispositionService = null,
    ICodeInsightHarvestCoverageRecorder? harvestCoverageRecorder = null,
    ICodeInsightProviderFeedbackReader? feedbackReader = null,
    IDiscussionSynchronizationService? discussionSynchronization = null,
    IDiscussionSynchronizationStore? discussionStore = null,
    IPullRequestThreadSnapshotReader? snapshotReader = null,
    TimeProvider? timeProvider = null) : ICodeInsightAcknowledgedCloseObserver, ICodeInsightHistoricalObserver
{
    public async Task ObserveAsync(CodeInsightPullRequestKey key, string providerScopePath, string providerProjectKey, CancellationToken ct = default)
        => _ = await this.ObserveAcknowledgedAsync(key, providerScopePath, providerProjectKey, ct);

    public async Task<CodeInsightEvidenceAcknowledgment> ObserveAcknowledgedAsync(
        CodeInsightPullRequestKey key, string providerScopePath,
        string providerProjectKey, CancellationToken ct = default)
    {
        if (discussionSynchronization is null)
        {
            return new(false);
        }

        var connection = await this.ResolveConnectionAsync(key.ClientId, providerScopePath, ct);
        if (connection is null)
        {
            return new(false);
        }

        var observation = await discussionSynchronization.ObserveAsync(
            new()
            {
                ClientId = key.ClientId, ConnectionId = connection.Id, Provider = connection.ProviderFamily,
                ProviderScopePath = providerScopePath, ProviderProjectKey = providerProjectKey, RepositoryId = key.RepositoryId,
                PullRequestId = checked((int)key.PullRequestId), PullRequestStatus = PrStatus.Completed, DiscussionOnly = true,
                AllowReviewSubmission = false, ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "close observation",
            }, ct);
        return new(observation.CurrentConsumersRetained && observation.ObservedPullRequestStatus is PrStatus.Completed or PrStatus.Abandoned);
    }

    public async Task<CodeInsightObservationResult> RefreshAsync(CodeInsightProviderEvidenceTarget target, CancellationToken ct = default)
    {
        if (discussionStore is null || !this.HasCapturedCoordinates(target))
        {
            return await this.RefreshCoreAsync(target, null, ct);
        }

        if (collectionGate is not null && !await collectionGate.IsCollectionEnabledAsync(target.Key.ClientId, ct))
        {
            return new CodeInsightObservationResult(CodeInsightObservationStatus.Disabled);
        }

        var connection = await this.ResolveHistoricalConnectionAsync(target, ct);
        if (connection is null)
        {
            return Unavailable(CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable);
        }

        var policy = providerRegistry.GetReviewSourcePolicy(target.Provider);
        var coordinates = policy.GetCacheCoordinates(target.ProviderScopePath, target.ProviderProjectKey);
        var scoped = new PullRequestThreadSnapshotTarget(
            target.Key.ClientId, connection.Id, target.Provider,
            coordinates.Authority + coordinates.ScopePath, coordinates.ProjectKey,
            policy.GetCacheCoordinates(target.ProviderScopePath, target.Key.RepositoryId).ProjectKey, checked((int)target.Key.PullRequestId));
        await using var lease = await discussionStore.TryAcquireAsync(scoped, null, true, TimeSpan.FromSeconds(300), ct);
        if (lease is null)
        {
            return Unavailable(CodeInsightObservationUnavailableReason.EvidenceRetentionFailed);
        }

        using var writeScope = lease.BeginWrites();
        await using var renewal = new DiscussionLeaseRenewal(lease, timeProvider ?? TimeProvider.System, ct);
        snapshotReader?.Invalidate(scoped);
        discussionSynchronization?.Invalidate(scoped);
        try
        {
            await lease.EnsureCurrentAsync(renewal.Token);
            return await this.RefreshCoreAsync(target with { ConnectionId = connection.Id }, lease.ObservedAt, renewal.Token);
        }
        finally
        {
            await renewal.StopAsync();
            snapshotReader?.Invalidate(scoped);
            discussionSynchronization?.Invalidate(scoped);
            renewal.ThrowIfLost();

            var progress = lease.State.ReadProgress();
            foreach (var state in progress.Values)
            {
                state.Consumers.Remove("disposition");
                state.Consumers.Remove("miss");
            }

            await lease.CompleteAsync(
                JsonSerializer.Serialize(progress), lease.State.ReviewerSummariesJson, lease.ObservedAt,
                false, false, lease.State.IsClosed, lease.State.LastReviewRevision, TimeSpan.FromSeconds(300), CancellationToken.None);
        }
    }

    private async Task<CodeInsightObservationResult> RefreshCoreAsync(
        CodeInsightProviderEvidenceTarget target,
        DateTimeOffset? observationCutoff, CancellationToken ct)
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
            var observedAt = observationCutoff ?? NormalizeDatabaseTimestamp(DateTimeOffset.UtcNow);
            failure = CodeInsightObservationUnavailableReason.ProviderReadFailed;
            var feedback = await feedbackReader.ReadAsync(target with { ConnectionId = connection.Id }, source.Ownership, ct);
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
        return connections.Where(row => (target.ConnectionId == Guid.Empty || row.Id == target.ConnectionId) && row.IsActive &&
                                        row.ProviderFamily == target.Provider && authority is not null
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

    /// <summary>Finds the client's active connection for the host the pull request lives on.</summary>
    private async Task<ClientScmConnectionDto?> ResolveConnectionAsync(
        Guid clientId,
        string providerScopePath,
        CancellationToken ct)
    {
        if (scmConnectionRepository is null)
        {
            return null;
        }

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

    [LoggerMessage(
        EventId = 6106, Level = LogLevel.Warning,
        Message =
            "Historical observation for PR {PullRequestId} of client {ClientId} is unavailable: {Reason}. Exception type: {ExceptionType}; error code: {ErrorCode}.")]
    private static partial void LogHistoricalObservationUnavailable(
        ILogger logger, long pullRequestId, Guid clientId, CodeInsightObservationUnavailableReason reason, string exceptionType, int errorCode);

    [LoggerMessage(
        EventId = 6104,
        Level = LogLevel.Warning,
        Message =
            "Client {ClientId} has active connections of more than one provider family on {Authority}; the close observation requires an unambiguous provider connection.")]
    private static partial void LogAmbiguousConnection(ILogger logger, Guid clientId, string authority);
}
