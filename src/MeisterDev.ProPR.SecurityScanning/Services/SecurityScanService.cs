// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Threading.Channels;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Orchestrates security scans: validation → persistence → background execution.</summary>
public interface ISecurityScanService
{
    /// <summary>Validates the request, persists a queued scan, and enqueues it. Throws user-safe errors.</summary>
    Task<StartSecurityScanResponse> StartScanAsync(
        Guid clientId, StartSecurityScanRequest request, Guid? userId, CancellationToken ct);

    Task<SecurityScanResponse?> GetScanAsync(Guid clientId, Guid scanId, CancellationToken ct);
    Task<IReadOnlyList<SecurityScanResponse>> ListScansAsync(Guid clientId, int limit, int offset, CancellationToken ct);
    Task<bool> CancelScanAsync(Guid clientId, Guid scanId, CancellationToken ct);
    Task<PagedSecurityFindingsResponse> GetFindingsAsync(
        Guid clientId, Guid scanId, string? severity, string? scanner, string? status,
        int limit, int offset, CancellationToken ct);
    Task<string?> GetSbomJsonAsync(Guid clientId, Guid scanId, CancellationToken ct);
    Task<bool> SuppressFindingAsync(Guid clientId, Guid findingId, string reason, Guid? userId, CancellationToken ct);
    Task<bool> AcknowledgeFindingAsync(Guid clientId, Guid findingId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SecurityScanService(
    MeisterProPRDbContext db,
    ChannelWriter<SecurityScanJob> channelWriter,
    IOptions<SecurityScanningOptions> options,
    ILogger<SecurityScanService> logger) : ISecurityScanService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <inheritdoc />
    public async Task<StartSecurityScanResponse> StartScanAsync(
        Guid clientId, StartSecurityScanRequest request, Guid? userId, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            throw new SecurityScanEngineException(
                "Security scanning is disabled on this installation (AEGIS_SECURITY_ENABLED=false).");
        }

        ArgumentNullException.ThrowIfNull(request);
        var normalizedUrl = SecurityScanUrlValidator.ValidateAndNormalize(request.RepositoryUrl, opts.AllowedHosts);

        var scanners = NormalizeScanners(request.Scanners);

        var scan = new SecurityScanRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            RepositoryUrl = normalizedUrl,
            Ref = string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref.Trim(),
            Status = (int)SecurityScanStatus.Queued,
            ProgressPct = 0,
            QueuedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
        };

        db.SecurityScans.Add(scan);
        await db.SaveChangesAsync(ct);

        await channelWriter.WriteAsync(new SecurityScanJob(scan.Id, clientId, scanners), ct);
        logger.LogInformation("Queued security scan {ScanId} for client {ClientId} on {Repo}",
            scan.Id, clientId, normalizedUrl);

