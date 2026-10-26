// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetJobDiff;

/// <summary>
///     Query for retrieving the unified diffs of every file a review job reviewed.
/// </summary>
/// <param name="JobId">The review job identifier.</param>
public sealed record GetJobDiffQuery(Guid JobId);
