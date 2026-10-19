// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>A source pull request requested by an authorized collection or analytics owner.</summary>
public sealed record ModelReviewSource(Guid ClientId, string RepositoryId, long PullRequestId, string ProviderScope, Guid? ConnectionId = null);

/// <summary>Durable generation evidence, without public review results or live provider access.</summary>
public sealed record ModelReviewExecution(
    Guid JobId,
    ModelReviewSource Source,
    string RevisionKey,
    string? HeadSha,
    string? BaseSha,
    ModelReviewManifest? Manifest,
    IReadOnlyList<ModelReviewEvidence> Files);

/// <summary>A bounded retained execution snapshot. An incomplete read cannot certify non-detection.</summary>
public sealed record ModelReviewEvidenceSnapshot(IReadOnlyList<ModelReviewExecution> Executions, bool Complete, string? UnavailableReason = null);

public interface IModelReviewEvidenceReader
{
    Task<ModelReviewEvidenceSnapshot> ReadAsync(IReadOnlyList<ModelReviewSource> sources, DateTimeOffset capturedAt, CancellationToken ct = default);
}