        return new StartSecurityScanResponse(scan.Id, nameof(SecurityScanStatus.Queued));
    }

    /// <inheritdoc />
    public async Task<SecurityScanResponse?> GetScanAsync(Guid clientId, Guid scanId, CancellationToken ct)
    {
        var scan = await db.SecurityScans
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scanId && s.ClientId == clientId, ct);
        return scan is null ? null : await this.ToResponseAsync(scan, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecurityScanResponse>> ListScansAsync(
        Guid clientId, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var scans = await db.SecurityScans
            .AsNoTracking()
            .Where(s => s.ClientId == clientId)
            .OrderByDescending(s => s.QueuedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var responses = new List<SecurityScanResponse>(scans.Count);
        foreach (var scan in scans)
        {
            responses.Add(await this.ToResponseAsync(scan, ct));
        }
        return responses;
    }

    /// <inheritdoc />
    public async Task<bool> CancelScanAsync(Guid clientId, Guid scanId, CancellationToken ct)
    {
        var scan = await db.SecurityScans
            .FirstOrDefaultAsync(s => s.Id == scanId && s.ClientId == clientId, ct);
        if (scan is null)
        {
            return false;
        }

        var status = (SecurityScanStatus)scan.Status;
        if (status is SecurityScanStatus.Succeeded or SecurityScanStatus.PartiallySucceeded
            or SecurityScanStatus.Failed or SecurityScanStatus.Cancelled)
        {
            return true; // already terminal — idempotent
        }

        scan.Status = (int)SecurityScanStatus.Cancelled;
        scan.FinishedAt = DateTimeOffset.UtcNow;
        scan.ProgressPct = 100;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Cancelled security scan {ScanId}", scanId);
        return true;
    }

    /// <inheritdoc />
    public async Task<PagedSecurityFindingsResponse> GetFindingsAsync(
        Guid clientId, Guid scanId, string? severity, string? scanner, string? status,
        int limit, int offset, CancellationToken ct)
    {
        var scanExists = await db.SecurityScans
            .AsNoTracking()
            .AnyAsync(s => s.Id == scanId && s.ClientId == clientId, ct);
        if (!scanExists)
        {
            return new PagedSecurityFindingsResponse([], 0);
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        var query = db.SecurityFindings.AsNoTracking()
            .Where(f => f.ScanId == scanId && f.ClientId == clientId);

        if (Enum.TryParse<SecurityFindingSeverity>(severity, ignoreCase: true, out var sev))
        {
            query = query.Where(f => f.Severity == (int)sev);
        }
        if (!string.IsNullOrWhiteSpace(scanner))
        {
            query = query.Where(f => f.Scanner == scanner);
        }
        if (Enum.TryParse<SecurityFindingStatus>(status, ignoreCase: true, out var st))
        {
            query = query.Where(f => f.Status == (int)st);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(f => f.Severity)
            .ThenByDescending(f => f.PriorityScore)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return new PagedSecurityFindingsResponse(rows.Select(ToFindingResponse).ToList(), total);
    }

    /// <inheritdoc />
    public async Task<string?> GetSbomJsonAsync(Guid clientId, Guid scanId, CancellationToken ct)
    {
        var scan = await db.SecurityScans
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scanId && s.ClientId == clientId, ct);
        if (scan?.SbomPath is null)
        {
            return null;
        }

        // Contain the path: only files under the configured SBOM root, matching this scan's id.
        var fileName = Path.GetFileName(scan.SbomPath);
        if (!string.Equals(fileName, $"{scanId:N}.cyclonedx.json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(scan.SbomPath, ct);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SuppressFindingAsync(
        Guid clientId, Guid findingId, string reason, Guid? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A suppression reason is required.", nameof(reason));
        }

        var finding = await db.SecurityFindings
            .FirstOrDefaultAsync(f => f.Id == findingId && f.ClientId == clientId, ct);
        if (finding is null)
        {
            return false;
        }

        var newEntry = new
        {
            reason = reason.Trim(),
            byUserId = userId,
            at = DateTimeOffset.UtcNow,
        };

        // Parse-then-append instead of string surgery: TrimEnd(']') corrupts
        // payloads like "[]" into "[,{...}]" and breaks on any nested arrays.
        List<object> history;
        try
        {
            history = string.IsNullOrWhiteSpace(finding.SuppressionsJson)
                ? []
                : JsonSerializer.Deserialize<List<object>>(finding.SuppressionsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt history must not block suppression; start fresh rather
            // than failing the request or writing malformed JSON.
            history = [];
        }

        history.Add(newEntry);
        finding.SuppressionsJson = JsonSerializer.Serialize(history, JsonOptions);
        finding.Status = (int)SecurityFindingStatus.Suppressed;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AcknowledgeFindingAsync(Guid clientId, Guid findingId, CancellationToken ct)
    {
        var finding = await db.SecurityFindings
            .FirstOrDefaultAsync(f => f.Id == findingId && f.ClientId == clientId, ct);
        if (finding is null)
        {
            return false;
        }

        if ((SecurityFindingStatus)finding.Status == SecurityFindingStatus.Open)
        {
            finding.Status = (int)SecurityFindingStatus.Acknowledged;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static IReadOnlyList<string> NormalizeScanners(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return SecurityScanners.All;
        }

        var normalized = requested
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => SecurityScanners.All.Contains(s))
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                $"No valid scanners requested. Valid: {string.Join(", ", SecurityScanners.All)}.", nameof(requested));
        }

        return normalized;
    }

    private async Task<SecurityScanResponse> ToResponseAsync(SecurityScanRecord scan, CancellationToken ct)
    {
        var counts = await db.SecurityFindings
            .AsNoTracking()
            .Where(f => f.ScanId == scan.Id)
            .GroupBy(f => f.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(int s) => counts.FirstOrDefault(c => c.Severity == s)?.Count ?? 0;

        SecurityScanCoverage? coverage = null;
        if (!string.IsNullOrEmpty(scan.CoverageJson))
        {
            try { coverage = JsonSerializer.Deserialize<SecurityScanCoverage>(scan.CoverageJson); }
            catch (JsonException) { /* stored coverage unreadable — leave null */ }
        }

        return new SecurityScanResponse(
            scan.Id,
            scan.ClientId,
            scan.RepositoryUrl,
            scan.Ref,
            ((SecurityScanStatus)scan.Status).ToString(),
            scan.ProgressPct,
            scan.QueuedAt,
            scan.StartedAt,
            scan.FinishedAt,
            scan.Error,
            coverage,
            new SecurityScanSummaryResponse(
                Count((int)SecurityFindingSeverity.Critical),
                Count((int)SecurityFindingSeverity.High),
                Count((int)SecurityFindingSeverity.Medium),
                Count((int)SecurityFindingSeverity.Low),
                Count((int)SecurityFindingSeverity.Info)));
    }

    private static SecurityFindingResponse ToFindingResponse(SecurityFindingRecord f)
    {
        IReadOnlyList<string> gaps = [];
        if (!string.IsNullOrEmpty(f.ScoreGapsJson))
        {
            try { gaps = JsonSerializer.Deserialize<string[]>(f.ScoreGapsJson) ?? []; }
            catch (JsonException) { /* leave empty */ }
        }

        return new SecurityFindingResponse(
            f.Id,
            f.Scanner,
            f.RuleId,
            ((SecurityFindingSeverity)f.Severity).ToString(),
            f.PriorityScore,
            gaps,
            f.Title,
            f.Category,
            f.FilePath,
            f.Line,
            f.Evidence,
            f.Remediation,
            f.Confidence,
            ((SecurityFindingStatus)f.Status).ToString(),
            f.FirstSeenAt,
            f.LastSeenAt);
    }
}
