// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Reads and writes the guard's configuration and latest checks for one client (dashboard API).</summary>
public interface IMergeGuardManagementService
{
    /// <summary>Returns the client's guard config plus its latest checks, newest first. Never returns another client's state.</summary>
    Task<MergeGuardStatusResponse> GetStatusAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Saves the client's guard configuration. Throws <see cref="ArgumentException" />
    ///     when the threshold is not Critical, High, Medium, or Low.
    /// </summary>
    Task<MergeGuardConfigResponse> UpdateConfigAsync(Guid clientId, bool enabled, string threshold, CancellationToken ct);
}

/// <summary>
///     The merge guard. Evaluates a published review against the latest security scan for
///     the same repository, decides pass/fail/neutral through
///     <see cref="MergeGuardGate" />, and reports the decision as a GitHub check run on
///     the pull request head. Best-effort by contract: evaluation or reporting failures
///     come back as a neutral report with the reason, never as an exception past the
///     review publication that triggered them, and never as a pass.
/// </summary>
public sealed class MergeGuardService(
    ISecurityScanService scanService,
    IScmCheckRunPoster checkRunPoster,
    IMergeGuardStore store,
    IOptions<SecurityScanningOptions> options,
    ILogger<MergeGuardService> logger,
    IOptions<PublicApplicationOptions>? publicApplicationOptions = null) : IMergeGuardService, IMergeGuardManagementService
{
    /// <summary>The check-run name on GitHub. Stable: branch protection matches on this name.</summary>
    public const string CheckRunName = "AegisCode Merge Guard";

    /// <inheritdoc />
    public async Task<MergeGuardReport?> EvaluateAndReportAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            var state = await store.GetStateAsync(clientId, ct);
            if (!state.Config.Enabled)
            {
                return null;
            }

            var threshold = MergeGuardThreshold.Normalize(state.Config.Threshold);
            if (threshold is null)
            {
                return await this.ReportNeutralAsync(
                    clientId,
                    review,
                    revision,
                    "The stored guard threshold is invalid; fix it on the client dashboard.",
                    null,
                    null,
                    ct);
            }

            var (owner, repository) = BuildRepositoryCoordinates(review.Repository);
            string repositoryUrl;
            try
            {
                var host = new Uri(review.Repository.Host.HostBaseUrl).Host;
                repositoryUrl = SecurityScanUrlValidator.ValidateAndNormalize(
                    $"https://{host}/{owner}/{repository}",
                    options.Value.AllowedHosts);
            }
            catch (ArgumentException ex)
            {
                return await this.ReportNeutralAsync(
                    clientId,
                    review,
                    revision,
                    $"The repository URL for this pull request is not supported by the merge guard: {ex.Message}",
                    null,
                    null,
                    ct);
            }

            var scan = await scanService.GetLatestScanForRepositoryAsync(clientId, repositoryUrl, ct);
            var evidence = await this.BuildScanEvidenceAsync(clientId, scan, ct);
            var reviewEvidence = new MergeGuardGate.ReviewEvidence(
                result.Comments.Count(c => c.Severity == CommentSeverity.Error),
                result.Comments.Count(c => c.Severity == CommentSeverity.Warning));

            var evaluation = MergeGuardGate.Evaluate(evidence, reviewEvidence, threshold.Value, DateTimeOffset.UtcNow);
            return await this.ReportAsync(
                clientId,
                review,
                revision,
                repositoryUrl,
                owner,
                repository,
                threshold.Value,
                scan,
                evidence,
                reviewEvidence,
                evaluation,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The guard must never take a review publication down with it, and must never
            // report a pass it did not compute. Neutral + the reason, logged server-side.
            logger.LogWarning(
                ex,
                "Merge-guard evaluation failed for client {ClientId} pull request {PullRequestNumber}; reporting neutral.",
                clientId,
                review.Number);
            try
            {
                return await this.ReportNeutralAsync(
                    clientId,
                    review,
                    revision,
                    "The guard could not read the data it needs; see the API log.",
                    null,
                    null,
                    ct);
            }
            catch (Exception storeEx) when (storeEx is not OperationCanceledException)
            {
                logger.LogWarning(storeEx, "Merge-guard neutral report could not be recorded for client {ClientId}.", clientId);
                return new MergeGuardReport(
                    MergeGuardDecision.Neutral,
                    "The guard could not read the data it needs; see the API log.",
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    Posted: false,
                    PostError: storeEx.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task<MergeGuardStatusResponse> GetStatusAsync(Guid clientId, CancellationToken ct)
    {
        var state = await store.GetStateAsync(clientId, ct);
        return new MergeGuardStatusResponse(
            new MergeGuardConfigResponse(state.Config.Enabled, state.Config.Threshold),
            state.Checks.Select(ToCheckResponse).ToList());
    }

    /// <inheritdoc />
    public async Task<MergeGuardConfigResponse> UpdateConfigAsync(Guid clientId, bool enabled, string threshold, CancellationToken ct)
    {
        var normalized = MergeGuardThreshold.Normalize(threshold);
        if (normalized is null)
        {
            throw new ArgumentException(
                $"Threshold must be one of: {string.Join(", ", MergeGuardThreshold.All)}.",
                nameof(threshold));
        }

        var saved = await store.SaveConfigAsync(clientId, new MergeGuardConfigRecord(enabled, normalized.Value.ToString()), ct);
        return new MergeGuardConfigResponse(saved.Enabled, saved.Threshold);
    }

    private async Task<MergeGuardGate.ScanEvidence> BuildScanEvidenceAsync(
        Guid clientId,
        SecurityScanResponse? scan,
        CancellationToken ct)
    {
        if (scan is null)
        {
            return new MergeGuardGate.ScanEvidence(false, null, null, 0, 0, 0, 0);
        }

        // Totals only: limit 1 reads TotalCount without hauling findings over the wire.
        // Only open findings count — acknowledged, suppressed, and resolved findings are
        // a human's explicit decision and never block.
        async Task<int> OpenCountAsync(string severity)
        {
            var page = await scanService.GetFindingsAsync(clientId, scan.ScanId, severity, null, "Open", 1, 0, ct);
            return page.TotalCount;
        }

        return new MergeGuardGate.ScanEvidence(
            true,
            scan.Status,
            scan.FinishedAt,
            await OpenCountAsync(nameof(SecurityFindingSeverity.Critical)),
            await OpenCountAsync(nameof(SecurityFindingSeverity.High)),
            await OpenCountAsync(nameof(SecurityFindingSeverity.Medium)),
            await OpenCountAsync(nameof(SecurityFindingSeverity.Low)));
    }

    private async Task<MergeGuardReport> ReportAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        string repositoryUrl,
        string owner,
        string repository,
        SecurityFindingSeverity threshold,
        SecurityScanResponse? scan,
        MergeGuardGate.ScanEvidence evidence,
        MergeGuardGate.ReviewEvidence reviewEvidence,
        MergeGuardGate.Evaluation evaluation,
        CancellationToken ct)
    {
        var state = await store.GetStateAsync(clientId, ct);
        var existing = state.Checks.FirstOrDefault(c =>
            string.Equals(c.RepositoryUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase)
            && c.PullRequestNumber == review.Number
            && string.Equals(c.HeadSha, revision.HeadSha, StringComparison.Ordinal));
        var title = BuildTitle(evaluation);
        var summary = BuildSummary(review, revision, owner, repository, threshold, scan, evidence, reviewEvidence, evaluation);
        var detailsUrl = this.TryBuildDashboardLink(clientId);

        long? checkRunId = null;
        string? checkRunUrl = null;
        var posted = false;
        string? postError = null;
        try
        {
            var postedRun = await checkRunPoster.PostOrUpdateCheckRunAsync(
                clientId,
                review.Repository.Host,
                owner,
                repository,
                revision.HeadSha,
                CheckRunName,
                evaluation.Decision,
                title,
                summary,
                detailsUrl,
                existing?.CheckRunId,
                ct);
            checkRunId = postedRun.CheckRunId;
            checkRunUrl = postedRun.HtmlUrl;
            posted = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The decision stands; only the delivery failed. Record it honestly.
            postError = ex.Message;
            logger.LogWarning(
                ex,
                "Merge-guard check run could not be posted for client {ClientId} pull request {PullRequestNumber}.",
                clientId,
                review.Number);
        }

        var record = new MergeGuardCheckRecord(
            repositoryUrl,
            review.Number,
            revision.HeadSha,
            checkRunId ?? existing?.CheckRunId,
            checkRunUrl ?? existing?.CheckRunUrl,
            evaluation.Decision.ToString().ToLowerInvariant(),
            title,
            evaluation.Reason,
            evaluation.BlockingScanFindings,
            evaluation.BlockingReviewFindings,
            DateTimeOffset.UtcNow,
            posted,
            postError);
        await this.TrySaveCheckAsync(clientId, record, ct);

        return new MergeGuardReport(
            evaluation.Decision,
            evaluation.Reason,
            evaluation.BlockingScanFindings,
            evaluation.BlockingReviewFindings,
            scan?.Status,
            scan?.FinishedAt,
            record.CheckRunId,
            record.CheckRunUrl,
            posted,
            postError);
    }

    private async Task<MergeGuardReport> ReportNeutralAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        string reason,
        string? scanStatus,
        DateTimeOffset? scanFinishedAt,
        CancellationToken ct)
    {
        // Best effort: a neutral evaluation with no scan data behind it still deserves a
        // visible check run, so the pull request shows why the guard could not decide
        // instead of showing nothing at all. When even that fails, the report still
        // carries the reason.
        var state = await store.GetStateAsync(clientId, ct);
        var (owner, repository) = BuildRepositoryCoordinates(review.Repository);
        var repositoryUrl = BuildBestEffortRepositoryUrl(review, owner, repository);
        var existing = state.Checks.FirstOrDefault(c =>
            string.Equals(c.RepositoryUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase)
            && c.PullRequestNumber == review.Number
            && string.Equals(c.HeadSha, revision.HeadSha, StringComparison.Ordinal));
        var title = "Merge guard could not decide";
        var summary = BuildNeutralSummary(review, revision, owner, repository, reason);

        long? checkRunId = null;
        string? checkRunUrl = null;
        var posted = false;
        string? postError = null;
        try
        {
            var postedRun = await checkRunPoster.PostOrUpdateCheckRunAsync(
                clientId,
                review.Repository.Host,
                owner,
                repository,
                revision.HeadSha,
                CheckRunName,
                MergeGuardDecision.Neutral,
                title,
                summary,
                this.TryBuildDashboardLink(clientId),
                existing?.CheckRunId,
                ct);
            checkRunId = postedRun.CheckRunId;
            checkRunUrl = postedRun.HtmlUrl;
            posted = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            postError = ex.Message;
            logger.LogWarning(
                ex,
                "Merge-guard neutral check run could not be posted for client {ClientId} pull request {PullRequestNumber}.",
                clientId,
                review.Number);
        }

        var record = new MergeGuardCheckRecord(
            repositoryUrl,
            review.Number,
            revision.HeadSha,
            checkRunId ?? existing?.CheckRunId,
            checkRunUrl ?? existing?.CheckRunUrl,
            "neutral",
            title,
            reason,
            0,
            0,
            DateTimeOffset.UtcNow,
            posted,
            postError);
        await this.TrySaveCheckAsync(clientId, record, ct);

        return new MergeGuardReport(
            MergeGuardDecision.Neutral,
            reason,
            0,
            0,
            scanStatus,
            scanFinishedAt,
            record.CheckRunId,
            record.CheckRunUrl,
            posted,
            postError);
    }

    private async Task TrySaveCheckAsync(Guid clientId, MergeGuardCheckRecord record, CancellationToken ct)
    {
        try
        {
            await store.SaveCheckAsync(clientId, record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Merge-guard check record could not be saved for client {ClientId}.", clientId);
        }
    }

    private string? TryBuildDashboardLink(Guid clientId)
    {
        // A link is offered when the address is known and left out when it is not. A
        // guessed host would send a reader nowhere.
        var uiOrigin = publicApplicationOptions?.Value.UiOrigin;
        return Uri.TryCreate(uiOrigin, UriKind.Absolute, out var origin)
            ? new Uri(origin, $"/clients/{clientId:D}/dashboard").ToString()
            : null;
    }

    private static (string Owner, string Repository) BuildRepositoryCoordinates(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return (repository.OwnerOrNamespace, repositoryName);
    }

    private static string BuildBestEffortRepositoryUrl(CodeReviewRef review, string owner, string repository)
    {
        try
        {
            var host = new Uri(review.Repository.Host.HostBaseUrl).Host;
            return $"https://{host}/{owner}/{repository}";
        }
        catch (UriFormatException)
        {
            return $"{owner}/{repository}";
        }
    }

    private static string BuildTitle(MergeGuardGate.Evaluation evaluation)
    {
        return evaluation.Decision switch
        {
            MergeGuardDecision.Success => "Merge guard passed — no blocking findings",
            MergeGuardDecision.Failure => $"Merge guard failed — {evaluation.BlockingScanFindings + evaluation.BlockingReviewFindings} blocking finding(s)",
            _ => "Merge guard could not decide",
        };
    }

    private static string BuildSummary(
        CodeReviewRef review,
        ReviewRevision revision,
        string owner,
        string repository,
        SecurityFindingSeverity threshold,
        SecurityScanResponse? scan,
        MergeGuardGate.ScanEvidence evidence,
        MergeGuardGate.ReviewEvidence reviewEvidence,
        MergeGuardGate.Evaluation evaluation)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Repository:** {owner}/{repository} — PR #{review.Number} (head `{ShortSha(revision.HeadSha)}`)");
        builder.AppendLine($"**Decision:** {evaluation.Decision.ToString().ToLowerInvariant()} — {evaluation.Reason}");
        builder.AppendLine(
            scan is null
                ? "**Latest scan:** none for this repository."
                : $"**Latest scan:** {scan.Status}, finished {(scan.FinishedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "unknown")} UTC.");
        builder.AppendLine($"**Open scan findings:** {evidence.Critical} critical, {evidence.High} high, {evidence.Medium} medium, {evidence.Low} low.");
        builder.AppendLine($"**Review findings:** {reviewEvidence.Errors} error(s), {reviewEvidence.Warnings} warning(s).");
        builder.AppendLine($"**Threshold:** {threshold} — findings at or above this severity block merging.");
        builder.AppendLine();
        builder.AppendLine(
            $"> To actually block merging, add \"{CheckRunName}\" to this branch's required status checks. The check reports the decision; branch protection enforces it.");
        builder.Append("Evaluated by AegisCode.");
        return builder.ToString();
    }

    private static string BuildNeutralSummary(
        CodeReviewRef review,
        ReviewRevision revision,
        string owner,
        string repository,
        string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Repository:** {owner}/{repository} — PR #{review.Number} (head `{ShortSha(revision.HeadSha)}`)");
        builder.AppendLine($"**Decision:** neutral — {reason}");
        builder.AppendLine();
        builder.AppendLine("No blocking evidence was found, but the guard did not have the data to confirm the pull request is safe to merge.");
        builder.Append("Evaluated by AegisCode.");
        return builder.ToString();
    }

    private static string ShortSha(string sha)
    {
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static MergeGuardCheckResponse ToCheckResponse(MergeGuardCheckRecord check)
    {
        return new MergeGuardCheckResponse(
            check.RepositoryUrl,
            check.PullRequestNumber,
            check.HeadSha,
            check.CheckRunId,
            check.CheckRunUrl,
            check.Conclusion,
            check.Title,
            check.Reason,
            check.BlockingScanFindings,
            check.BlockingReviewFindings,
            check.UpdatedAt,
            check.Posted,
            check.PostError);
    }
}
