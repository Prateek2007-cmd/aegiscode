// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoCodeReviewQueryService(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory) : ICodeReviewQueryService
{
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null)
    {
        EnsureAzureDevOps(review.Repository.Host);

        var projectId = ResolveProjectId(review.Repository);
        AdoConnectionCredentials? credentials = null;
        if (context is not null)
        {
            var selected = await ManualReviewDiscoveryCredentials.ResolveAsync(
                connectionRepository, clientId, review.Repository.Host,
                context, new AdoReviewSourcePolicy().IsSelectedScopeCompatible, ct).ConfigureAwait(false);
            credentials = ToAdoCredentials(selected) ?? throw new InvalidOperationException("The selected connection is unavailable.");
        }

        var organizations = context is not null
            ? [context.ProviderScopePath]
            : await ResolveOrganizationUrlsAsync(
                connectionRepository,
                scopeRepository,
                clientId,
                review.Repository.Host,
                ct);
        foreach (var organizationUrl in organizations)
        {
            try
            {
                var gitClient = context is null || this.GitClientResolver is not null
                    ? await ResolveGitClientAsync(
                        connectionFactory,
                        connectionRepository,
                        this.GitClientResolver,
                        clientId,
                        organizationUrl,
                        ct)
                    : await (await connectionFactory.GetOverviewConnectionAsync(organizationUrl, credentials!, ct).ConfigureAwait(false))
                        .GetClientAsync<GitHttpClient>(ct).ConfigureAwait(false);
                var pullRequest = await gitClient.GetPullRequestAsync(
                    projectId,
                    review.Repository.ExternalRepositoryId,
                    review.Number,
                    cancellationToken: ct);
                if (pullRequest.Status is not (PullRequestStatus.Active or PullRequestStatus.Completed or PullRequestStatus.Abandoned))
                {
                    throw new InvalidOperationException("The provider returned an unknown review state.");
                }

                var revision = await AdoProviderAdapterHelpers.GetLatestRevisionAsync(
                    gitClient,
                    projectId,
                    review.Repository.ExternalRepositoryId,
                    review.Number,
                    ct);

                return ToDiscoveryItem(
                    review.Repository,
                    pullRequest,
                    revision,
                    SelectRequestedReviewer(review.Repository.Host, pullRequest), organizationUrl);
            }
            catch when (context is null && !ct.IsCancellationRequested)
            {
                // This organization doesn't have the pull request; try the next candidate organization URL.
            }
        }

        return null;
    }

    public async Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var item = await this.GetReviewAsync(clientId, review, ct);
        return item?.ReviewRevision;
    }
}
