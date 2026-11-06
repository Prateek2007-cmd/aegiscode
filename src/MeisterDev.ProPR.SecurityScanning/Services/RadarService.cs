// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Raised when the radar cannot run at all (disabled installation). Maps to HTTP 503.</summary>
public sealed class RadarUnavailableException(string message) : Exception(message);

/// <summary>
///     Dependency Radar: re-checks each client's stored SBOMs against OSV.dev and
///     raises an alert when a Critical or High CVE appears that no scan has
///     reported yet. Only OSV data is ever used — a CVE, severity, or fixed
///     version is never invented. A pass that finds nothing new says so; a pass
///     that could not check anything says that instead.
/// </summary>
public interface IRadarService
{
    /// <summary>Runs one radar pass for the client right now, regardless of the watch schedule.</summary>
    Task<RadarPassResponse> CheckNowAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Runs a scheduled pass when the client's watch is enabled and due.
    ///     Returns <c>null</c> when the watch is disabled, not due, or the radar is off.
    /// </summary>
    Task<RadarPassResponse?> RunScheduledPassAsync(Guid clientId, CancellationToken ct);

    /// <summary>Lists the client's radar alerts, newest first. Never returns another client's alerts.</summary>
    Task<IReadOnlyList<RadarAlertResponse>> ListAlertsAsync(Guid clientId, CancellationToken ct);

    /// <summary>Reads the client's radar watch settings (defaults when never saved).</summary>
    Task<RadarSettingsResponse> GetSettingsAsync(Guid clientId, CancellationToken ct);

    /// <summary>Updates the client's radar watch settings. Throws <see cref="ArgumentException"/> on an invalid interval.</summary>
    Task<RadarSettingsResponse> UpdateSettingsAsync(Guid clientId, UpdateRadarSettingsRequest request, CancellationToken ct);

