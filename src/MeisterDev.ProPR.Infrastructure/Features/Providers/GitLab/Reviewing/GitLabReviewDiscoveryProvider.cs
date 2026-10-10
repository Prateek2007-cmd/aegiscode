// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewDiscoveryProvider(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryPageProvider
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null)
    {
        return (await this.ListOpenReviewsPageAsync(clientId, repository, reviewer, ct: ct, context: context)).Items;
    }

    public async Task<ReviewDiscoveryPageDto> ListOpenReviewsPageAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        string? continuation = null,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null)
    {
        var page = ReviewDiscoveryPaging.PageNumber(continuation);
        var authentication = context is null
            ? await connectionVerifier.VerifyAsync(clientId, repository.Host, ct).ConfigureAwait(false)
            : await connectionVerifier.VerifyAsync(clientId, repository.Host, context, ct).ConfigureAwait(false);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/projects/{Uri.EscapeDataString(repository.ExternalRepositoryId)}/merge_requests",
                "state=opened&per_page=100" + (page == 1 ? "" : $"&page={page}")),
            authentication.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support.GitLabReadFailures.ThrowIfDeniedOrThrottled(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab review discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await ReviewDiscoveryPaging.ReadAsync<GitLabCodeReviewQueryService.GitLabMergeRequestResponse>(response, ct).ConfigureAwait(false);
        var total = ReviewDiscoveryPaging.Total(response, "X-Total");
        var next = ReviewDiscoveryPaging.Next(
            response, request.RequestUri!, page, payload.Count, numberedHeader: true, total: total, continuation: continuation);
        var ids = payload.Select(item => item.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (ids.Any(id => !long.TryParse(id, out var value) || value <= 0))
        {
            throw new InvalidOperationException("The provider returned an invalid discovery identity.");
        }

        var items = payload
            .Where(item => reviewer is null || ContainsRequestedReviewer(item, reviewer))
            .Select(item => ToDiscoveryItem(repository, item, reviewer))
            .ToList()
            .AsReadOnly();
        return new(items, next, payload.Count, ids) { TotalCount = total };
    }

    private static bool ContainsRequestedReviewer(
        GitLabCodeReviewQueryService.GitLabMergeRequestResponse payload,
        ReviewerIdentity reviewer)
    {
        return payload.Reviewers?.Any(candidate =>
            (!string.IsNullOrWhiteSpace(candidate.Username) && string.Equals(
                candidate.Username,
                reviewer.Login,
                StringComparison.OrdinalIgnoreCase)) ||
            candidate.Id.ToString(CultureInfo.InvariantCulture) == reviewer.ExternalUserId) == true;
    }

    private static ReviewDiscoveryItemDto ToDiscoveryItem(
        RepositoryRef repository,
        GitLabCodeReviewQueryService.GitLabMergeRequestResponse payload,
        ReviewerIdentity? requestedReviewerFilter)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Iid);
        var requestedReviewer = payload.Reviewers?
                                    .FirstOrDefault(candidate =>
                                        !string.IsNullOrWhiteSpace(candidate.Username)
                                        && (requestedReviewerFilter is null
                                            || string.Equals(
                                                candidate.Username,
                                                requestedReviewerFilter.Login,
                                                StringComparison.OrdinalIgnoreCase)
                                            || candidate.Id.ToString(CultureInfo.InvariantCulture) ==
                                            requestedReviewerFilter.ExternalUserId))
                                ?? payload.Reviewers?.FirstOrDefault(candidate =>
                                    !string.IsNullOrWhiteSpace(candidate.Username));

        ReviewerIdentity? mappedReviewer = null;
        if (requestedReviewer is not null)
        {
            var displayName = string.IsNullOrWhiteSpace(requestedReviewer.Name)
                ? requestedReviewer.Username!
                : requestedReviewer.Name!;
            mappedReviewer = new ReviewerIdentity(
                repository.Host,
                requestedReviewer.Id.ToString(CultureInfo.InvariantCulture),
                requestedReviewer.Username!,
                displayName,
                requestedReviewer.Bot);
        }

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitLab,
            repository,
            review,
            payload.Draft ? CodeReviewState.Draft : GitLabCodeReviewQueryService.MapState(payload.State),
            GitLabCodeReviewQueryService.BuildRevision(payload),
            mappedReviewer,
            payload.Title ?? $"Merge Request !{payload.Iid}",
            payload.WebUrl,
            payload.SourceBranch,
            payload.TargetBranch,
            ReviewOverviewPresentation.AuthorName(payload.Author?.Name, payload.Author?.Username));
    }
}
