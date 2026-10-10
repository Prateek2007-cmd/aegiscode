// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Selects and rechecks the non-secret authority used by scheduled review discovery.</summary>
public interface IScheduledReviewAuthorityService
{
    Task<ScheduledReviewAuthority> ResolveAsync(CrawlConfigurationDto target, Guid? connectionId = null, CancellationToken ct = default);
    Task<bool> IsCurrentAsync(ScheduledReviewAuthority expected, CancellationToken ct = default);
}

/// <summary>Captures selected connection, grant, reviewer and saved target evidence for one complete observation.</summary>
public sealed record ScheduledReviewAuthority(
    ClientScmConnectionDto Connection,
    IReadOnlyList<ClientScmScopeDto> Grants,
    ClientReviewerIdentityDto? ConfiguredReviewer,
    CrawlConfigurationDto Target)
{
    public ReviewDiscoveryContext DiscoveryContext => new(this.Connection.Id, this.Target.ProviderScopePath);

    public ReviewerIdentity? Reviewer => this.ConfiguredReviewer is { } reviewer
        ? new(new(this.Target.Provider, this.Target.ProviderScopePath), reviewer.ExternalUserId, reviewer.Login, reviewer.DisplayName, reviewer.IsBot)
        : null;
}
