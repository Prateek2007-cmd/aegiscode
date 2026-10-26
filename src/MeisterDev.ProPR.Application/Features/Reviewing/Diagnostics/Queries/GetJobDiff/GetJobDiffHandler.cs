// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetJobDiff;

/// <summary>
///     Handles <see cref="GetJobDiffQuery" /> by resolving the diff of every file result on
///     the job through <see cref="GetFileDiffHandler" />, so the job-level read path serves
///     retained diffs first and re-fetches from the provider exactly like the per-file
///     endpoint. Read-only: it never alters the diff used during a live review.
///     Returns <see langword="null" /> when the job does not exist.
/// </summary>
public sealed class GetJobDiffHandler(
    IJobRepository jobRepository,
    GetFileDiffHandler getFileDiffHandler)
{
    /// <summary>
    ///     Returns the job's diffs, one entry per reviewed file ordered by file path,
    ///     or <see langword="null" /> when the job is not found.
    /// </summary>
    public async Task<JobDiffDto?> HandleAsync(GetJobDiffQuery query, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdWithFileResultsAsync(query.JobId, ct).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        var files = new List<FileDiffDto>();
        foreach (var fileResult in job.FileReviewResults.OrderBy(result => result.FilePath, StringComparer.Ordinal))
        {
            var diff = await getFileDiffHandler.HandleForFileAsync(job, fileResult.Id, ct).ConfigureAwait(false);
            files.Add(diff);
        }

        return new JobDiffDto(job.Id, files);
    }
}
