// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewDiscoveryProvider(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewDiscoveryPageProvider
{
    public ScmProvider Provider => ScmProvider.Forgejo;

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
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                repository.Host,
                $"/repos/{ForgejoCodeReviewQueryService.BuildRepositoryPath(repository)}/pulls",
                "state=open&limit=100" + (page == 1 ? "" : $"&page={page}")),
            authentication.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support.ForgejoReadFailures.ThrowIfDeniedOrThrottled(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await ReviewDiscoveryPaging.ReadAsync<ForgejoCodeReviewQueryService.ForgejoPullRequestResponse>(response, ct).ConfigureAwait(false);
        var total = ReviewDiscoveryPaging.Total(response, "X-Total-Count");
        var next = ReviewDiscoveryPaging.Next(response, request.RequestUri!, page, payload.Count, total: total, continuation: continuation);
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
        ForgejoCodeReviewQueryService.ForgejoPullRequestResponse payload,
        ReviewerIdentity reviewer)
    {
        return ForgejoCodeReviewQueryService.ContainsAssignedReviewer(payload, reviewer);
    }

    private static ReviewDiscoveryItemDto ToDiscoveryItem(
        RepositoryRef repository,
        ForgejoCodeReviewQueryService.ForgejoPullRequestResponse payload,
        ReviewerIdentity? requestedReviewerFilter)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Number);
        var requestedReviewer = ForgejoCodeReviewQueryService.SelectAssignedReviewer(payload, requestedReviewerFilter);

        ReviewerIdentity? mappedReviewer = null;
        if (requestedReviewer is not null)
        {
            var displayName = string.IsNullOrWhiteSpace(requestedReviewer.FullName)
                ? requestedReviewer.Login!
                : requestedReviewer.FullName!;
            mappedReviewer = new ReviewerIdentity(
                repository.Host,
                requestedReviewer.Id.ToString(CultureInfo.InvariantCulture),
                requestedReviewer.Login!,
                displayName,
                IsBot(requestedReviewer.Login));
        }

        return new ReviewDiscoveryItemDto(
            ScmProvider.Forgejo,
            repository,
            review,
            ForgejoCodeReviewQueryService.MapState(payload),
            ForgejoCodeReviewQueryService.BuildRevision(payload),
            mappedReviewer,
            payload.Title ?? $"Pull Request #{payload.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref,
            ReviewOverviewPresentation.AuthorName(payload.User?.FullName, payload.User?.Login));
    }

    private static bool IsBot(string? login)
    {
        return !string.IsNullOrWhiteSpace(login)
               && (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
                   || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
    }
}
