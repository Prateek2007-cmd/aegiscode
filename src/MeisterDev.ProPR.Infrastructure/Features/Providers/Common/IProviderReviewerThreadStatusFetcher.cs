// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal interface IProviderReviewerThreadStatusFetcher : IReviewerThreadStatusFetcher
{
    ScmProvider Provider { get; }

    Task<IReadOnlyList<PrThreadStatusEntry>> GetFeedbackStatusesAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default)
    {
        return this.GetReviewerThreadStatusesAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct);
    }
}
