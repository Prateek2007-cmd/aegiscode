// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Extensibility.Execution;

/// <summary>The facts ProPR knows about an execution when it asks whether the execution may start.</summary>
/// <param name="Execution">The execution and its attempt.</param>
/// <param name="TenantId">The tenant the work belongs to.</param>
/// <param name="ClientId">The client the work belongs to.</param>
/// <param name="Location">The pull request the work concerns.</param>
/// <param name="Author">
///     The account that opened the pull request, or null when ProPR could not determine it, for example because
///     the provider did not answer.
/// </param>
/// <param name="AiConnections">
///     The active AI connections the client can use, its own and those it inherits from its tenant, each with its
///     current configuration revision.
/// </param>
/// <param name="RequestedAt">When ProPR asked.</param>
public sealed record AdmissionRequest(
    ExecutionIdentity Execution,
    Guid TenantId,
    Guid ClientId,
    ScmLocation Location,
    ScmAuthor? Author,
    IReadOnlyList<AiConnectionRevision> AiConnections,
    DateTimeOffset RequestedAt);

/// <summary>The source control provider family of a repository.</summary>
public enum ScmProviderKind
{
    /// <summary>Azure DevOps Services or Azure DevOps Server.</summary>
    AzureDevOps = 0,

    /// <summary>GitHub or GitHub Enterprise Server.</summary>
    GitHub = 1,

    /// <summary>GitLab.</summary>
    GitLab = 2,

    /// <summary>Forgejo or Gitea.</summary>
    Forgejo = 3,
}

/// <summary>A pull request, located the way ProPR records it.</summary>
/// <param name="Provider">The provider family.</param>
/// <param name="HostBaseUrl">The base URL of the provider instance.</param>
/// <param name="OrganizationUrl">The organization or host URL ProPR stores for the repository.</param>
/// <param name="ProjectId">The project, owner or namespace key ProPR stores for the repository.</param>
/// <param name="OwnerOrNamespace">The owner, group or namespace path of the repository, when ProPR knows it.</param>
/// <param name="RepositoryId">The provider's identifier of the repository.</param>
/// <param name="RepositoryPath">The repository path below the host, when ProPR knows it.</param>
/// <param name="PullRequestNumber">The number of the pull request or merge request.</param>
public sealed record ScmLocation(
    ScmProviderKind Provider,
    string HostBaseUrl,
    string OrganizationUrl,
    string ProjectId,
    string? OwnerOrNamespace,
    string RepositoryId,
    string? RepositoryPath,
    int PullRequestNumber);

/// <summary>The account that opened a pull request, as the provider identifies it.</summary>
/// <param name="ExternalUserId">The provider's stable identifier of the account.</param>
/// <param name="Login">The account's login, when the provider reports one.</param>
/// <param name="DisplayName">The account's display name, when the provider reports one.</param>
/// <param name="IsBot">Whether the provider marks the account as a bot, or null when the provider states nothing.</param>
public sealed record ScmAuthor(string ExternalUserId, string? Login, string? DisplayName, bool? IsBot);

/// <summary>An AI connection and the configuration revision it had when ProPR listed or used it.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="Revision">
///     The configuration revision, starting at 1. It increases when an administrator or a client changes the
///     provider, endpoint, authentication or credential of the connection, and stays the same when ProPR renews a
///     credential on its own.
/// </param>
public sealed record AiConnectionRevision(Guid ConnectionId, long Revision);
