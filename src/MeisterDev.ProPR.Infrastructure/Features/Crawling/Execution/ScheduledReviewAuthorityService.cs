// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Execution;

internal sealed class ScheduledReviewAuthorityService(
    IClientScmConnectionRepository connections,
    IClientScmScopeRepository scopes,
    IClientReviewerIdentityRepository reviewers,
    IScmProviderRegistry registry,
    IProviderActivationService activation) : IScheduledReviewAuthorityService
{
    public async Task<ScheduledReviewAuthority> ResolveAsync(CrawlConfigurationDto target, Guid? connectionId = null, CancellationToken ct = default)
    {
        if (!await activation.IsEnabledAsync(target.Provider, ct).ConfigureAwait(false) ||
            registry.GetReviewDiscoveryProvider(target.Provider) is not IReviewDiscoveryPageProvider)
        {
            throw Refusal();
        }

        var policy = registry.GetConnectionConfigurationPolicy(target.Provider);
        var source = registry.GetReviewSourcePolicy(target.Provider);
        var selection = policy.PrepareOperationalConnectionSelection(target.ProviderScopePath);
        var observedHost = new ProviderHostRef(target.Provider, target.ProviderScopePath).HostBaseUrl;
        var operationalSelection = policy.PrepareOperationalConnectionSelection(observedHost);
        var alternatives = (await connections.GetOperationalSelectionCandidatesAsync(target.ClientId, ct).ConfigureAwait(false))
            .Where(candidate => candidate.ClientId == target.ClientId && candidate.IsActive &&
                                (candidate.ProviderFamily == target.Provider &&
                                 ((selection.ExactHost is not null && string.Equals(
                                      selection.ExactHost, candidate.HostBaseUrl, StringComparison.OrdinalIgnoreCase)) ||
                                  (selection.ExactHost is null && selection.MatchesStoredHost(candidate.HostBaseUrl)) ||
                                  (operationalSelection.ExactHost is not null && string.Equals(
                                      operationalSelection.ExactHost, candidate.HostBaseUrl, StringComparison.OrdinalIgnoreCase)) ||
                                  (operationalSelection.ExactHost is null && operationalSelection.MatchesStoredHost(candidate.HostBaseUrl)) ||
                                  policy.MatchesObservedConnectionHost(candidate.HostBaseUrl, target.ProviderScopePath)) ||
                                 string.Equals(candidate.HostBaseUrl.TrimEnd('/'), target.ProviderScopePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(candidate.HostBaseUrl.TrimEnd('/'), observedHost, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (alternatives.Length != 1)
        {
            throw Refusal();
        }

        var selected = alternatives[0];
        if (selected.ProviderFamily != target.Provider || connectionId.HasValue && selected.Id != connectionId.Value ||
            !string.Equals(selected.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase) ||
            !source.IsSelectedScopeCompatible(selected.HostBaseUrl, target.ProviderScopePath))
        {
            throw Refusal();
        }

        var grants = (await scopes.GetByConnectionIdAsync(target.ClientId, selected.Id, ct).ConfigureAwait(false))
            .Where(grant => grant.ClientId == target.ClientId && grant.ConnectionId == selected.Id && grant.IsEnabled &&
                            source.MatchesScheduledScopeGrant(target, grant))
            .OrderBy(grant => grant.Id).ToArray();
        if (grants.Length == 0 || target.OrganizationScopeId.HasValue && !grants.Any(grant => grant.Id == target.OrganizationScopeId.Value))
        {
            throw Refusal();
        }

        foreach (var filter in target.RepoFilters.Where(filter => filter.CanonicalSourceRef is not null))
        {
            var repository = source.CreateCapturedRepository(
                target.ProviderScopePath, filter.CanonicalSourceRef!.Value, target.ProviderProjectKey, filter.RepositoryName);
            if (!grants.Any(grant => source.MatchesScheduledRepositoryGrant(target, repository, grant)))
            {
                throw Refusal();
            }
        }

        var reviewer = await reviewers.GetByConnectionIdAsync(target.ClientId, selected.Id, ct).ConfigureAwait(false);
        if (reviewer is not null &&
            (reviewer.ClientId != target.ClientId || reviewer.ConnectionId != selected.Id || reviewer.ProviderFamily != target.Provider))
        {
            throw Refusal();
        }

        return new(selected, grants, reviewer, target);
    }

    public async Task<bool> IsCurrentAsync(ScheduledReviewAuthority expected, CancellationToken ct = default)
    {
        try
        {
            var current = await this.ResolveAsync(expected.Target, expected.Connection.Id, ct).ConfigureAwait(false);
            return current.Connection == expected.Connection && current.ConfiguredReviewer == expected.ConfiguredReviewer &&
                   current.Grants.SequenceEqual(expected.Grants);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static InvalidOperationException Refusal() => new("Automatic review authority is unavailable. Check the connection and repository scopes.");
}
