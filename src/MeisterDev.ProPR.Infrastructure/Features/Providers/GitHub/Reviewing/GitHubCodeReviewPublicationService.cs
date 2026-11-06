// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed partial class GitHubCodeReviewPublicationService : ICodeReviewPublicationService
{
    private readonly GitHubLifecyclePublicationService _lifecyclePublicationService;
    private readonly IMergeGuardService? _mergeGuardService;
    private readonly ILogger<GitHubCodeReviewPublicationService> _logger;

    public GitHubCodeReviewPublicationService(
        GitHubConnectionVerifier connectionVerifier,
        IHttpClientFactory httpClientFactory,
        IPostedCommentComposer composer,
        IMergeGuardService? mergeGuardService = null,
        ILogger<GitHubCodeReviewPublicationService>? logger = null)
        : this(new GitHubLifecyclePublicationService(connectionVerifier, httpClientFactory, composer), mergeGuardService, logger)
    {
    }

    internal GitHubCodeReviewPublicationService(
        GitHubLifecyclePublicationService lifecyclePublicationService,
        IMergeGuardService? mergeGuardService = null,
        ILogger<GitHubCodeReviewPublicationService>? logger = null)
    {
        this._lifecyclePublicationService = lifecyclePublicationService;
        this._mergeGuardService = mergeGuardService;
        this._logger = logger ?? NullLogger<GitHubCodeReviewPublicationService>.Instance;
    }

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct = default,
        ReviewPublicationContext? publicationContext = null)
    {
        var diagnostics = await this._lifecyclePublicationService.PublishReviewAsync(
            clientId,
            review,
            revision,
            result,
            reviewer,
            ct,
            publicationContext);

        // The review is published; the guard reports on it best-effort afterwards. A
        // guard failure never unwinds a published review.
        await this.ReportMergeGuardAsync(clientId, review, revision, result, ct);

        return diagnostics;
    }

    private async Task ReportMergeGuardAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        CancellationToken ct)
    {
        if (this._mergeGuardService is null)
        {
            return;
        }

        try
        {
            var report = await this._mergeGuardService.EvaluateAndReportAsync(clientId, review, revision, result, ct);
            if (report is null)
            {
                // Guard disabled for this client: nothing to report.
                return;
            }

            if (!report.Posted)
            {
                LogMergeGuardNotPosted(this._logger, review.Number, report.Decision.ToString(), report.PostError ?? report.Reason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMergeGuardFailed(this._logger, review.Number, ex);
        }
    }

    [LoggerMessage(
        EventId = 4786,
        Level = LogLevel.Warning,
        Message = "Merge guard decided {Decision} for pull request {ReviewNumber} but the check run was not posted. Detail: {Detail}")]
    private static partial void LogMergeGuardNotPosted(ILogger logger, int reviewNumber, string decision, string detail);

    [LoggerMessage(
        EventId = 4787,
        Level = LogLevel.Warning,
        Message = "Merge-guard evaluation failed after publishing pull request {ReviewNumber}; the review stands.")]
    private static partial void LogMergeGuardFailed(ILogger logger, int reviewNumber, Exception exception);
}
