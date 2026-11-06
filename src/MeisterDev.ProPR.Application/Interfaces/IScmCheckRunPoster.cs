// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Posts provider check runs on a pull request head. Implemented per provider family
///     (GitHub Checks API, ...); callers stay provider-neutral and pass the guard decision,
///     the adapter maps it onto the provider's conclusion values.
/// </summary>
public interface IScmCheckRunPoster
{
    /// <summary>
    ///     Creates a check run on <paramref name="headSha" />, or updates the existing one
    ///     when <paramref name="existingCheckRunId" /> names a check run for the same head.
    ///     Throws <see cref="InvalidOperationException" /> with a user-safe message when the
    ///     provider refuses the write (missing permission, unknown head, ...). The provider
    ///     credential needs check-write permission (GitHub: <c>checks:write</c> for an App
    ///     installation, <c>repo</c> scope for a classic personal access token).
    /// </summary>
    Task<CheckRunPostResult> PostOrUpdateCheckRunAsync(
        Guid clientId,
        ProviderHostRef host,
        string owner,
        string repository,
        string headSha,
        string name,
        MergeGuardDecision decision,
        string title,
        string summary,
        string? detailsUrl,
        long? existingCheckRunId,
        CancellationToken ct = default);
}

/// <summary>The provider's record of a posted check run.</summary>
public sealed record CheckRunPostResult(long CheckRunId, string? HtmlUrl);
