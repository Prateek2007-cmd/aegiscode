// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewWorkspaceRemoteResolver(GitHubConnectionVerifier connectionVerifier) : IProviderReviewWorkspaceRemoteResolver
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await connectionVerifier.VerifyAsync(request.ClientId, request.Repository.Host, ct);
        var repositoryPath = BuildRepositoryPath(request.Repository);
        var remoteUrl = $"{request.Repository.Host.HostBaseUrl.TrimEnd('/')}/{repositoryPath}.git";
        var accessToken = await context.GetAccessTokenAsync(ct);

        return new ReviewWorkspaceRemoteRef(
            ScmProvider.GitHub,
            remoteUrl,
            BuildFetchRefSpecs(request.PullRequestNumber, request.SourceBranch, request.TargetBranch),
            $"github:{request.Repository.Host.HostBaseUrl}:{repositoryPath}",
            $"github:{context.Connection.Id:D}:{context.Connection.AuthenticationKind}",
            true,
            request.Repository.OwnerOrNamespace,
            $"AUTHORIZATION: Basic {BuildBasicCredentials(accessToken)}");
    }

    // GitHub's git-over-HTTP endpoint rejects Bearer credentials for personal access
    // tokens ("remote: invalid credentials") even though the same token authenticates
    // REST calls; basic credentials with the token as the password are what git
    // accepts. The username is ignored as long as the password carries the token, so
    // use GitHub's x-access-token convention. This matches the GitLab resolver, whose
    // git endpoint has the same basic-only requirement.
    private static string BuildBasicCredentials(string token)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
    }

    private static IReadOnlyList<string> BuildFetchRefSpecs(int pullRequestNumber, string sourceBranch, string targetBranch)
    {
        var refSpecs = new List<string>
        {
            $"+refs/pull/{pullRequestNumber}/head:refs/remotes/review/pr/{pullRequestNumber}/head",
        };

        AddBranchRef(refSpecs, sourceBranch);
        AddBranchRef(refSpecs, targetBranch);
        return refSpecs.AsReadOnly();
    }

    private static void AddBranchRef(ICollection<string> refSpecs, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        var normalized = branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch.Trim();
        refSpecs.Add($"+refs/heads/{normalized}:refs/remotes/origin/{normalized}");
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.ProjectPath) && repository.ProjectPath.Contains('/'))
        {
            return repository.ProjectPath.Trim();
        }

        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{repository.OwnerOrNamespace}/{repositoryName}";
    }
}
