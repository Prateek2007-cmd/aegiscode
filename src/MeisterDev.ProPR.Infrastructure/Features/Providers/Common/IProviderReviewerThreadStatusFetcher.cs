// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal interface IProviderReviewerThreadStatusFetcher : IReviewerThreadStatusFetcher
{
    ScmProvider Provider { get; }

    Task<PullRequestThreadSnapshot> ReadTargetedSnapshotAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        IReadOnlyList<string> threadIds, ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default, Guid? connectionId = null)
        => this.ReadSnapshotAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct, connectionId);

    Task<PullRequestThreadSnapshot> ReadSnapshotAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default, Guid? connectionId = null)
    {
        throw new NotSupportedException("The provider has no authoritative thread snapshot reader.");
    }


    Task<IReadOnlyList<PrThreadStatusEntry>> GetFeedbackStatusesAsync(
        string organizationUrl, string projectId, string repositoryId, int pullRequestId,
        ThreadOwnershipResolver ownership, Guid clientId, CancellationToken ct = default)
    {
        return this.GetReviewerThreadStatusesAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct);
    }
}
