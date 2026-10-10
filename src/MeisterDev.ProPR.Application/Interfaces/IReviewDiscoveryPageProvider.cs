// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Optional bounded paging capability on a registered review discovery adapter.</summary>
public interface IReviewDiscoveryPageProvider : IReviewDiscoveryProvider
{
    /// <summary>Reads one native page. Continuations are opaque and interpreted only by the adapter.</summary>
    Task<ReviewDiscoveryPageDto> ListOpenReviewsPageAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        string? continuation = null,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null);
}
