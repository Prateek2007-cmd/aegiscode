// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for push-scan state. Implementations are client-scoped: a client can only ever see its own push scans.</summary>
public interface IPushScanStore
{
    /// <summary>Loads the client's push-scan state; the default (enabled, main only, no scans) when nothing was ever saved.</summary>
    Task<PushScanStateRecord> GetStateAsync(Guid clientId, CancellationToken ct);

    /// <summary>Persists the client's push-scan configuration, keeping its recorded scan attempts.</summary>
    Task<PushScanConfigRecord> SaveConfigAsync(Guid clientId, PushScanConfigRecord config, CancellationToken ct);

    /// <summary>
    ///     Reserves one (repository, commit SHA) for scanning by recording a pending
    ///     attempt, and returns it. Returns <c>null</c> when the same repository and
    ///     commit already has a pending (younger than the reservation window) or
    ///     started attempt — the caller must then report "already scanned" and start
    ///     nothing. A failed attempt never reserves, so the same commit can be
    ///     retried after a failed start.
    /// </summary>
    Task<PushScanEntryRecord?> TryReserveAsync(Guid clientId, PushScanEntryRecord pending, CancellationToken ct);

    /// <summary>
    ///     Replaces the pending attempt for one (repository, commit SHA) with its
    ///     final outcome (started with a scan id, or failed with the reason).
    ///     Returns the stored record.
    /// </summary>
    Task<PushScanEntryRecord> UpdateEntryAsync(Guid clientId, PushScanEntryRecord entry, CancellationToken ct);
}

/// <summary>
///     JSON-file push-scan store. No new database table and no EF migration: one JSON
///     file per client (<c>{clientId:N}.push-scans.json</c>) under the same root as
///     the fix-proposal files (see <see cref="JsonFileFixProposalStore.ResolveRoot" />),
///     so the same persistence caveat applies — set
///     <c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c> to a persistent path on durable
///     deployments, or push-scan config and history are forgotten on a restart.
///     Writes are atomic (temp file in the same directory + replace) and serialized
///     through a semaphore, which is what makes the reserve check-then-write
///     atomic across concurrent deliveries. A corrupt file is treated as the
///     default state and logged, never allowed to fail reads. Attempts are capped
///     at 50 per client, newest first.
/// </summary>
public sealed class JsonFilePushScanStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFilePushScanStore> logger) : IPushScanStore
{
    private const int MaxEntriesPerClient = 50;

    /// <summary>How long a pending reservation blocks a duplicate push for the same commit.</summary>
    internal static readonly TimeSpan PendingReservationWindow = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly PushScanConfigRecord DefaultConfig = new(true, ["main"]);

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<PushScanStateRecord> GetStateAsync(Guid clientId, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            return await this.ReadAllAsync(clientId, ct);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PushScanConfigRecord> SaveConfigAsync(Guid clientId, PushScanConfigRecord config, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var state = await this.ReadAllAsync(clientId, ct);
            var updated = state with { Config = config };
            await this.WriteAllAsync(clientId, updated, ct);
            return config;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PushScanEntryRecord?> TryReserveAsync(Guid clientId, PushScanEntryRecord pending, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var state = await this.ReadAllAsync(clientId, ct);
            var now = DateTimeOffset.UtcNow;
            var blocking = state.Entries.FirstOrDefault(e =>
                IsSameCommit(e, pending)
                && (string.Equals(e.Outcome, "started", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(e.Outcome, "pending", StringComparison.OrdinalIgnoreCase)
                        && now - e.UpdatedAt < PendingReservationWindow)));
            if (blocking is not null)
            {
                return null;
            }

            // A stale pending reservation for the same commit is dead weight: drop it
            // so the new attempt is the one the dashboard shows.
            var updated = state.Entries
                .Where(e => !(IsSameCommit(e, pending)
                              && string.Equals(e.Outcome, "pending", StringComparison.OrdinalIgnoreCase)))
                .Prepend(pending)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(MaxEntriesPerClient)
                .ToList();
            await this.WriteAllAsync(clientId, state with { Entries = updated }, ct);
            return pending;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PushScanEntryRecord> UpdateEntryAsync(Guid clientId, PushScanEntryRecord entry, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var state = await this.ReadAllAsync(clientId, ct);
            var updated = state.Entries
                .Where(e => !(IsSameCommit(e, entry)
                              && string.Equals(e.Outcome, "pending", StringComparison.OrdinalIgnoreCase)))
                .Prepend(entry)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(MaxEntriesPerClient)
                .ToList();
            await this.WriteAllAsync(clientId, state with { Entries = updated }, ct);
            return entry;
        }
        finally
        {
            this.gate.Release();
        }
    }

    internal static bool IsSameCommit(PushScanEntryRecord left, PushScanEntryRecord right)
    {
        return string.Equals(left.RepositoryUrl, right.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.HeadSha, right.HeadSha, StringComparison.OrdinalIgnoreCase);
    }

    private string FilePathFor(Guid clientId) =>
        Path.Combine(JsonFileFixProposalStore.ResolveRoot(options.Value), $"{clientId:N}.push-scans.json");

    private async Task<PushScanStateRecord> ReadAllAsync(Guid clientId, CancellationToken ct)
    {
        var path = this.FilePathFor(clientId);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return new PushScanStateRecord(DefaultConfig, []);
        }
        catch (DirectoryNotFoundException)
        {
            return new PushScanStateRecord(DefaultConfig, []);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read push-scan state for client {ClientId}; treating as default.", clientId);
            return new PushScanStateRecord(DefaultConfig, []);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new PushScanStateRecord(DefaultConfig, []);
        }

        try
        {
            return JsonSerializer.Deserialize<PushScanStateRecord>(json, JsonOptions)
                   ?? new PushScanStateRecord(DefaultConfig, []);
        }
        catch (JsonException ex)
        {
            // A corrupt file must not fail reads or block new scans; start fresh and log.
            logger.LogWarning(ex, "Push-scan file for client {ClientId} is corrupt; treating as default.", clientId);
            return new PushScanStateRecord(DefaultConfig, []);
        }
    }

    private async Task WriteAllAsync(Guid clientId, PushScanStateRecord state, CancellationToken ct)
    {
        var root = JsonFileFixProposalStore.ResolveRoot(options.Value);
        Directory.CreateDirectory(root);

        var path = this.FilePathFor(clientId);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);

        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
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
}
