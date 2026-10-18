// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

public interface IDiscussionSynchronizationService
{
    Task<PullRequestThreadSnapshotTarget?> ResolveTargetAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default)
        => Task.FromResult<PullRequestThreadSnapshotTarget?>(null);

    Task RequestFromDeliveryAsync(PullRequestSynchronizationRequest request, string? threadId, Guid deliveryId, CancellationToken ct = default);

    void Invalidate(PullRequestThreadSnapshotTarget target)
    {
    }

    Task<PullRequestThreadSnapshot> ObserveAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default);
    Task RequestAsync(PullRequestSynchronizationRequest request, string? threadId, CancellationToken ct = default);
}
