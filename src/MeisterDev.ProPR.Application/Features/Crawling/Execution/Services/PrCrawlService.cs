// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedReviewDiscoveryService prFetcher,
    IJobRepository jobs,
    IPrStatusFetcher prStatusFetcher,
    ILogger<PrCrawlService> logger,
    IPullRequestSynchronizationService pullRequestSynchronizationService,
    IProviderActivationService? providerActivationService = null,
    IClientRegistry? clientRegistry = null,
    IScmProviderRegistry? providerRegistry = null,
    IScheduledReviewAuthorityService? scheduledAuthority = null) : IPrCrawlService
{
    /// <summary>
    ///     Runs one crawl cycle across all active configurations, creating review jobs for newly discovered pull requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the crawl cycle.</param>
    public async Task CrawlAsync(CancellationToken cancellationToken = default)
    {
        var configs = await crawlConfigs.GetAllActiveAsync(cancellationToken);
        LogCrawlStarted(logger, configs.Count);

        foreach (var config in configs)
        {
            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(config.Provider, cancellationToken))
            {
                continue;
            }

            IReadOnlyList<AssignedCodeReviewRef> assignedPrs;
            ResolvedReviewer reviewerContext;
            try
            {
                assignedPrs = await prFetcher.ListAssignedOpenReviewsAsync(config, cancellationToken);
                reviewerContext = scheduledAuthority is null
                    ? await this.ResolveReviewerContextAsync(config, cancellationToken)
                    : new ResolvedReviewer(null);
                LogPrsDiscovered(logger, assignedPrs.Count, config.ProviderScopePath, config.ProviderProjectKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConfigFetchError(logger, config.ProviderScopePath, config.ProviderProjectKey, ex);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.ProcessDiscoveredPullRequestsAsync(config, assignedPrs, reviewerContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTargetAdmissionError(logger, config.Id, ex);
            }

            try
            {
                await this.DetectAbandonedJobsAsync(config, assignedPrs, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTargetAdmissionError(logger, config.Id, ex);
            }
        }
    }

    private async Task ProcessDiscoveredPullRequestsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        ResolvedReviewer reviewerContext,
        CancellationToken cancellationToken)
    {
        foreach (var pr in assignedPrs)
        {
            try
            {
                var canonical = config.RepoFilters.Count == 1 && config.RepoFilters[0].CanonicalSourceRef is not null;
                var request = new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl discovery",
                    ClientId = config.ClientId,
                    ProviderScopePath = config.ProviderScopePath,
                    ProviderProjectKey = config.ProviderProjectKey,
                    RepositoryId = pr.Repository.ExternalRepositoryId,
                    PullRequestId = pr.CodeReview.Number,
                    PullRequestStatus = PrStatus.Active,
                    Provider = pr.Host.Provider,
                    Host = pr.Host,
                    Repository = pr.Repository,
                    CodeReview = pr.CodeReview,
                    ReviewRevision = pr.ReviewRevision,
                    RequestedReviewerIdentity = pr.Authority is not null ? pr.Authority.Reviewer : reviewerContext.ConfiguredTriggerReviewer,
                    CandidateIterationId = pr.RevisionId,
                    PrTitle = pr.ReviewTitle,
                    RepositoryName = pr.RepositoryDisplayName,
                    SourceBranch = pr.SourceBranch,
                    TargetBranch = pr.TargetBranch,
                    ProCursorSourceScopeMode = config.ProCursorSourceScopeMode,
                    ProCursorSourceIds = config.ProCursorSourceIds ?? [],
                    InvalidProCursorSourceIds = config.InvalidProCursorSourceIds ?? [],
                    ReviewTemperature = config.ReviewTemperature,
                };
                if (scheduledAuthority is not null)
                {
                    if (pr.Authority is null)
                    {
                        continue;
                    }

                    await using (var initialAdmission =
                                 await crawlConfigs.AcquireReviewTargetAdmissionAsync(config.ClientId, cancellationToken).ConfigureAwait(false))
                    {
                        var before = await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false);
                        if (before is null || !await scheduledAuthority.IsCurrentAsync(pr.Authority, cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }

                    var selectedPrepared = await pullRequestSynchronizationService.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
                    await using var selectedAdmission =
                        await crawlConfigs.AcquireReviewTargetAdmissionAsync(config.ClientId, cancellationToken).ConfigureAwait(false);
                    var after = await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false);
                    if (after is not null && await scheduledAuthority.IsCurrentAsync(pr.Authority, cancellationToken).ConfigureAwait(false))
                    {
                        await selectedPrepared.CompleteAsync(
                                new PullRequestReviewSettings(
                                    after.ProCursorSourceScopeMode,
                                    after.ProCursorSourceIds ?? [], after.InvalidProCursorSourceIds ?? [], after.ReviewTemperature), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                if (!canonical)
                {
                    if (await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        await this.TrySynchronizeAsync(request, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var prepared = await pullRequestSynchronizationService.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
                await using var admission = await crawlConfigs.AcquireReviewTargetAdmissionAsync(config.ClientId, cancellationToken).ConfigureAwait(false);
                var current = await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    await prepared.CompleteAsync(
                        new PullRequestReviewSettings(
                            current.ProCursorSourceScopeMode,
                            current.ProCursorSourceIds ?? [], current.InvalidProCursorSourceIds ?? [], current.ReviewTemperature), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTargetAdmissionError(logger, config.Id, ex);
            }
        }
    }

    private async Task<CrawlConfigurationDto?> GetCurrentAdmissionConfigurationAsync(
        CrawlConfigurationDto discovered,
        AssignedCodeReviewRef pr,
        CancellationToken ct)
    {
        var canonical = discovered.RepoFilters.Count == 1 && discovered.RepoFilters[0].CanonicalSourceRef is not null;
        var current = canonical || scheduledAuthority is not null
            ? await crawlConfigs.GetReviewTargetPolicySnapshotAsync(discovered.Id, discovered.ClientId, ct).ConfigureAwait(false)
            : discovered;
        if (current is null || current.ClientId != discovered.ClientId || current.Id != discovered.Id)
        {
            return null;
        }

        if (!current.IsActive || current.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            return null;
        }

        if (current.ReviewTargetRevision != discovered.ReviewTargetRevision)
        {
            return null;
        }

        if (!canonical && scheduledAuthority is null)
        {
            return current;
        }

        if (current.Provider != discovered.Provider || current.Provider != pr.Host.Provider)
        {
            return null;
        }

        var currentHost = new ProviderHostRef(current.Provider, current.ProviderScopePath);
        if (!Equals(currentHost, pr.Host) || !Equals(pr.Host, pr.Repository.Host))
        {
            return null;
        }

        var scopeUnchanged = string.Equals(current.ProviderScopePath, discovered.ProviderScopePath, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(current.ProviderProjectKey, discovered.ProviderProjectKey, StringComparison.OrdinalIgnoreCase);
        if (!scopeUnchanged)
        {
            return null;
        }

        if (current.OrganizationScopeId != discovered.OrganizationScopeId || current.RepoFilters.Count != discovered.RepoFilters.Count ||
            current.RepoFilters.Where((filter, index) => filter.Id != discovered.RepoFilters[index].Id ||
                                                         filter.RepositoryName != discovered.RepoFilters[index].RepositoryName ||
                                                         !Equals(filter.CanonicalSourceRef, discovered.RepoFilters[index].CanonicalSourceRef) ||
                                                         !filter.TargetBranchPatterns.SequenceEqual(
                                                             discovered.RepoFilters[index].TargetBranchPatterns, StringComparer.Ordinal)).Any())
        {
            return null;
        }

        if (providerRegistry is null)
        {
            return null;
        }

        var sourcePolicy = providerRegistry.GetReviewSourcePolicy(current.Provider);
        if (scheduledAuthority is not null && (pr.Authority is null ||
                                               !pr.Authority.Grants.Any(grant => sourcePolicy.MatchesScheduledRepositoryGrant(current, pr.Repository, grant))))
        {
            return null;
        }

        if (!canonical)
        {
            var matching = current.RepoFilters.FirstOrDefault(filter => filter.CanonicalSourceRef is not null
                ? sourcePolicy.MatchesCapturedRepository(
                    filter.CanonicalSourceRef.Value, filter.RepositoryName, current.ProviderProjectKey,
                    pr.Repository.ExternalRepositoryId, pr.Repository.ProjectPath)
                : sourcePolicy.MatchesRepositoryName(filter.RepositoryName, pr.Repository.RepositoryName ?? pr.Repository.ProjectPath));
            if (current.RepoFilters.Count > 0 && matching is null)
            {
                return null;
            }

            return matching is null || DestinationBranchPolicy.TryCreate(matching.TargetBranchPatterns, out var branches) && branches!.Matches(pr.TargetBranch)
                ? current
                : null;
        }

        if (current.RepoFilters.Count != 1 || current.RepoFilters[0].CanonicalSourceRef is null)
        {
            return null;
        }

        if (!Equals(current.RepoFilters[0].CanonicalSourceRef, discovered.RepoFilters[0].CanonicalSourceRef))
        {
            return null;
        }

        var filter = current.RepoFilters[0];
        var repositoryMatches = providerRegistry.GetReviewSourcePolicy(current.Provider).MatchesRepository(current, pr.Repository);

        if (!repositoryMatches || !DestinationBranchPolicy.TryCreate(filter.TargetBranchPatterns, out var policy))
        {
            return null;
        }

        return policy!.Matches(pr.TargetBranch) ? current : null;
    }

    // Check live lifecycle state for active jobs absent from discovery and forward it
    // to shared synchronization without admitting another review.
    private async Task DetectAbandonedJobsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        CancellationToken cancellationToken)
    {
        var activeJobs = await jobs.GetActiveJobsForConfigAsync(
            config.ClientId,
            config.ProviderScopePath,
            config.ProviderProjectKey,
            cancellationToken);
        var repositoryIds = config.RepoFilters.Count > 0 && config.RepoFilters.All(filter => filter.CanonicalSourceRef is not null)
            ? config.RepoFilters.Select(filter => filter.CanonicalSourceRef!.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var discoveredPrIds = assignedPrs
            .Select(pr => (pr.Repository.ExternalRepositoryId.ToUpperInvariant(), pr.CodeReview.Number))
            .ToHashSet();

        foreach (var activeJob in activeJobs)
        {
            if (activeJob.ClientId != config.ClientId ||
                (repositoryIds is not null && !repositoryIds.Contains(activeJob.RepositoryId)) ||
                discoveredPrIds.Contains((activeJob.RepositoryId.ToUpperInvariant(), activeJob.PullRequestId)))
            {
                continue;
            }

            LogAbandonmentCheckStarted(logger, activeJob.Id, activeJob.PullRequestId);

            PrStatus status;
            if (scheduledAuthority is not null && providerRegistry is not null)
            {
                try
                {
                    var authority = await scheduledAuthority.ResolveAsync(config, ct: cancellationToken).ConfigureAwait(false);
                    var repository = providerRegistry.GetReviewSourcePolicy(config.Provider).CreateCapturedRepository(
                        config.ProviderScopePath, activeJob.RepositoryId, config.ProviderProjectKey, activeJob.PrRepositoryName ?? activeJob.RepositoryId);
                    if (activeJob.Provider == config.Provider && activeJob.RepositoryProjectPath is not null)
                    {
                        repository = activeJob.RepositoryReference;
                    }

                    if (!authority.Grants.Any(grant =>
                            providerRegistry.GetReviewSourcePolicy(config.Provider).MatchesScheduledRepositoryGrant(config, repository, grant)))
                    {
                        continue;
                    }

                    var review = new CodeReviewRef(
                        repository, CodeReviewPlatformKind.PullRequest, activeJob.PullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        activeJob.PullRequestId);
                    var observed = await providerRegistry.GetCodeReviewQueryService(config.Provider).GetReviewAsync(
                        config.ClientId, review, cancellationToken, authority.DiscoveryContext).ConfigureAwait(false);
                    if (observed is null ||
                        observed.ReviewState is not (CodeReviewState.Open or CodeReviewState.Draft or CodeReviewState.Closed or CodeReviewState.Merged) ||
                        !await scheduledAuthority.IsCurrentAsync(authority, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    status = observed.ReviewState switch
                    {
                        CodeReviewState.Merged => PrStatus.Completed,
                        CodeReviewState.Closed => PrStatus.Abandoned,
                        _ => PrStatus.Active,
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTargetAdmissionError(logger, config.Id, ex);
                    continue;
                }
            }
            else
            {
                status = await prStatusFetcher.GetStatusAsync(
                    config.ProviderScopePath,
                    config.ProviderProjectKey,
                    activeJob.RepositoryId,
                    activeJob.PullRequestId,
                    config.ClientId,
                    cancellationToken);
            }

            await this.TrySynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl disappearance",
                    ClientId = config.ClientId,
                    ProviderScopePath = config.ProviderScopePath,
                    ProviderProjectKey = config.ProviderProjectKey,
                    RepositoryId = activeJob.RepositoryId,
                    PullRequestId = activeJob.PullRequestId,
                    PullRequestStatus = status,

                    // Synchronization uses the captured family to select the client's provider connection.
                    Provider = config.Provider,
                    AllowReviewSubmission = false,
                },
                cancellationToken);
        }
    }

    private async Task TrySynchronizeAsync(PullRequestSynchronizationRequest request, CancellationToken ct)
    {
        try
        {
            await pullRequestSynchronizationService.SynchronizeAsync(request, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSynchronizationFailed(
                logger,
                request.PullRequestId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.SummaryLabel,
                ex);
        }
    }

    private async Task<ResolvedReviewer> ResolveReviewerContextAsync(
        CrawlConfigurationDto config,
        CancellationToken ct)
    {
        if (clientRegistry is null)
        {
            return new ResolvedReviewer(null);
        }

        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        return new ResolvedReviewer(await clientRegistry.GetReviewerIdentityAsync(config.ClientId, host, ct));
    }

    private sealed record ResolvedReviewer(ReviewerIdentity? ConfiguredTriggerReviewer);
}
