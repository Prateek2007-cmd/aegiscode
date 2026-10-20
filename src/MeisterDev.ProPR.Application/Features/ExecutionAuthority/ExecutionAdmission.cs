// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     Asks the registered execution authority whether a review, a thread pass or a mention reply may start. Without
///     a registered authority every execution is allowed, and nothing is looked up or stored.
/// </summary>
/// <remarks>
///     <para>
///         The dependencies are optional, so a composition without a database or without client configuration can
///         still resolve this service. They are required once an authority is registered.
///     </para>
///     <para>
///     The facts in a request come from the job and from the client's configuration. The pull request author comes
///     from the review job when an earlier attempt recorded it; otherwise one lightweight review query asks the
///     provider for it. A review job keeps the author it learned this way, so a held job does not query the
///     provider again each time it is asked about.
///     </para>
/// </remarks>
public sealed partial class ExecutionAdmission(
    ILogger<ExecutionAdmission> logger,
    IClientRegistry? clients = null,
    IScmProviderRegistry? providers = null,
    IAiConnectionRepository? connections = null,
    TimeProvider? timeProvider = null,
    IExecutionAuthority? authority = null) : IExecutionAdmission
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The shortest hold ProPR applies, so a held execution is not asked about on every worker cycle.</summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(30);

    /// <summary>The longest hold ProPR applies, so a held execution is asked about again within the hour.</summary>
    public static readonly TimeSpan MaximumHold = TimeSpan.FromHours(1);

    /// <summary>The hold ProPR applies when the authority gives no delay.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMinutes(5);

    /// <summary>The reason recorded when the authority could not be asked.</summary>
    public const string AuthorityUnavailableReason = "execution_authority_unavailable";

    /// <summary>The reason recorded when the client the execution belongs to no longer exists.</summary>
    public const string OwnerUnknownReason = "execution_owner_unknown";

    /// <inheritdoc />
    public bool IsActive => authority is not null;

    /// <inheritdoc />
    public async Task<ExecutionAdmissionResult> AdmitReviewAsync(
        ReviewJob job,
        IReviewJobExecutionStore jobs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(jobs);
        if (authority is null)
        {
            return ExecutionAdmissionResult.Allowed;
        }

        var author = job.PullRequestAuthorReference;
        if (author is null)
        {
            author = await this.LookUpAuthorAsync(job.Provider, job.ClientId, job.CodeReviewReference, ct);
            if (author is not null)
            {
                await jobs.UpdatePullRequestAuthorAsync(job.Id, author, ct);
            }
        }

        return await this.AdmitAsync(
            new ExecutionIdentity(ExecutionKind.Review, job.Id, job.RetryCount + 1),
            job.ClientId,
            Location(
                job.Provider, job.ProviderHost, job.OrganizationUrl, job.ProjectId, job.RepositoryOwnerOrNamespace, job.RepositoryId, job.RepositoryProjectPath,
                job.PullRequestId),
            author,
            ct);
    }

    /// <inheritdoc />
    public async Task<ExecutionAdmissionResult> AdmitThreadPassAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (authority is null)
        {
            return ExecutionAdmissionResult.Allowed;
        }

        var author = await this.LookUpAuthorAsync(job.Provider, job.ClientId, job.CodeReviewReference, ct);

        // The attempt about to start: beginning it is what counts it.
        return await this.AdmitAsync(
            new ExecutionIdentity(ExecutionKind.ThreadPass, job.Id, job.AttemptCount + 1),
            job.ClientId,
            Location(
                job.Provider, job.ProviderHost, job.OrganizationUrl, job.ProjectId, job.RepositoryOwnerOrNamespace, job.RepositoryId, job.RepositoryProjectPath,
                job.PullRequestId),
            author,
            ct);
    }

    /// <inheritdoc />
    public async Task<ExecutionAdmissionResult> AdmitMentionReplyAsync(MentionReplyJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (authority is null)
        {
            return ExecutionAdmissionResult.Allowed;
        }

        var author = await this.LookUpAuthorAsync(job.Provider, job.ClientId, job.CodeReviewReference, ct);

        // A mention reply runs once; a reply that fails is not retried under the same identifier.
        return await this.AdmitAsync(
            new ExecutionIdentity(ExecutionKind.MentionReply, job.Id, 1),
            job.ClientId,
            Location(
                job.Provider, job.ProviderHost, job.OrganizationUrl, job.ProjectId, job.RepositoryOwnerOrNamespace, job.RepositoryId, job.RepositoryProjectPath,
                job.PullRequestId),
            author,
            ct);
    }

    private async Task<ExecutionAdmissionResult> AdmitAsync(
        ExecutionIdentity execution,
        Guid clientId,
        ScmLocation location,
        PullRequestAuthor? author,
        CancellationToken ct)
    {
        // The request carries the time ProPR asked. A hold counts from the time the answer or the failure arrived,
        // so a slow lookup or a slow authority cannot use up the delay the hold promises.
        var requestedAt = this._clock.GetUtcNow();
        AdmissionDecision decision;
        try
        {
            var tenantId = await Required(clients).GetTenantIdAsync(clientId, ct);
            if (tenantId is not { } tenant)
            {
                return ExecutionAdmissionResult.HoldUntil(
                    this._clock.GetUtcNow() + MinimumHold,
                    OwnerUnknownReason,
                    "The client of this execution could not be found, so the execution authority was not asked.");
            }

            var request = new AdmissionRequest(
                execution,
                tenant,
                clientId,
                location,
                author is null ? null : new ScmAuthor(author.ExternalUserId, author.Login, author.DisplayName, author.IsBot),
                await this.ConnectionRevisionsAsync(tenant, clientId, ct),
                requestedAt);
            decision = await authority!.AdmitAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // An authority that cannot answer does not admit work: the execution waits and is asked about again.
            LogAuthorityUnavailable(logger, execution.Kind, execution.Id, ex);
            return ExecutionAdmissionResult.HoldUntil(
                this._clock.GetUtcNow() + MinimumHold,
                AuthorityUnavailableReason,
                "The execution authority did not answer, so ProPR asks again shortly.");
        }

        var answeredAt = this._clock.GetUtcNow();
        return decision.Outcome switch
        {
            AdmissionOutcome.Allow => ExecutionAdmissionResult.Allowed,
            AdmissionOutcome.Refuse => ExecutionAdmissionResult.Refused(
                Text(decision.Reason, "execution_refused"),
                Text(decision.Message, "The execution authority refused this execution.")),
            _ => ExecutionAdmissionResult.HoldUntil(
                answeredAt + Clamp(decision.RetryAfter ?? DefaultHold),
                Text(decision.Reason, "execution_held"),
                Text(decision.Message, "The execution authority holds this execution.")),
        };
    }

    private async Task<IReadOnlyList<AiConnectionRevision>> ConnectionRevisionsAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var own = await Required(connections).GetByClientAsync(clientId, ct);
        var inherited = await Required(connections).GetByTenantAsync(tenantId, ct);
        return own.Concat(inherited)
            .Select(connection => new AiConnectionRevision(connection.Id, connection.ConfigurationRevision))
            .DistinctBy(revision => revision.ConnectionId)
            .ToList();
    }

    private async Task<PullRequestAuthor?> LookUpAuthorAsync(
        ScmProvider provider,
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct)
    {
        try
        {
            var item = await Required(providers).GetCodeReviewQueryService(provider).GetReviewAsync(clientId, review, ct);
            return item?.Creator;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The authority is asked without the author; it decides whether that is enough to admit the work.
            LogAuthorLookupFailed(logger, review.Number, ex);
            return null;
        }
    }

    private static ScmLocation Location(
        ScmProvider provider,
        ProviderHostRef host,
        string organizationUrl,
        string projectId,
        string? ownerOrNamespace,
        string repositoryId,
        string? repositoryPath,
        int pullRequestNumber)
    {
        return new ScmLocation(
            provider switch
            {
                ScmProvider.AzureDevOps => ScmProviderKind.AzureDevOps,
                ScmProvider.GitHub => ScmProviderKind.GitHub,
                ScmProvider.GitLab => ScmProviderKind.GitLab,
                ScmProvider.Forgejo => ScmProviderKind.Forgejo,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider family has no contract value."),
            },
            host.HostBaseUrl,
            organizationUrl,
            projectId,
            ownerOrNamespace,
            repositoryId,
            repositoryPath,
            pullRequestNumber);
    }

    private static T Required<T>(T? service)
        where T : class
    {
        return service ?? throw new InvalidOperationException(
            $"An execution authority is registered, but no {typeof(T).Name} is registered, which admission needs.");
    }

    private static TimeSpan Clamp(TimeSpan requested)
    {
        if (requested < MinimumHold)
        {
            return MinimumHold;
        }

        return requested > MaximumHold ? MaximumHold : requested;
    }

    private static string Text(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The execution authority did not answer for {Kind} {ExecutionId}; the execution is held")]
    private static partial void LogAuthorityUnavailable(ILogger logger, ExecutionKind kind, Guid executionId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The author of pull request {PullRequestNumber} could not be read; the execution authority is asked without it")]
    private static partial void LogAuthorLookupFailed(ILogger logger, int pullRequestNumber, Exception exception);
}
