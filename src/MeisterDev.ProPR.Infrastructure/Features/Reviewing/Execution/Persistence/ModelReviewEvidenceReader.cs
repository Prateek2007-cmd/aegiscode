// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>Reads bounded immutable execution checkpoints in the caller's database snapshot.</summary>
public sealed class ModelReviewEvidenceReader(MeisterProPRDbContext db, IEnumerable<IReviewSourcePolicy>? sourcePolicies = null) : IModelReviewEvidenceReader
{
    public const int MaximumJobs = 4096;
    public const int MaximumFiles = 100_000;
    public const int MaximumBytes = 32 * 1024 * 1024;

    private readonly IReadOnlyDictionary<MeisterDev.ProPR.Domain.Enums.ScmProvider, IReviewSourcePolicy> _policies =
        (sourcePolicies ?? ScmLocalPolicyFactory.CreateSourcePolicies()).ToDictionary(policy => policy.Provider);

    public async Task<ModelReviewEvidenceSnapshot> ReadAsync(
        IReadOnlyList<ModelReviewSource> sources, DateTimeOffset capturedAt, CancellationToken ct = default)
    {
        if (sources.Count == 0)
        {
            return new([], true);
        }

        if (sources.Count > MaximumFiles || sources.Sum(source => (long)source.RepositoryId.Length + source.ProviderScope.Length) > 1_000_000)
        {
            return Unavailable();
        }

        var requested = sources.Distinct().ToArray();
        var requestedKeys = requested.ToLookup(source => (source.ClientId, source.RepositoryId, source.PullRequestId, source.ProviderScope));
        IQueryable<ReviewJob> jobs;
        if (db.Database.IsNpgsql())
        {
            var json = JsonSerializer.Serialize(
                requested.Select(source => new
                {
                    client_id = source.ClientId, repository_id = source.RepositoryId, pull_request_id = source.PullRequestId,
                }).Distinct());
            jobs = db.ReviewJobs.FromSqlInterpolated(
                $"""
                 SELECT j.* FROM review_jobs AS j
                 JOIN jsonb_to_recordset({json}::jsonb) AS s(client_id uuid, repository_id text, pull_request_id bigint)
                   ON j.client_id=s.client_id AND j.repository_id=s.repository_id AND j.pull_request_id=s.pull_request_id
                 """);
        }
        else
        {
            var clients = requested.Select(source => source.ClientId).Distinct().ToArray();
            var repositories = requested.Select(source => source.RepositoryId).Distinct().ToArray();
            var pullRequests = requested.Select(source => source.PullRequestId).Distinct().ToArray();
            jobs = db.ReviewJobs.Where(job =>
                clients.Contains(job.ClientId) && repositories.Contains(job.RepositoryId) && pullRequests.Contains(job.PullRequestId));
        }

        var metadata = await jobs.AsNoTracking().Where(job => job.SubmittedAt <= capturedAt)
            .OrderBy(job => job.Id).Select(job => new
            {
                job.Id, job.ClientId, job.RepositoryId, job.PullRequestId, job.Provider, job.OrganizationUrl, job.HostBaseUrl,
                job.IterationId, job.RevisionHeadSha, job.RevisionBaseSha, job.RevisionStartSha, job.ProviderRevisionId, job.ReviewPatchIdentity,
            }).Take(MaximumJobs + 1).ToListAsync(ct);
        if (metadata.Count > MaximumJobs)
        {
            return Unavailable();
        }

        var eligible = metadata.Select(job => new
        {
            Job = job,
            Scope = ProviderSourceIdentity.FromReviewSource(
                job.Provider,
                this._policies[job.Provider].SelectCapturedSource(job.OrganizationUrl, job.HostBaseUrl), this._policies[job.Provider]).Value,
        }).Where(item => requestedKeys.Contains((item.Job.ClientId, item.Job.RepositoryId, (long)item.Job.PullRequestId, item.Scope))).ToArray();
        var ids = eligible.Select(item => item.Job.Id).ToArray();
        if (ids.Length == 0)
        {
            return new([], true);
        }

        long bytes = 0;
        if (db.Database.IsNpgsql())
        {
            bytes = await db.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(octet_length(model_evidence_manifest_json::text)),0)::bigint AS "Value"
                 FROM review_jobs WHERE id=ANY({ids})
                 """).SingleAsync(ct);
            bytes += await db.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(octet_length((model_evidence_json-'PendingFragments'-'RetainedTransportDigests')::text)),0)::bigint AS "Value"
                 FROM review_file_results WHERE job_id=ANY({ids})
                 """).SingleAsync(ct);
            if (bytes > MaximumBytes)
            {
                return Unavailable();
            }
        }

        var manifests = await db.ReviewJobs.AsNoTracking().Where(job => ids.Contains(job.Id))
            .Select(job => new { job.Id, job.ModelEvidenceManifest }).ToDictionaryAsync(job => job.Id, job => job.ModelEvidenceManifest, ct);
        if (!db.Database.IsNpgsql())
        {
            bytes = manifests.Values.Sum(value => (long)JsonSerializer.SerializeToUtf8Bytes(value).Length);
        }

        var retained = new List<(Guid JobId, string Path, ModelReviewEvidence Proof)>();
        if (db.Database.IsNpgsql())
        {
            var rows = await db.Database.SqlQuery<FilePayload>(
                $"""
                 SELECT job_id AS "JobId", file_path AS "FilePath",
                   (model_evidence_json-'PendingFragments'-'RetainedTransportDigests')::text AS "EvidenceJson"
                 FROM review_file_results WHERE job_id=ANY({ids}) AND model_evidence_json IS NOT NULL
                 ORDER BY job_id,id LIMIT {MaximumFiles + 1}
                 """).ToListAsync(ct);
            if (rows.Count > MaximumFiles)
            {
                return Unavailable();
            }

            foreach (var row in rows)
            {
                if (ModelEvidenceJson.ReadEvidence(row.EvidenceJson) is { } proof)
                {
                    retained.Add((row.JobId, row.FilePath, proof));
                }
            }
        }
        else
        {
            await foreach (var row in db.ReviewFileResults.AsNoTracking().Where(row => ids.Contains(row.JobId) && row.ModelEvidence != null).AsAsyncEnumerable()
                               .WithCancellation(ct))
            {
                var proof = row.ModelEvidence! with { PendingFragments = [], RetainedTransportDigests = [] };
                bytes += JsonSerializer.SerializeToUtf8Bytes(proof).Length;
                if (bytes > MaximumBytes || retained.Count >= MaximumFiles)
                {
                    return Unavailable();
                }

                retained.Add((row.JobId, row.FilePath, proof));
            }
        }

        var byJob = retained.ToLookup(row => row.JobId);
        var result = new List<ModelReviewExecution>(eligible.Length);
        foreach (var item in eligible)
        {
            var job = item.Job;
            var manifest = manifests.GetValueOrDefault(job.Id);
            if (manifest?.JobId != job.Id || manifest?.ProviderScope != item.Scope)
            {
                manifest = null;
            }

            var files = byJob[job.Id].Where(row => row.Proof.JobId == job.Id && row.Proof.FilePath == row.Path
                                                                             && row.Proof.ClientId == job.ClientId &&
                                                                             row.Proof.RepositoryId == job.RepositoryId &&
                                                                             row.Proof.PullRequestId == job.PullRequestId
                                                                             && row.Proof.ProviderScope == item.Scope).Select(row => row.Proof).ToArray();
            var connections = files.Select(file => file.SourceConnectionId).Concat(manifest is null ? [] : new[] { manifest.SourceConnectionId }).Distinct();
            foreach (var connection in connections)
            {
                var source = new ModelReviewSource(job.ClientId, job.RepositoryId, job.PullRequestId, item.Scope, connection);
                if (!requestedKeys[(source.ClientId, source.RepositoryId, source.PullRequestId, source.ProviderScope)]
                        .Any(wanted => wanted.ConnectionId is null || wanted.ConnectionId == source.ConnectionId))
                {
                    continue;
                }

                var revision = string.IsNullOrWhiteSpace(job.RevisionHeadSha) || string.IsNullOrWhiteSpace(job.RevisionBaseSha)
                    ? null
                    : new ReviewRevision(job.RevisionHeadSha, job.RevisionBaseSha, job.RevisionStartSha, job.ProviderRevisionId, job.ReviewPatchIdentity);
                result.Add(
                    new(
                        job.Id, source, ReviewRevisionKeys.GetStoredKey(revision, job.IterationId), job.RevisionHeadSha, job.RevisionBaseSha,
                        manifest?.SourceConnectionId == connection ? manifest : null, files.Where(file => file.SourceConnectionId == connection).ToArray()));
            }
        }

        return new(result, true);
    }

    private static ModelReviewEvidenceSnapshot Unavailable() => new([], false, "model-proof-read-limit-exceeded");

    private sealed record FilePayload(Guid JobId, string FilePath, string EvidenceJson);
}
