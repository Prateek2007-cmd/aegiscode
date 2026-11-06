// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Creates draft pull requests on the SCM provider from an approved fix proposal.
///     The adapter owns every provider call: resolving the client's connection, reading
///     file content, creating the branch, committing the patched file, and opening the
///     draft PR. It never invents content — the caller supplies the exact replacement,
///     already verified against the head file by the suggested-code gate — and it
///     reports the provider's own error text instead of a generic failure.
/// </summary>
public interface IDraftPullRequestGateway
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>
    ///     Returns the repository's default branch, or <c>null</c> when the repository
    ///     cannot be read (not found, or not accessible to the connection).
    /// </summary>
    Task<string?> GetDefaultBranchAsync(
        Guid clientId,
        ProviderHostRef host,
        string owner,
        string repository,
        CancellationToken ct);

    /// <summary>
    ///     Reads one file at a ref (branch name or commit SHA). Returns <c>null</c> when the
    ///     file does not exist at that ref. Throws <see cref="InvalidOperationException" />
    ///     with the provider's message when the file cannot be read as text (too large,
    ///     binary, or a directory) or any other provider call fails.
    /// </summary>
    Task<DraftPullRequestFileContent?> GetFileContentAsync(
        Guid clientId,
        ProviderHostRef host,
        string owner,
        string repository,
        string filePath,
        string @ref,
        CancellationToken ct);

    /// <summary>
    ///     Creates the branch, commits the patched file on it, and opens a draft pull
    ///     request against <see cref="DraftPullRequestInput.BaseBranch" />. Throws
    ///     <see cref="InvalidOperationException" /> with the provider's own message when
    ///     any step fails; no pull request is reported unless GitHub returned one.
    /// </summary>
    Task<DraftPullRequestResult> CreateDraftPullRequestAsync(
        Guid clientId,
        ProviderHostRef host,
        DraftPullRequestInput input,
        CancellationToken ct);
}

/// <summary>
///     One file as read from the provider: its decoded text content and the blob SHA a
///     later update must carry, so a concurrent change fails instead of overwriting.
/// </summary>
public sealed record DraftPullRequestFileContent(string Content, string Sha);

/// <summary>
///     Everything the provider needs to open the draft PR. <see cref="BaseSha" /> pins
///     the branch point to one commit; when <c>null</c> the branch starts at the head of
///     <see cref="BaseBranch" />. <see cref="BlobSha" /> is the blob SHA read with the
///     file content the replacement was verified against.
/// </summary>
public sealed record DraftPullRequestInput(
    string Owner,
    string Repository,
    string BaseBranch,
    string? BaseSha,
    string BranchName,
    string FilePath,
    string BlobSha,
    string NewContent,
    string Title,
    string Body);

/// <summary>
///     The opened draft PR: the branch actually created (a numeric suffix is added
///     when the requested name is already taken) and the pull request URL.
/// </summary>
public sealed record DraftPullRequestResult(string Branch, string PullRequestUrl, string BaseBranch);
