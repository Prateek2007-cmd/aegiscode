// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Normalized review discovery item surfaced from a provider adapter.</summary>
/// <remarks>
///     <see cref="Creator" /> carries the stable provider identity of the account that opened the review request,
///     where the adapter reads one. <see cref="AuthorName" /> stays a display label.
/// </remarks>
public sealed record ReviewDiscoveryItemDto(
    ScmProvider Provider,
    RepositoryRef Repository,
    CodeReviewRef CodeReview,
    CodeReviewState ReviewState,
    ReviewRevision? ReviewRevision,
    ReviewerIdentity? RequestedReviewerIdentity,
    string Title,
    string? WebUrl,
    string? SourceBranch,
    string? TargetBranch,
    string? AuthorName = null,
    PullRequestAuthor? Creator = null);
