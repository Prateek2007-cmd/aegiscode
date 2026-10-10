// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Result of a revision-conditional automatic-review activity change.</summary>
public enum ReviewTargetAutomationResult
{
    Changed,
    NotFound,
    Conflict,
    Unavailable,
}

/// <summary>Contains the activity-change outcome and its confirmed saved snapshot when successful.</summary>
public sealed record ReviewTargetAutomationOutcome(ReviewTargetAutomationResult Status, CrawlConfigurationDto? SavedTarget = null);

/// <summary>Changes saved automatic-review activity under target admission serialization.</summary>
public interface IReviewTargetAutomationService
{
    /// <summary>Enables with selected current authority or pauses without credentials.</summary>
    Task<ReviewTargetAutomationOutcome> ChangeAsync(
        Guid clientId, Guid targetId, long expectedRevision, bool isActive, Guid? connectionId, CancellationToken ct = default);
}

/// <summary>Validates saved revision and enable authority before a conditional activity write.</summary>
public sealed class ReviewTargetAutomationService(ICrawlConfigurationRepository repository, IScheduledReviewAuthorityService authority)
    : IReviewTargetAutomationService
{
    /// <inheritdoc />
    public async Task<ReviewTargetAutomationOutcome> ChangeAsync(
        Guid clientId, Guid targetId, long expectedRevision, bool isActive, Guid? connectionId, CancellationToken ct = default)
    {
        await using var lease = await repository.AcquireReviewTargetAdmissionAsync(clientId, ct).ConfigureAwait(false);
        var target = await repository.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct).ConfigureAwait(false);
        if (target is null || target.ClientId != clientId || target.Id != targetId || target.RepoFilters.Count != 1 ||
            target.RepoFilters[0].CanonicalSourceRef is null)
        {
            return new(ReviewTargetAutomationResult.NotFound);
        }

        if (expectedRevision < 1 || expectedRevision == long.MaxValue || target.ReviewTargetRevision != expectedRevision ||
            isActive && target.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            return new(ReviewTargetAutomationResult.Conflict);
        }

        if (isActive)
        {
            if (connectionId is null || connectionId == Guid.Empty)
            {
                return new(ReviewTargetAutomationResult.Unavailable);
            }

            try
            {
                var selected = await authority.ResolveAsync(target, connectionId, ct).ConfigureAwait(false);
                if (!await authority.IsCurrentAsync(selected, ct).ConfigureAwait(false))
                {
                    return new(ReviewTargetAutomationResult.Unavailable);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new(ReviewTargetAutomationResult.Unavailable);
            }
        }

        if (!await repository.ChangeReviewTargetAutomationUnderAdmissionAsync(targetId, clientId, expectedRevision, isActive, ct).ConfigureAwait(false))
        {
            return new(ReviewTargetAutomationResult.Conflict);
        }

        var saved = await repository.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct).ConfigureAwait(false);
        if (saved is null || saved.ClientId != clientId || saved.Id != targetId || saved.IsActive != isActive ||
            saved.ReviewTargetRevision != expectedRevision + 1 || saved.RepoFilters.Count != 1 ||
            saved.RepoFilters[0].CanonicalSourceRef is null || isActive && saved.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            return new(ReviewTargetAutomationResult.Conflict);
        }

        return new(ReviewTargetAutomationResult.Changed, saved);
    }
}
