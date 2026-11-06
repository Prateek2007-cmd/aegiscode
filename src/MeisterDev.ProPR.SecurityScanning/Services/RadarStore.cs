// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for radar alerts and per-client radar state. Implementations are client-scoped.</summary>
public interface IRadarStore
{
    /// <summary>Lists the client's radar alerts. Never returns another client's alerts.</summary>
    Task<IReadOnlyList<RadarAlertRecord>> ListAlertsAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Appends new alerts, skipping any whose (vulnerability, package, version)
    ///     triple already has an alert for this client — a concurrent pass can never
    ///     create a duplicate alert. Returns the alerts actually added.
    /// </summary>
    Task<IReadOnlyList<RadarAlertRecord>> AddAlertsAsync(Guid clientId, IReadOnlyList<RadarAlertRecord> alerts, CancellationToken ct);

    /// <summary>Replaces one alert by id. Returns <c>null</c> when it does not exist for this client.</summary>
    Task<RadarAlertRecord?> UpdateAlertAsync(Guid clientId, RadarAlertRecord alert, CancellationToken ct);

    /// <summary>Reads the client's radar state, or the default state when none was saved yet.</summary>
    Task<RadarStateRecord> GetStateAsync(Guid clientId, int defaultIntervalMinutes, CancellationToken ct);

    /// <summary>Replaces the client's radar state.</summary>
    Task SaveStateAsync(Guid clientId, RadarStateRecord state, CancellationToken ct);
}

/// <summary>
///     JSON-file radar store. No new database table and no EF migration: two files
///     per client (<c>{clientId:N}.radar-alerts.json</c> and
///     <c>{clientId:N}.radar-state.json</c>) under the configured radar root
///     (<c>AEGIS_SECURITY_RADAR_ROOT</c>, defaulting to
///     <c>{WorkRoot}/aegis-radar</c>, mirroring the fix-proposals root).
///     Writes are atomic (temp file in the same directory + replace) and serialized
///     through a semaphore, so a crash mid-write cannot leave a half-written file.
///     A corrupt file is treated as empty and logged, never allowed to fail reads.
/// </summary>
public sealed class JsonFileRadarStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFileRadarStore> logger) : IRadarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarAlertRecord>> ListAlertsAsync(Guid clientId, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            return await this.ReadAlertsAsync(clientId, ct);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarAlertRecord>> AddAlertsAsync(Guid clientId, IReadOnlyList<RadarAlertRecord> alerts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        await this.gate.WaitAsync(ct);
        try
        {
            var existing = await this.ReadAlertsAsync(clientId, ct);
            var existingKeys = existing
                .Select(a => RadarDetector.ExactKey(a.VulnId, a.PackageName, a.PackageVersion))
                .ToHashSet(StringComparer.Ordinal);
            var added = alerts
                .Where(a => existingKeys.Add(RadarDetector.ExactKey(a.VulnId, a.PackageName, a.PackageVersion)))
                .ToList();
            if (added.Count > 0)
            {
                await this.WriteAlertsAsync(clientId, existing.Concat(added).ToList(), ct);
            }

            return added;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RadarAlertRecord?> UpdateAlertAsync(Guid clientId, RadarAlertRecord alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        await this.gate.WaitAsync(ct);
        try
        {
            var updated = (await this.ReadAlertsAsync(clientId, ct)).ToList();
            var index = updated.FindIndex(a => a.Id == alert.Id);
            if (index < 0)
            {
                return null;
            }

            updated[index] = alert;
            await this.WriteAlertsAsync(clientId, updated, ct);
            return alert;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RadarStateRecord> GetStateAsync(Guid clientId, int defaultIntervalMinutes, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var path = this.StatePathFor(clientId);
            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, ct);
            }
            catch (FileNotFoundException)
            {
                return DefaultState(clientId, defaultIntervalMinutes);
            }
            catch (DirectoryNotFoundException)
            {
                return DefaultState(clientId, defaultIntervalMinutes);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not read radar state for client {ClientId}; using defaults.", clientId);
                return DefaultState(clientId, defaultIntervalMinutes);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return DefaultState(clientId, defaultIntervalMinutes);
            }

            try
            {
                return JsonSerializer.Deserialize<RadarStateRecord>(json, JsonOptions)
                       ?? DefaultState(clientId, defaultIntervalMinutes);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Radar state file for client {ClientId} is corrupt; using defaults.", clientId);
                return DefaultState(clientId, defaultIntervalMinutes);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveStateAsync(Guid clientId, RadarStateRecord state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        await this.gate.WaitAsync(ct);
        try
        {
            var root = ResolveRoot(options.Value);
            Directory.CreateDirectory(root);

            var path = this.StatePathFor(clientId);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(state, JsonOptions);
            try
            {
                await File.WriteAllTextAsync(tempPath, json, ct);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                TryDeleteTemp(tempPath);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    ///     Resolves the radar root, mirroring the fix-proposals root resolution:
    ///     explicit <c>RadarRoot</c>, else <c>{WorkRoot}/aegis-radar</c>,
    ///     else <c>{temp}/aegis-radar</c>.
    /// </summary>
    internal static string ResolveRoot(SecurityScanningOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.RadarRoot))
        {
            return opts.RadarRoot;
        }

        var workRoot = string.IsNullOrWhiteSpace(opts.WorkRoot) ? Path.GetTempPath() : opts.WorkRoot;
        return Path.Combine(workRoot, "aegis-radar");
    }

    private static RadarStateRecord DefaultState(Guid clientId, int defaultIntervalMinutes) =>
        new(clientId, Enabled: true, IntervalMinutes: defaultIntervalMinutes, LastPassAt: null, LastPass: null, SeenKeys: []);

    private string AlertsPathFor(Guid clientId) =>
        Path.Combine(ResolveRoot(options.Value), $"{clientId:N}.radar-alerts.json");

    private string StatePathFor(Guid clientId) =>
        Path.Combine(ResolveRoot(options.Value), $"{clientId:N}.radar-state.json");

    private async Task<IReadOnlyList<RadarAlertRecord>> ReadAlertsAsync(Guid clientId, CancellationToken ct)
    {
        var path = this.AlertsPathFor(clientId);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read radar alerts for client {ClientId}; treating as empty.", clientId);
            return [];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RadarAlertRecord>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Radar alerts file for client {ClientId} is corrupt; treating as empty.", clientId);
            return [];
        }
    }

    private async Task WriteAlertsAsync(Guid clientId, IReadOnlyList<RadarAlertRecord> alerts, CancellationToken ct)
    {
        var root = ResolveRoot(options.Value);
        Directory.CreateDirectory(root);

        var path = this.AlertsPathFor(clientId);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(alerts, JsonOptions);
        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            /* best-effort temp cleanup */
        }
        catch (UnauthorizedAccessException)
        {
            /* best-effort temp cleanup */
        }
    }
}
