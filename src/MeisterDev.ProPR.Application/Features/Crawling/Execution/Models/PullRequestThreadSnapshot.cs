// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

/// <summary>Identifies a client-owned provider observation without credentials.</summary>
public sealed record PullRequestThreadSnapshotTarget(
    Guid ClientId,
    Guid ConnectionId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId)
{
    /// <summary>Identifies shared evidence independently of captured credentials.</summary>
    public Guid GetEvidenceSourceId(IScmProviderRegistry providers)
    {
        var scope = ProviderSourceIdentity.FromReviewSource(
            this.Provider, this.ProviderScopePath,
            providers.GetSourceIdentityPolicy(this.Provider)).Value;
        var repository = providers.GetReviewSourcePolicy(this.Provider).GetCacheCoordinates(this.ProviderScopePath, this.RepositoryId).ProjectKey;
        return DiscussionSynchronizationState.Identity(this.ClientId, Guid.Empty, this.Provider, scope, "", repository, this.PullRequestId);
    }
}

/// <summary>One complete provider enumeration and its ownership-dependent reviewer projection.</summary>
public sealed record PullRequestThreadSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<PrCommentThread> Threads,
    IReadOnlyList<PrThreadStatusEntry> ReviewerThreads,
    bool EnumerationComplete = true,
    string? TargetThreadId = null)
{
    public IReadOnlyList<string>? TargetThreadIds { get; init; }
    public ThreadOwnerIdentity? AuthenticatedIdentity { get; init; }
    public ReviewRevision? ObservedReviewRevision { get; init; }
    public PrStatus? ObservedPullRequestStatus { get; init; }
    public Exception? ReadFailure { get; init; }
    public bool CurrentConsumersRetained { get; init; }
}

/// <summary>Retains authoritative lifecycle coordinates when discussion evidence remains unavailable.</summary>
public sealed class DiscussionObservationUnavailableException(PrStatus status, ReviewRevision? revision, Exception inner)
    : InvalidOperationException("Authoritative discussion evidence could not be retained.", inner)
{
    public PrStatus PullRequestStatus { get; } = status;
    public ReviewRevision? ReviewRevision { get; } = revision;
}
