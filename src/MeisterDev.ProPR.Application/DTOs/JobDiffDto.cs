// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Carries the unified diffs of every file a review job reviewed, for the job-level
///     diff view. Each entry is the same <see cref="FileDiffDto" /> the per-file endpoint
///     returns, so a retained diff is served from the archive and any gap is re-fetched
///     from the source control provider exactly as the single-file read path does.
/// </summary>
/// <param name="JobId">The review job the diffs belong to.</param>
/// <param name="Files">One entry per reviewed file, ordered by file path. Empty when the job reviewed no files.</param>
public sealed record JobDiffDto(
    Guid JobId,
    IReadOnlyList<FileDiffDto> Files);
