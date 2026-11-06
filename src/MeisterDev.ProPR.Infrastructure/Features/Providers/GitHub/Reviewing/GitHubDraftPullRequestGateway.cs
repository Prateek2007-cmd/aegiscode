// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     GitHub adapter for <see cref="IDraftPullRequestGateway" />: branch creation, file
///     update, and draft-PR creation through the GitHub REST API, authenticated with the
///     client's own saved connection (personal access token or GitHub App installation —
///     the same token plumbing the workspace resolver uses). Every step reports the
///     provider's own error text; a pull request URL is only returned when GitHub
///     actually created one.
/// </summary>
internal sealed class GitHubDraftPullRequestGateway(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IDraftPullRequestGateway
{
    /// <inheritdoc />
    public ScmProvider Provider => ScmProvider.GitHub;

    /// <inheritdoc />
    public async Task<string?> GetDefaultBranchAsync(
        Guid clientId,
        ProviderHostRef host,
        string owner,
        string repository,
        CancellationToken ct)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{owner}/{repository}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub repository lookup failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub repository lookup returned an empty payload.");
        return string.IsNullOrWhiteSpace(payload.DefaultBranch) ? null : payload.DefaultBranch.Trim();
    }

    /// <inheritdoc />
    public async Task<DraftPullRequestFileContent?> GetFileContentAsync(
        Guid clientId,
        ProviderHostRef host,
        string owner,
        string repository,
        string filePath,
        string @ref,
        CancellationToken ct)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{owner}/{repository}/contents/{Uri.EscapeDataString(filePath)}",
                $"ref={Uri.EscapeDataString(@ref)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub file lookup failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub file lookup returned an empty payload.");

        if (!string.Equals(payload.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"GitHub path '{filePath}' is not a file.");
        }

        if (string.IsNullOrWhiteSpace(payload.Sha))
        {
            throw new InvalidOperationException($"GitHub file lookup for '{filePath}' did not return a blob SHA.");
        }

        if (string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.Content))
        {
            var raw = payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal);
            return new DraftPullRequestFileContent(Encoding.UTF8.GetString(Convert.FromBase64String(raw)), payload.Sha.Trim());
        }

        // The contents API omits the content for files over 1 MB (and for binary files):
        // there is nothing verifiable to read, so fail here rather than guessing.
        throw new InvalidOperationException($"GitHub file '{filePath}' could not be read as text (too large, binary, or otherwise unreadable).");
    }

    /// <inheritdoc />
    public async Task<DraftPullRequestResult> CreateDraftPullRequestAsync(
        Guid clientId,
        ProviderHostRef host,
        DraftPullRequestInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = $"{input.Owner}/{input.Repository}";

        var baseSha = input.BaseSha ?? await this.GetBranchHeadShaAsync(context, host, repositoryPath, input.BaseBranch, ct);

        // The requested branch name can already exist (a repeat attempt within the same
        // second, or a name the user created by hand); retry with a numeric suffix
        // instead of failing the whole flow on a name collision.
        var branch = input.BranchName;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var created = await this.TryCreateRefAsync(context, host, repositoryPath, branch, baseSha, ct);
            if (created)
            {
                break;
            }

            if (attempt == 2)
            {
                throw new InvalidOperationException($"GitHub branch '{input.BranchName}' already exists and the fallback names are taken.");
            }

            branch = $"{input.BranchName}-{attempt + 1}";
        }

        await this.PutFileContentAsync(context, host, repositoryPath, input, branch, ct);

        using var prRequest = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/pulls"),
            HttpMethod.Post,
            ct);
        prRequest.Content = JsonContent.Create(
            new
            {
                title = input.Title,
                head = branch,
                @base = input.BaseBranch,
                body = input.Body,
                draft = true,
            });
        using var prResponse = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(prRequest, ct);
        if (!prResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub draft pull request creation failed", prResponse, ct));
        }

        var prPayload = await prResponse.Content.ReadFromJsonAsync<GitHubPullResponse>(ct)
                        ?? throw new InvalidOperationException("GitHub pull request creation returned an empty payload.");
        if (string.IsNullOrWhiteSpace(prPayload.HtmlUrl))
        {
            throw new InvalidOperationException("GitHub pull request creation did not return a pull request URL.");
        }

        return new DraftPullRequestResult(branch, prPayload.HtmlUrl.Trim(), input.BaseBranch);
    }

    private async Task<string> GetBranchHeadShaAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string branch,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/git/ref/heads/{Uri.EscapeDataString(branch)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"GitHub branch '{branch}' was not found in '{repositoryPath}'.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub branch lookup failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubRefResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub branch lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(payload.Object?.Sha))
        {
            throw new InvalidOperationException($"GitHub branch '{branch}' did not return a commit SHA.");
        }

        return payload.Object.Sha.Trim();
    }

    /// <summary>Creates the branch ref. Returns <c>false</c> only when GitHub says the ref already exists (422).</summary>
    private async Task<bool> TryCreateRefAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string branch,
        string baseSha,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/git/refs"),
            HttpMethod.Post,
            ct);
        request.Content = JsonContent.Create(
            new
            {
                @ref = $"refs/heads/{branch}",
                sha = baseSha,
            });
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == (HttpStatusCode)422)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub branch creation failed", response, ct));
        }

        return true;
    }

    private async Task PutFileContentAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        DraftPullRequestInput input,
        string branch,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/contents/{Uri.EscapeDataString(input.FilePath)}"),
            HttpMethod.Put,
            ct);
        request.Content = JsonContent.Create(
            new
            {
                message = $"AegisCode fix: {input.Title}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(input.NewContent)),
                sha = input.BlobSha,
                branch,
            });
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // A 409 here means the blob SHA no longer matches: the file changed after
            // the gate verified it, and the update was refused rather than overwriting.
            throw new InvalidOperationException(await BuildStatusMessageAsync("GitHub file update failed", response, ct));
        }
    }

    private static async Task<string> BuildStatusMessageAsync(string prefix, HttpResponseMessage response, CancellationToken ct)
    {
        var detail = await ReadResponseMessageAsync(response, ct);
        return string.IsNullOrWhiteSpace(detail)
            ? $"{prefix} with status {(int)response.StatusCode}."
            : $"{prefix} with status {(int)response.StatusCode}: {detail}";
    }

    private static async Task<string?> ReadResponseMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON body: fall through and surface the raw response text instead.
        }

        var normalized = body.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512].TrimEnd() + "...";
    }

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("default_branch")]
        string? DefaultBranch);

    private sealed record GitHubContentResponse(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("encoding")]
        string? Encoding,
        [property: JsonPropertyName("content")]
        string? Content,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record GitHubRefResponse([property: JsonPropertyName("object")] GitHubRefObjectResponse? Object);

    private sealed record GitHubRefObjectResponse([property: JsonPropertyName("sha")] string? Sha);

    private sealed record GitHubPullResponse(
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl);
}
