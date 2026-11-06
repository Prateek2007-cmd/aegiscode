// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Reads and writes the client's push-scan configuration and recent push-triggered scans (dashboard API).</summary>
public interface IPushScanManagementService
{
    /// <summary>Returns the client's push-scan config plus its latest push-triggered scans, newest first. Never returns another client's state.</summary>
    Task<PushScanStatusResponse> GetStatusAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Saves the client's push-scan configuration. Throws <see cref="ArgumentException" />
    ///     when the branch list is unusable (empty after normalization, too many
    ///     branches, or a branch with whitespace). Null or empty branches reset to main only.
    /// </summary>
    Task<PushScanConfigResponse> UpdateConfigAsync(Guid clientId, bool enabled, IReadOnlyList<string>? branches, CancellationToken ct);
}

/// <summary>
///     Push-triggered security scans. A verified push delivery for a repository the
///     client covers reaches here only after the webhook handler checked signature,
///     event enablement, and the repository filter — this service owns the rest:
///     the per-client on/off toggle, the branch filter (default: main only), the
///     (repository, commit SHA) dedupe, and starting the scan through the normal
///     scan service so the scan runs exactly like a manual one. Nothing here ever
///     claims a scan ran when it did not: a failed start is recorded as failed
///     with its reason, and a duplicate, disabled, or unconfigured push is
///     reported as such without touching the scan queue.
/// </summary>
public sealed class PushScanService(
    ISecurityScanService scanService,
    IPushScanStore store,
    IOptions<SecurityScanningOptions> options,
    ILogger<PushScanService> logger) : IPushScanTrigger, IPushScanManagementService
{
    private const int MaxBranches = 20;
    private const int MaxBranchLength = 64;

    /// <inheritdoc />
    public async Task<PushScanTriggerOutcome> HandlePushAsync(
        Guid clientId,
        PushScanTriggerRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string repositoryUrl;
        try
        {
            repositoryUrl = SecurityScanUrlValidator.ValidateAndNormalize(request.RepositoryUrl, options.Value.AllowedHosts);
        }
        catch (ArgumentException ex)
        {
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.ScanStartFailed,
                $"The repository URL for this push is not supported by security scanning: {ex.Message}");
        }

        var branch = NormalizeBranch(request.Branch);
        var headSha = (request.HeadSha ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(headSha) || headSha.All(c => c == '0'))
        {
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.IgnoredNoCommit,
                "The push carried no commit to scan (for example, the branch was deleted).");
        }

        var state = await store.GetStateAsync(clientId, ct);
        if (!state.Config.Enabled)
        {
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.Disabled,
                "Push scans are switched off for this client.");
        }

        if (!state.Config.Branches.Any(pattern => MatchesBranchPattern(pattern, branch)))
        {
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.BranchNotConfigured,
                $"Branch {branch} is not in this client's push-scan branch filter ({string.Join(", ", state.Config.Branches)}).");
        }

        var pending = new PushScanEntryRecord(
            repositoryUrl,
            branch,
            headSha,
            string.IsNullOrWhiteSpace(request.Pusher) ? null : request.Pusher.Trim(),
            "pending",
            "Scan is being queued.",
            null,
            null,
            DateTimeOffset.UtcNow);

        var reserved = await store.TryReserveAsync(clientId, pending, ct);
        if (reserved is null)
        {
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.AlreadyScanned,
                $"Commit {headSha} on {branch} was already scanned; no new scan was started.");
        }

        try
        {
            var started = await scanService.StartScanAsync(
                clientId,
                new StartSecurityScanRequest(repositoryUrl, branch),
                null,
                ct);
            var entry = pending with
            {
                Outcome = "started",
                Reason = $"Scan {started.ScanId} queued for {branch} at {ShortSha(headSha)}.",
                ScanId = started.ScanId,
                ScanStatus = started.Status,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await this.TrySaveEntryAsync(clientId, entry, ct);
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.Started,
                entry.Reason,
                started.ScanId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The scan never queued, so the reservation must not block a retry:
            // record the failure honestly and let the next delivery try again.
            var entry = pending with
            {
                Outcome = "failed",
                Reason = $"The scan could not be started: {ex.Message}",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await this.TrySaveEntryAsync(clientId, entry, ct);
            logger.LogWarning(
                ex,
                "Push scan could not be started for client {ClientId} repository {RepositoryUrl} branch {Branch} at {HeadSha}.",
                clientId,
                repositoryUrl,
                branch,
                headSha);
            return new PushScanTriggerOutcome(
                PushScanTriggerOutcomeKind.ScanStartFailed,
                entry.Reason);
        }
    }

    /// <inheritdoc />
    public async Task<PushScanStatusResponse> GetStatusAsync(Guid clientId, CancellationToken ct)
    {
        var state = await store.GetStateAsync(clientId, ct);
        var responses = new List<PushScanEntryResponse>(state.Entries.Count);
        foreach (var entry in state.Entries)
        {
            var scanStatus = entry.ScanStatus;
            if (entry.ScanId.HasValue && string.Equals(entry.Outcome, "started", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var scan = await scanService.GetScanAsync(clientId, entry.ScanId.Value, ct);
                    if (scan is not null)
                    {
                        scanStatus = scan.Status;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The stored snapshot stands; a failed enrichment must not hide the push-scan history.
                    logger.LogWarning(
                        ex,
                        "Could not refresh the live scan status for push scan {ScanId}; showing the stored status.",
                        entry.ScanId.Value);
                }
            }

            responses.Add(ToEntryResponse(entry with { ScanStatus = scanStatus }));
        }

        return new PushScanStatusResponse(
            new PushScanConfigResponse(state.Config.Enabled, state.Config.Branches),
            responses);
    }

    /// <inheritdoc />
    public async Task<PushScanConfigResponse> UpdateConfigAsync(Guid clientId, bool enabled, IReadOnlyList<string>? branches, CancellationToken ct)
    {
        var normalized = NormalizeBranches(branches);
        var saved = await store.SaveConfigAsync(clientId, new PushScanConfigRecord(enabled, normalized), ct);
        return new PushScanConfigResponse(saved.Enabled, saved.Branches);
    }

    /// <summary>Normalizes a branch list for storage. Null or empty resets to main only.</summary>
    internal static IReadOnlyList<string> NormalizeBranches(IReadOnlyList<string>? branches)
    {
        if (branches is null || branches.Count == 0)
        {
            return ["main"];
        }

        var normalized = new List<string>();
        foreach (var raw in branches)
        {
            var branch = NormalizeBranch(raw);
            if (string.IsNullOrWhiteSpace(branch))
            {
                throw new ArgumentException("Branch names must not be empty.", nameof(branches));
            }

            if (branch.Length > MaxBranchLength)
            {
                throw new ArgumentException($"Branch names must be at most {MaxBranchLength} characters: {branch}.", nameof(branches));
            }

            if (branch.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"Branch names must not contain whitespace: {branch}.", nameof(branches));
            }

            if (!normalized.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase)))
            {
                normalized.Add(branch);
            }
        }

        if (normalized.Count == 0)
        {
            return ["main"];
        }

        if (normalized.Count > MaxBranches)
        {
            throw new ArgumentException($"At most {MaxBranches} branches can be configured.", nameof(branches));
        }

        return normalized;
    }

    /// <summary>Glob branch match (<c>*</c> and <c>?</c>), case-insensitive — the same shape as the webhook repo filters.</summary>
    internal static bool MatchesBranchPattern(string pattern, string branch)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(branch))
        {
            return false;
        }

        var regexPattern = "^" + Regex.Escape(NormalizeBranch(pattern))
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(
            branch,
            regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    internal static string NormalizeBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return string.Empty;
        }

        const string prefix = "refs/heads/";
        var trimmed = branch.Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private async Task TrySaveEntryAsync(Guid clientId, PushScanEntryRecord entry, CancellationToken ct)
    {
        try
        {
            await store.UpdateEntryAsync(clientId, entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Push-scan attempt could not be recorded for client {ClientId}.", clientId);
        }
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static PushScanEntryResponse ToEntryResponse(PushScanEntryRecord entry)
    {
        return new PushScanEntryResponse(
            entry.RepositoryUrl,
            entry.Branch,
            entry.HeadSha,
            entry.Pusher,
            entry.Outcome,
            entry.Reason,
            entry.ScanId,
            entry.ScanStatus,
            entry.UpdatedAt);
    }
}
