// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

/// <summary>Reads authoritative thread evidence once within one scoped observation.</summary>
public interface IPullRequestThreadSnapshotReader
{
    void Invalidate(PullRequestThreadSnapshotTarget target)
    {
    }

    void AcknowledgeCurrentConsumers(PullRequestThreadSnapshotTarget target, PullRequestThreadSnapshot snapshot)
    {
    }

    Task<PullRequestThreadSnapshot> ReadTargetedAsync(
        PullRequestThreadSnapshotTarget target, IReadOnlyList<string> threadIds,
        ThreadOwnershipResolver ownership, CancellationToken ct = default) => this.ReadAsync(target, ownership, ct);

    Task<PullRequestThreadSnapshot> ReadAsync(PullRequestThreadSnapshotTarget target, ThreadOwnershipResolver ownership, CancellationToken ct = default);
}
