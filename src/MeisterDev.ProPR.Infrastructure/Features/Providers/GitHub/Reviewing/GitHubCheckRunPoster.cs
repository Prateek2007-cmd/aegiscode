// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     Posts AegisCode check runs through the GitHub Checks API, reusing the same
///     connection verification and HTTP pipeline as review publication — no new auth,
///     no new secrets. A create (POST) opens a fresh check run on the head SHA; an
///     update (PATCH) refreshes the existing check run for that head instead of opening
///     a second one. The provider credential needs check-write permission (GitHub App:
///     <c>checks:write</c>; classic personal access token: <c>repo</c> scope); a
///     refusal surfaces as an <see cref="InvalidOperationException" /> with the
///     provider's status, never as a silently dropped check.
/// </summary>
internal sealed partial class GitHubCheckRunPoster(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubCheckRunPoster>? logger = null) : IScmCheckRunPoster
{
    private readonly ILogger<GitHubCheckRunPoster> _logger = logger ?? NullLogger<GitHubCheckRunPoster>.Instance;

    /// <inheritdoc />
    public async Task<CheckRunPostResult> PostOrUpdateCheckRunAsync(
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
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(summary);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var repositoryPath = $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        var conclusion = MapConclusion(decision);

        using var request = existingCheckRunId is { } checkRunId
            ? new HttpRequestMessage(
                HttpMethod.Patch,
                GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/check-runs/{checkRunId.ToString(CultureInfo.InvariantCulture)}"))
            : new HttpRequestMessage(
                HttpMethod.Post,
                GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/check-runs"));
        request.Content = JsonContent.Create(
            existingCheckRunId is null
                ? new GitHubCheckRunRequest(name, headSha, conclusion, title, summary, detailsUrl)
                : new GitHubCheckRunRequest(null, null, conclusion, title, summary, detailsUrl));
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var responseBody = await ReadDiagnosticBodyAsync(response, ct);
            LogCheckRunPermissionFailure(this._logger, repositoryPath, (int)response.StatusCode);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(responseBody)
                    ? "GitHub check-run posting failed because the configured credential no longer has permission to write checks (checks:write / repo scope required)."
                    : $"GitHub check-run posting failed because the configured credential no longer has permission to write checks (checks:write / repo scope required). {responseBody}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await ReadDiagnosticBodyAsync(response, ct);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(responseBody)
                    ? $"GitHub check-run posting failed with status {(int)response.StatusCode}."
                    : $"GitHub check-run posting failed with status {(int)response.StatusCode}: {responseBody}");
        }

        var created = await response.Content.ReadFromJsonAsync<GitHubCheckRunResponse>(ct);
        if (created?.Id is null)
        {
            throw new InvalidOperationException("GitHub accepted the check run but returned no check-run id.");
        }

        return new CheckRunPostResult(created.Id.Value, created.HtmlUrl);
    }

    private static string MapConclusion(MergeGuardDecision decision)
    {
        return decision switch
        {
            MergeGuardDecision.Success => "success",
            MergeGuardDecision.Failure => "failure",
            _ => "neutral",
        };
    }

    private static async Task<string?> ReadDiagnosticBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var normalized = body.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000].TrimEnd() + "...";
    }

    [LoggerMessage(
        EventId = 4785,
        Level = LogLevel.Warning,
        Message = "GitHub check-run posting permission failure for repository {RepositoryPath} with status {StatusCode}.")]
    private static partial void LogCheckRunPermissionFailure(ILogger logger, string repositoryPath, int statusCode);

    private sealed record GitHubCheckRunRequest(
        [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name,
        [property: JsonPropertyName("head_sha"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HeadSha,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("conclusion")]
        string Conclusion,
        [property: JsonPropertyName("output")] GitHubCheckRunOutput Output,
        [property: JsonPropertyName("details_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DetailsUrl)
    {
        public GitHubCheckRunRequest(
                string? name,
                string? headSha,
                string conclusion,
                string title,
                string summary,
                string? detailsUrl)
            // A conclusion is only accepted with status completed; without it GitHub
            // answers 422 and the check run stays queued without a verdict.
            : this(name, headSha, "completed", conclusion, new GitHubCheckRunOutput(title, summary), detailsUrl)
        {
        }
    }

    private sealed record GitHubCheckRunOutput(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("summary")]
        string Summary);

    private sealed record GitHubCheckRunResponse(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl);
}
