// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Threading.Channels;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Workers;

/// <summary>
///     Background consumer that executes queued security scans: clones the repository
///     into an isolated temp dir, runs the deterministic engine sidecar, persists
///     normalized findings with fingerprint-based regression detection, and records
///     scan coverage honestly (completed vs partial vs failed).
/// </summary>
public sealed partial class SecurityScanWorker(
    ChannelReader<SecurityScanJob> channelReader,
    ChannelWriter<SecurityScanJob> channelWriter,
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityScanningOptions> options,
    ILogger<SecurityScanWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.HydratePendingScansAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);
        var maxConcurrent = Math.Max(1, options.Value.MaxConcurrentScans);
        using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        try
        {
            await foreach (var job in channelReader.ReadAllAsync(stoppingToken))
            {
                await gate.WaitAsync(stoppingToken);
                _ = this.ProcessJobSafeAsync(job, gate, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task ProcessJobSafeAsync(
        SecurityScanJob job, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        try
        {
            await this.ProcessJobAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await this.MarkScanAsync(job.ScanId, SecurityScanStatus.Cancelled,
                "Scan cancelled.", null, stoppingToken);
        }
        catch (Exception ex)
        {
            // Never leak internals: user-safe message only; full details go to the log.
            logger.LogError(ex, "Security scan {ScanId} failed unexpectedly", job.ScanId);
            await this.MarkScanAsync(job.ScanId, SecurityScanStatus.Failed,
                "Scan failed unexpectedly. Check engine configuration and try again.", null, stoppingToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessJobAsync(SecurityScanJob job, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<ISecurityScanEngineRunner>();

        var scan = await db.SecurityScans
            .FirstOrDefaultAsync(s => s.Id == job.ScanId && s.ClientId == job.ClientId, stoppingToken);
        if (scan is null)
        {
            logger.LogWarning("Security scan {ScanId} not found; skipping", job.ScanId);
            return;
        }

        if ((SecurityScanStatus)scan.Status is SecurityScanStatus.Cancelled)
        {
            logger.LogInformation("Security scan {ScanId} was cancelled before starting", job.ScanId);
            return;
        }

        scan.Status = (int)SecurityScanStatus.Running;
        scan.StartedAt = DateTimeOffset.UtcNow;
        scan.ProgressPct = 5;
        await db.SaveChangesAsync(stoppingToken);

        var progress = new Progress<int>(pct =>
        {
            // Best-effort progress reporting; never fails the scan.
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var s = scopeFactory.CreateAsyncScope();
                    var d = s.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
                    var row = await d.SecurityScans.FindAsync([job.ScanId], CancellationToken.None);
                    if (row is not null && (SecurityScanStatus)row.Status == SecurityScanStatus.Running)
                    {
                        row.ProgressPct = Math.Clamp(pct, 0, 100);
                        await d.SaveChangesAsync(CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Progress update failed for scan {ScanId}", job.ScanId);
                }
            });
        });

        EngineScanResult result;
        try
        {
            result = await runner.RunAsync(
                job.ScanId, scan.RepositoryUrl, scan.Ref, job.Scanners, progress, stoppingToken);
        }
        catch (SecurityScanEngineException ex)
        {
            await this.MarkScanAsync(job.ScanId, SecurityScanStatus.Failed, ex.Message, null, stoppingToken);
            return;
        }

        await this.PersistResultAsync(db, scan, job, result, stoppingToken);
    }

    private async Task PersistResultAsync(
        MeisterProPRDbContext db,
        SecurityScanRecord scan,
        SecurityScanJob job,
        EngineScanResult result,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in result.Findings)
        {
            var fingerprint = SecurityScanFingerprint.Compute(finding);
            seenFingerprints.Add(fingerprint);
            var (priority, gaps) = SecurityScanPrioritizer.Score(finding);

            var existing = await db.SecurityFindings
                .FirstOrDefaultAsync(
                    f => f.ClientId == job.ClientId && f.Fingerprint == fingerprint, ct);

            if (existing is null)
            {
                db.SecurityFindings.Add(new SecurityFindingRecord
                {
                    Id = Guid.NewGuid(),
                    ClientId = job.ClientId,
                    ScanId = scan.Id,
                    Fingerprint = fingerprint,
                    Scanner = finding.Scanner,
                    RuleId = Truncate(finding.RuleId, 128),
                    Severity = (int)ParseSeverity(finding.Severity),
                    PriorityScore = priority,
                    ScoreGapsJson = JsonSerializer.Serialize(gaps, JsonOptions),
                    Title = Truncate(finding.Title, 512),
                    Category = Truncate(finding.Category, 64),
                    FilePath = Truncate(finding.FilePath, 1024),
                    Line = finding.Line,
                    Evidence = finding.Evidence,
                    Remediation = finding.Remediation,
                    Confidence = finding.Confidence,
                    Status = (int)SecurityFindingStatus.Open,
                    FirstSeenScanId = scan.Id,
                    LastSeenScanId = scan.Id,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                // Regression: a resolved finding that reappears is reopened.
                if ((SecurityFindingStatus)existing.Status == SecurityFindingStatus.Resolved)
                {
                    existing.Status = (int)SecurityFindingStatus.Open;
                }

                existing.LastSeenScanId = scan.Id;
                existing.LastSeenAt = now;
                existing.PriorityScore = priority;
                existing.ScoreGapsJson = JsonSerializer.Serialize(gaps, JsonOptions);
                existing.Severity = (int)ParseSeverity(finding.Severity);
                existing.Evidence = finding.Evidence;
                existing.Remediation = finding.Remediation;
            }
        }

        // Findings from earlier scans of the same repo that were not seen again are resolved.
        var stale = await db.SecurityFindings
            .Where(f => f.ClientId == job.ClientId
                && f.ScanId != scan.Id
                && (f.Status == (int)SecurityFindingStatus.Open || f.Status == (int)SecurityFindingStatus.Acknowledged))
            .Join(db.SecurityScans.Where(s => s.RepositoryUrl == scan.RepositoryUrl && s.ClientId == job.ClientId),
                f => f.ScanId,
                s => s.Id,
                (f, _) => f)
            .ToListAsync(ct);
        foreach (var finding in stale)
        {
            if (!seenFingerprints.Contains(finding.Fingerprint))
            {
                finding.Status = (int)SecurityFindingStatus.Resolved;
            }
        }

        var counts = result.Findings
            .GroupBy(f => ParseSeverity(f.Severity))
            .ToDictionary(g => g.Key, g => g.Count());
        scan.CriticalCount = GetCount(counts, SecurityFindingSeverity.Critical);
        scan.HighCount = GetCount(counts, SecurityFindingSeverity.High);
        scan.MediumCount = GetCount(counts, SecurityFindingSeverity.Medium);
        scan.LowCount = GetCount(counts, SecurityFindingSeverity.Low);
        scan.InfoCount = GetCount(counts, SecurityFindingSeverity.Info);

        scan.CoverageJson = JsonSerializer.Serialize(result.Coverage, JsonOptions);
        scan.ProgressPct = 100;
        scan.FinishedAt = now;

        if (!string.IsNullOrEmpty(result.SbomJson))
        {
            var sbomRoot = this.GetSbomRoot();
            var sbomPath = Path.Combine(sbomRoot, $"{scan.Id:N}.cyclonedx.json");
            await File.WriteAllTextAsync(sbomPath, result.SbomJson, ct);
            scan.SbomPath = sbomPath;
        }

        // Honest terminal state: partial when any scanner failed or was skipped.
        var isPartial = result.IsPartial
            || result.Coverage.ScannersFailed.Count > 0
            || result.Coverage.ScannersSkipped.Count > 0;
        scan.Status = (int)(isPartial ? SecurityScanStatus.PartiallySucceeded : SecurityScanStatus.Succeeded);
        if (isPartial && !string.IsNullOrEmpty(result.PartialReason))
        {
            scan.Error = Truncate(result.PartialReason, 2048);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Security scan {ScanId} finished: {Status} with {Findings} findings",
            scan.Id, (SecurityScanStatus)scan.Status, result.Findings.Count);
    }

    private string GetSbomRoot()
    {
        var opts = options.Value;
        var root = string.IsNullOrWhiteSpace(opts.SbomRoot)
            ? Path.Combine(string.IsNullOrWhiteSpace(opts.WorkRoot) ? Path.GetTempPath() : opts.WorkRoot, "aegis-sboms")
            : opts.SbomRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task HydratePendingScansAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

            // A scan stuck in Running across a restart never ran to completion: requeue it.
            var stuck = await db.SecurityScans
                .Where(s => s.Status == (int)SecurityScanStatus.Running)
                .ToListAsync(ct);
            foreach (var scan in stuck)
            {
                scan.Status = (int)SecurityScanStatus.Queued;
                scan.ProgressPct = 0;
                scan.StartedAt = null;
            }

            var pending = await db.SecurityScans
                .Where(s => s.Status == (int)SecurityScanStatus.Queued)
                .OrderBy(s => s.QueuedAt)
                .ToListAsync(ct);
            await db.SaveChangesAsync(ct);

            foreach (var scan in pending)
            {
                await channelWriter.WriteAsync(
                    new SecurityScanJob(scan.Id, scan.ClientId, Models.SecurityScanners.All), ct);
            }

            LogHydrated(logger, pending.Count);
        }
        catch (Exception ex)
        {
            // A hydration failure must not prevent startup; pending scans stay queued in the DB.
            logger.LogError(ex, "Failed to hydrate pending security scans on startup");
        }
    }

    private async Task MarkScanAsync(
        Guid scanId, SecurityScanStatus status, string? error, string? coverageJson, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var scan = await db.SecurityScans.FindAsync([scanId], ct);
            if (scan is null)
            {
                return;
            }

            scan.Status = (int)status;
            scan.FinishedAt = DateTimeOffset.UtcNow;
            scan.ProgressPct = 100;
            if (error is not null)
            {
                scan.Error = Truncate(error, 2048);
            }
            if (coverageJson is not null)
            {
                scan.CoverageJson = coverageJson;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark scan {ScanId} as {Status}", scanId, status);
        }
    }

    private static SecurityFindingSeverity ParseSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => SecurityFindingSeverity.Critical,
        "high" => SecurityFindingSeverity.High,
        "medium" => SecurityFindingSeverity.Medium,
        "low" => SecurityFindingSeverity.Low,
        _ => SecurityFindingSeverity.Info,
    };

    private static int GetCount(
        Dictionary<SecurityFindingSeverity, int> counts, SecurityFindingSeverity severity) =>
        counts.TryGetValue(severity, out var c) ? c : 0;

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Security scan worker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Security scan worker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hydrated {Count} pending security scans")]
    private static partial void LogHydrated(ILogger logger, int count);
}
