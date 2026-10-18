// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal sealed class PullRequestThreadSnapshotReader(IEnumerable<IProviderReviewerThreadStatusFetcher> adapters, IScmProviderRegistry providers)
    : IPullRequestThreadSnapshotReader
{
    private readonly Dictionary<PullRequestThreadSnapshotTarget, Task<PullRequestThreadSnapshot>> _observations = [];

    public void Invalidate(PullRequestThreadSnapshotTarget target)
    {
        var source = this.Normalize(target).GetEvidenceSourceId(providers);
        foreach (var cached in this._observations.Keys.Where(key => key.GetEvidenceSourceId(providers) == source).ToArray())
        {
            this._observations.Remove(cached);
        }
    }

    public void AcknowledgeCurrentConsumers(PullRequestThreadSnapshotTarget target, PullRequestThreadSnapshot snapshot)
    {
        target = this.Normalize(target);
        if (snapshot.EnumerationComplete && snapshot.TargetThreadId is null)
        {
            this._observations[target] = Task.FromResult(snapshot with { CurrentConsumersRetained = true });
        }
    }

    public async Task<PullRequestThreadSnapshot> ReadTargetedAsync(
        PullRequestThreadSnapshotTarget target, IReadOnlyList<string> threadIds,
        ThreadOwnershipResolver ownership, CancellationToken ct = default)
    {
        target = this.Normalize(target);
        if (this._observations.ContainsKey(target))
        {
            return await this.ReadAsync(target, ownership, ct);
        }

        var adapter = adapters.SingleOrDefault(adapter => adapter.Provider == target.Provider)
                      ?? throw new InvalidOperationException("The provider has no authoritative thread snapshot reader.");
        var snapshot = await adapter.ReadTargetedSnapshotAsync(
            target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId,
            target.PullRequestId, threadIds, ownership, target.ClientId, ct, target.ConnectionId);
        if (snapshot.EnumerationComplete)
        {
            this._observations[target] = Task.FromResult(snapshot);
        }

        return snapshot;
    }

    public async Task<PullRequestThreadSnapshot> ReadAsync(
        PullRequestThreadSnapshotTarget target, ThreadOwnershipResolver ownership, CancellationToken ct = default)
    {
        target = this.Normalize(target);
        if (!this._observations.TryGetValue(target, out var observation))
        {
            var adapter = adapters.SingleOrDefault(adapter => adapter.Provider == target.Provider)
                          ?? throw new InvalidOperationException("The provider has no authoritative thread snapshot reader.");
            observation = adapter.ReadSnapshotAsync(
                target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId,
                target.PullRequestId, ownership, target.ClientId, ct, target.ConnectionId == Guid.Empty ? null : target.ConnectionId);
            this._observations.Add(target, observation);
        }

        try
        {
            var snapshot = await observation;
            if (snapshot.AuthenticatedIdentity is not null)
            {
                ownership.ContributeIdentity(snapshot.AuthenticatedIdentity.Value);
            }

            return snapshot;
        }
        catch
        {
            this._observations.Remove(target);
            throw;
        }
    }

    private PullRequestThreadSnapshotTarget Normalize(PullRequestThreadSnapshotTarget target)
    {
        var policy = providers.GetReviewSourcePolicy(target.Provider);
        var coordinates = policy.GetCacheCoordinates(target.ProviderScopePath, target.ProviderProjectKey);
        return target with
        {
            ProviderScopePath = coordinates.Authority + coordinates.ScopePath,
            ProviderProjectKey = coordinates.ProjectKey,
            RepositoryId = policy.GetCacheCoordinates(target.ProviderScopePath, target.RepositoryId).ProjectKey
        };
    }
}