    /// <summary>
    ///     Acknowledges a New alert. Idempotent: acknowledging an already-acknowledged
    ///     or resolved alert returns it unchanged. Returns <c>null</c> when it does
    ///     not exist for this client.
    /// </summary>
    Task<RadarAlertResponse?> AcknowledgeAlertAsync(Guid clientId, Guid alertId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class RadarService(
    MeisterProPRDbContext db,
    IOsvClient osvClient,
    IRadarStore store,
    IOptions<SecurityScanningOptions> options,
    ILogger<RadarService> logger) : IRadarService
{
    private const int MinIntervalMinutes = 15;
    private const int MaxIntervalMinutes = 10080;
    private const int MaxDetailConcurrency = 4;

    /// <inheritdoc />
    public async Task<RadarPassResponse> CheckNowAsync(Guid clientId, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled || !opts.RadarEnabled)
        {
            throw new RadarUnavailableException("Dependency Radar is disabled on this installation.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = await store.GetStateAsync(clientId, opts.RadarDefaultIntervalMinutes, ct);
        var knownKeys = await this.BuildKnownKeysAsync(clientId, state, ct);
        var repos = await this.LatestScansWithSbomAsync(clientId, ct);

        var repoResults = new List<RadarPassRepoResponse>();
        var newAlerts = new List<RadarAlertRecord>();
        var seenKeys = new HashSet<string>(state.SeenKeys, StringComparer.Ordinal);
        var totalComponents = 0;
        var totalSkipped = 0;
        var totalNewOccurrences = 0;
        var errors = new List<string>();

        foreach (var scan in repos)
        {
            ct.ThrowIfCancellationRequested();
            var result = await this.CheckRepoAsync(clientId, scan, knownKeys, seenKeys, now, ct);
            repoResults.Add(result.Repo);
            newAlerts.AddRange(result.NewAlerts);
            totalComponents += result.Repo.ComponentsChecked;
            totalSkipped += result.Repo.ComponentsSkipped;
            totalNewOccurrences += result.Repo.NewOccurrences;
            if (result.Repo.Error is not null)
            {
                errors.Add($"{scan.RepositoryUrl}: {result.Repo.Error}");
            }
        }

        if (newAlerts.Count > 0)
        {
            await store.AddAlertsAsync(clientId, newAlerts, ct);
        }

        var summary = new RadarPassSummaryRecord(
            now,
            repoResults.Count,
            totalComponents,
            totalSkipped,
            totalNewOccurrences,
            newAlerts.Count,
            errors);
        var updatedState = state with
        {
            LastPassAt = now,
            LastPass = summary,
            SeenKeys = seenKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        };
        await store.SaveStateAsync(clientId, updatedState, ct);

        var nothingNew = repoResults.Count > 0 && totalNewOccurrences == 0 && errors.Count == 0;
        var message = repoResults.Count == 0
            ? "No scans with SBOMs yet — run a scan first."
            : errors.Count > 0 && repoResults.All(r => r.Error is not null)
                ? "OSV.dev could not be reached for any repository — no new vulnerabilities were checked."
                : null;

        logger.LogInformation(
            "Radar pass for client {ClientId}: {Repos} repos, {Components} components, {New} new CVEs, {Alerts} new alerts",
            clientId, repoResults.Count, totalComponents, totalNewOccurrences, newAlerts.Count);

        return new RadarPassResponse(
            now,
            repoResults.Count,
            totalComponents,
            totalSkipped,
            totalNewOccurrences,
            newAlerts.Count,
            nothingNew,
            repoResults,
            message);
    }

    /// <inheritdoc />
    public async Task<RadarPassResponse?> RunScheduledPassAsync(Guid clientId, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled || !opts.RadarEnabled)
        {
            return null;
        }

        var state = await store.GetStateAsync(clientId, opts.RadarDefaultIntervalMinutes, ct);
        if (!state.Enabled)
        {
            return null;
        }

        if (state.LastPassAt is not null
            && state.LastPassAt.Value + TimeSpan.FromMinutes(state.IntervalMinutes) > DateTimeOffset.UtcNow)
        {
            return null;
        }

        return await this.CheckNowAsync(clientId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarAlertResponse>> ListAlertsAsync(Guid clientId, CancellationToken ct)
    {
        var alerts = await store.ListAlertsAsync(clientId, ct);
        return alerts
            .OrderByDescending(a => a.FirstSeenAt)
            .ThenBy(a => RadarDetector.SeverityRank(a.Severity))
            .Select(ToResponse)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RadarSettingsResponse> GetSettingsAsync(Guid clientId, CancellationToken ct)
    {
        var state = await store.GetStateAsync(clientId, options.Value.RadarDefaultIntervalMinutes, ct);
        return new RadarSettingsResponse(state.Enabled, state.IntervalMinutes, state.LastPassAt);
    }

    /// <inheritdoc />
    public async Task<RadarSettingsResponse> UpdateSettingsAsync(Guid clientId, UpdateRadarSettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interval = request.IntervalMinutes;
        if (interval is not null && (interval.Value < MinIntervalMinutes || interval.Value > MaxIntervalMinutes))
        {
            throw new ArgumentException(
                $"intervalMinutes must be between {MinIntervalMinutes} and {MaxIntervalMinutes}.",
                nameof(request));
        }

        var state = await store.GetStateAsync(clientId, options.Value.RadarDefaultIntervalMinutes, ct);
        var updated = state with
        {
            Enabled = request.Enabled ?? state.Enabled,
            IntervalMinutes = interval ?? state.IntervalMinutes,
        };
        await store.SaveStateAsync(clientId, updated, ct);
        return new RadarSettingsResponse(updated.Enabled, updated.IntervalMinutes, updated.LastPassAt);
    }

    /// <inheritdoc />
    public async Task<RadarAlertResponse?> AcknowledgeAlertAsync(Guid clientId, Guid alertId, CancellationToken ct)
    {
        var alerts = await store.ListAlertsAsync(clientId, ct);
        var alert = alerts.FirstOrDefault(a => a.Id == alertId);
        if (alert is null)
        {
            return null;
        }

        if (alert.Status != RadarAlertStatus.New)
        {
            return ToResponse(alert);
        }

        var acknowledged = alert with
        {
            Status = RadarAlertStatus.Acknowledged,
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        var saved = await store.UpdateAlertAsync(clientId, acknowledged, ct);
        return saved is null ? null : ToResponse(saved);
    }

    /// <summary>
    ///     Known set for one client: the radar ledger from past passes, plus the
    ///     vulnerability ids past dep-scan findings already reported (parsed from
    ///     finding evidence into exact keys where possible, coarse rule-id keys
    ///     otherwise). The first radar pass therefore never re-alerts on what the
    ///     last full scan already found.
    /// </summary>
    private async Task<HashSet<string>> BuildKnownKeysAsync(Guid clientId, RadarStateRecord state, CancellationToken ct)
    {
        var known = new HashSet<string>(state.SeenKeys, StringComparer.Ordinal);

        var depFindings = await db.SecurityFindings
            .AsNoTracking()
            .Where(f => f.ClientId == clientId && f.Scanner == SecurityScanners.DepScan && f.RuleId != null)
            .Select(f => new { f.RuleId, f.Evidence })
            .ToListAsync(ct);
        foreach (var key in RadarDetector.BuildKnownKeys(depFindings.Select(f => (f.RuleId, f.Evidence))))
        {
            known.Add(key);
        }

        return known;
    }

    /// <summary>Latest succeeded or partially-succeeded scan per repository URL that still has an SBOM on disk.</summary>
    private async Task<IReadOnlyList<SecurityScanRecord>> LatestScansWithSbomAsync(Guid clientId, CancellationToken ct)
    {
        var scans = await db.SecurityScans
            .AsNoTracking()
            .Where(s => s.ClientId == clientId
                        && s.SbomPath != null
                        && (s.Status == (int)SecurityScanStatus.Succeeded
                            || s.Status == (int)SecurityScanStatus.PartiallySucceeded))
            .OrderByDescending(s => s.FinishedAt ?? s.QueuedAt)
            .Take(200)
            .ToListAsync(ct);

        return scans
            .GroupBy(s => s.RepositoryUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.FinishedAt ?? s.QueuedAt).First())
            .OrderBy(s => s.RepositoryUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Checks one repository's SBOM against OSV. One repo failing never aborts the others.</summary>
    private async Task<(RadarPassRepoResponse Repo, IReadOnlyList<RadarAlertRecord> NewAlerts)> CheckRepoAsync(
        Guid clientId,
        SecurityScanRecord scan,
        HashSet<string> knownKeys,
        HashSet<string> seenKeys,
        DateTimeOffset now,
        CancellationToken ct)
    {
        string sbomJson;
        try
        {
            sbomJson = await ReadSbomAsync(scan, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Radar could not read the SBOM for scan {ScanId}", scan.Id);
            return (new RadarPassRepoResponse(scan.RepositoryUrl, scan.Id, 0, 0, 0, 0, 0, "The stored SBOM is no longer on disk — re-run the scan."), []);
        }

        var parsed = SbomComponentParser.Parse(sbomJson);
        if (parsed.Error is not null)
        {
            return (new RadarPassRepoResponse(scan.RepositoryUrl, scan.Id, 0, parsed.Skipped, 0, 0, 0, parsed.Error), []);
        }

        // Same package version twice (e.g. nested lockfiles) is checked once.
        var components = parsed.Components
            .DistinctBy(c => (c.Ecosystem, c.Name, c.Version))
            .ToList();

        IReadOnlyList<OsvBatchEntry> batch;
        try
        {
            batch = await osvClient.QueryBatchAsync(
                components.Select(c => new OsvPackageQuery(c.Name, c.Ecosystem, c.Version)).ToList(),
                ct);
        }
        catch (OsvClientException ex)
        {
            return (new RadarPassRepoResponse(scan.RepositoryUrl, scan.Id, 0, parsed.Skipped, 0, 0, 0, ex.Message), []);
        }

        // Per-component OSV results: needed both for new-vuln detection and for
        // resolving alerts whose vulnerability no longer affects the component.
        var vulnsByComponent = new Dictionary<(string Ecosystem, string Name, string Version), HashSet<string>>();
        var freshByComponent = new Dictionary<(string Ecosystem, string Name, string Version), List<string>>();
        foreach (var entry in batch)
        {
            var key = (entry.Query.Ecosystem, entry.Query.Name, entry.Query.Version);
            vulnsByComponent[key] = entry.VulnIds.ToHashSet(StringComparer.Ordinal);
            freshByComponent[key] = entry.VulnIds
                .Where(id => !RadarDetector.IsKnown(id, entry.Query.Name, entry.Query.Version, knownKeys))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        // Details only for unknown ids: known vulnerabilities need no re-fetch.
        var unknownIds = freshByComponent.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).ToList();
        var (details, detailsSkipped) = await this.FetchDetailsAsync(unknownIds, ct);

        var occurrences = new List<RadarVulnOccurrence>();
        foreach (var (component, ids) in freshByComponent)
        {
            foreach (var id in ids)
            {
                if (!details.TryGetValue(id, out var detail))
                {
                    continue;
                }

                var (severity, _) = OsvSeverity.Map(detail);
                occurrences.Add(
                    new RadarVulnOccurrence(
                        detail.Id,
                        component.Name,
                        component.Version,
                        component.Ecosystem,
                        severity,
                        detail.Summary,
                        detail.FixedVersion,
                        detail.CveId));
            }
        }

        var newOccurrences = RadarDetector.DetectNew(occurrences, knownKeys);
        foreach (var occurrence in newOccurrences)
        {
            seenKeys.Add(RadarDetector.ExactKey(occurrence.VulnId, occurrence.PackageName, occurrence.PackageVersion));
        }

        var newAlerts = newOccurrences
            .Where(o => RadarDetector.IsAlertSeverity(o.Severity))
            .Select(o => new RadarAlertRecord(
                Guid.NewGuid(),
                clientId,
                scan.Id,
                scan.RepositoryUrl,
                o.PackageName,
                o.PackageVersion,
                o.Ecosystem,
                o.VulnId,
                o.CveId,
                o.Severity,
                o.Summary,
                o.FixedVersion,
                $"https://osv.dev/vulnerability/{o.VulnId}",
                now,
                now,
                RadarAlertStatus.New,
                null,
                null))
            .ToList();

        await this.RefreshAlertStatesAsync(clientId, scan, vulnsByComponent, components, now, ct);

        return (
            new RadarPassRepoResponse(
                scan.RepositoryUrl,
                scan.Id,
                components.Count,
                parsed.Skipped,
                newOccurrences.Count,
                newAlerts.Count,
                detailsSkipped,
                null),
            newAlerts);
    }

    /// <summary>
    ///     Refreshes open alerts for one checked repository: an alert whose
    ///     vulnerability OSV no longer reports for its component is resolved,
    ///     and one that is still reported gets its last-seen time refreshed.
    ///     Alerts for components that were not checked (or repositories not in
    ///     this pass) are left untouched.
    /// </summary>
    private async Task RefreshAlertStatesAsync(
        Guid clientId,
        SecurityScanRecord scan,
        Dictionary<(string Ecosystem, string Name, string Version), HashSet<string>> vulnsByComponent,
        IReadOnlyList<SbomComponent> components,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var checkedComponents = components
            .Select(c => (c.Ecosystem, c.Name, c.Version))
            .ToHashSet();
        var alerts = await store.ListAlertsAsync(clientId, ct);
        foreach (var alert in alerts)
        {
            if (alert.Status is not (RadarAlertStatus.New or RadarAlertStatus.Acknowledged))
            {
                continue;
            }

            if (!string.Equals(alert.RepositoryUrl, scan.RepositoryUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = (alert.Ecosystem, alert.PackageName, alert.PackageVersion);
            if (!checkedComponents.Contains(key) || !vulnsByComponent.TryGetValue(key, out var currentVulns))
            {
                continue;
            }

            if (currentVulns.Contains(alert.VulnId, StringComparer.Ordinal))
            {
                if (alert.LastSeenAt != now)
                {
                    await store.UpdateAlertAsync(clientId, alert with { LastSeenAt = now }, ct);
                }
            }
            else
            {
                await store.UpdateAlertAsync(
                    clientId,
                    alert with { Status = RadarAlertStatus.Resolved, ResolvedAt = now },
                    ct);
            }
        }
    }

    /// <summary>Fetches OSV details for unknown ids with bounded concurrency. A single id failing never fails the pass.</summary>
    private async Task<(Dictionary<string, OsvVulnDetail> Details, int Skipped)> FetchDetailsAsync(
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var details = new Dictionary<string, OsvVulnDetail>(StringComparer.Ordinal);
        var skippedCount = 0;
        using var gate = new SemaphoreSlim(MaxDetailConcurrency, MaxDetailConcurrency);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var detail = await osvClient.GetVulnAsync(id, ct);
                if (detail is null)
                {
                    Interlocked.Increment(ref skippedCount);
                    return;
                }

                lock (details)
                {
                    details[detail.Id] = detail;
                }
            }
            catch (OsvClientException ex)
            {
                logger.LogWarning(ex, "OSV detail fetch failed for vulnerability {VulnId}", id);
                Interlocked.Increment(ref skippedCount);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return (details, skippedCount);
    }

    /// <summary>Reads one scan's SBOM with the same containment as the scans service: only this scan's file, by name.</summary>
    private static async Task<string> ReadSbomAsync(SecurityScanRecord scan, CancellationToken ct)
    {
        if (scan.SbomPath is null)
        {
            throw new IOException("Scan has no SBOM path.");
        }

        var fileName = Path.GetFileName(scan.SbomPath);
        if (!string.Equals(fileName, $"{scan.Id:N}.cyclonedx.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Scan SBOM path is not the expected file.");
        }

        return await File.ReadAllTextAsync(scan.SbomPath, ct);
    }

    internal static RadarAlertResponse ToResponse(RadarAlertRecord record) =>
        new(
            record.Id,
            record.ScanId,
            record.RepositoryUrl,
            record.PackageName,
            record.PackageVersion,
            record.Ecosystem,
            record.VulnId,
            record.CveId,
            record.Severity,
            record.Summary,
            record.FixedVersion,
            record.OsvUrl,
            record.FirstSeenAt,
            record.LastSeenAt,
            record.Status.ToString(),
            record.AcknowledgedAt,
            record.ResolvedAt);
}
