// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Execution;

internal sealed class ScheduledReviewDiscoveryService(
    IScmProviderRegistry registry,
    IScheduledReviewAuthorityService authority) : IAssignedReviewDiscoveryService
{
    public async Task<IReadOnlyList<AssignedCodeReviewRef>> ListAssignedOpenReviewsAsync(
        CrawlConfigurationDto config, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await this.DiscoverCompleteAsync(config, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Automatic review discovery did not complete within its deadline.", ex);
        }
    }

    private async Task<IReadOnlyList<AssignedCodeReviewRef>> DiscoverCompleteAsync(CrawlConfigurationDto config, CancellationToken ct)
    {
        var evidence = await authority.ResolveAsync(config, ct: ct).ConfigureAwait(false);
        var policy = registry.GetReviewSourcePolicy(config.Provider);
        IReadOnlyList<RepositoryRef> repositories;
        if (config.RepoFilters.Count > 0 && config.RepoFilters.All(filter => filter.CanonicalSourceRef is not null))
        {
            repositories = config.RepoFilters.Select(filter => policy.CreateCapturedRepository(
                config.ProviderScopePath, filter.CanonicalSourceRef!.Value, config.ProviderProjectKey, filter.RepositoryName)).Distinct().ToArray();
        }
        else
        {
            repositories = await registry.GetRepositoryDiscoveryProvider(config.Provider).ListRepositoriesAsync(
                new(config.ClientId, evidence.Connection.Id, new(config.Provider, evidence.Connection.HostBaseUrl)),
                config.ProviderScopePath, config.ProviderProjectKey, ct).ConfigureAwait(false);
            if (config.RepoFilters.Count > 0)
            {
                var selected = new List<RepositoryRef>();
                foreach (var filter in config.RepoFilters)
                {
                    var matches = repositories.Where(repository => filter.CanonicalSourceRef is not null
                        ? policy.MatchesCapturedRepository(
                            filter.CanonicalSourceRef.Value, filter.RepositoryName, config.ProviderProjectKey,
                            repository.ExternalRepositoryId, repository.ProjectPath)
                        : policy.MatchesRepositoryName(filter.RepositoryName, repository.RepositoryName ?? repository.ProjectPath)).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new InvalidOperationException("The saved repository filter does not resolve to one repository.");
                    }

                    selected.Add(matches[0]);
                }

                repositories = selected.Distinct().ToArray();
            }
        }

        var provider = registry.GetReviewDiscoveryProvider(config.Provider) as IReviewDiscoveryPageProvider
                       ?? throw new InvalidOperationException("Automatic review discovery is unavailable.");
        var result = new List<AssignedCodeReviewRef>();
        var rawCount = 0;
        foreach (var repository in repositories)
        {
            ct.ThrowIfCancellationRequested();
            if (!evidence.Grants.Any(grant => policy.MatchesScheduledRepositoryGrant(config, repository, grant)))
            {
                continue;
            }

            var continuations = new HashSet<string>(StringComparer.Ordinal);
            var observations = new HashSet<string>(StringComparer.Ordinal);
            var reviews = new HashSet<string>(StringComparer.Ordinal);
            string? continuation = null;
            do
            {
                var page = await provider.ListOpenReviewsPageAsync(config.ClientId, repository, evidence.Reviewer, continuation, ct, evidence.DiscoveryContext)
                    .ConfigureAwait(false);
                rawCount += page.RawCount;
                if (page.RawCount < 0 || rawCount > 10000 || page.ObservationIds.Count != page.RawCount ||
                    page.RawCount > 0 && page.ObservationIds.All(id => observations.Contains(id)))
                {
                    throw new InvalidOperationException("Automatic review discovery did not establish complete coverage.");
                }

                observations.UnionWith(page.ObservationIds);
                foreach (var listed in page.Items)
                {
                    var item = listed;
                    if (item.ReviewState is not (CodeReviewState.Open or CodeReviewState.Draft) || !reviews.Add(item.CodeReview.ExternalReviewId))
                    {
                        continue;
                    }

                    var filter = config.RepoFilters.FirstOrDefault(candidate => candidate.CanonicalSourceRef is not null
                        ? policy.MatchesCapturedRepository(
                            candidate.CanonicalSourceRef.Value, candidate.RepositoryName, config.ProviderProjectKey,
                            repository.ExternalRepositoryId, repository.ProjectPath)
                        : policy.MatchesRepositoryName(candidate.RepositoryName, repository.RepositoryName ?? repository.ProjectPath));
                    if (filter is not null && (!DestinationBranchPolicy.TryCreate(filter.TargetBranchPatterns, out var branches) ||
                                               !branches!.Matches(item.TargetBranch)))
                    {
                        continue;
                    }

                    if (item.ReviewRevision?.ProviderRevisionId is null)
                    {
                        item = await registry.GetCodeReviewQueryService(config.Provider).GetReviewAsync(
                                   config.ClientId, item.CodeReview, ct, evidence.DiscoveryContext).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("The provider could not confirm the discovered review revision.");
                        if (item.ReviewState is not (CodeReviewState.Open or CodeReviewState.Draft))
                        {
                            continue;
                        }

                        if (filter is not null && (!DestinationBranchPolicy.TryCreate(filter.TargetBranchPatterns, out var refreshedBranches) ||
                                                   !refreshedBranches!.Matches(item.TargetBranch)))
                        {
                            continue;
                        }
                    }

                    if (item.ReviewRevision is null)
                    {
                        throw new InvalidOperationException("Automatic review discovery requires a native revision.");
                    }

                    var alias = int.TryParse(item.ReviewRevision.ProviderRevisionId, NumberStyles.None, CultureInfo.InvariantCulture, out var nativeNumber) &&
                                nativeNumber > 0
                        ? nativeNumber
                        : 1;
                    result.Add(
                        new(
                            item.Repository.Host, item.Repository, item.CodeReview, alias, item.Title,
                            item.Repository.RepositoryName, item.SourceBranch, item.TargetBranch, item.ReviewRevision, evidence));
                }

                continuation = page.Continuation;
                if (continuation is not null && !continuations.Add(continuation))
                {
                    throw new InvalidOperationException("Automatic review discovery repeated a continuation.");
                }
            } while (continuation is not null);
        }

        ct.ThrowIfCancellationRequested();
        return result;
    }
}
