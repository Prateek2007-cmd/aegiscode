// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for merge-guard state. Implementations are client-scoped: a client can only ever see its own guard.</summary>
public interface IMergeGuardStore
{
    /// <summary>Loads the client's guard state; the default (disabled, High threshold, no checks) when nothing was ever saved.</summary>
    Task<MergeGuardStateRecord> GetStateAsync(Guid clientId, CancellationToken ct);

    /// <summary>Persists the client's guard configuration, keeping its recorded checks.</summary>
    Task<MergeGuardConfigRecord> SaveConfigAsync(Guid clientId, MergeGuardConfigRecord config, CancellationToken ct);

    /// <summary>
    ///     Records the latest check for one pull request, replacing any earlier check for the
    ///     same repository and PR number. Returns the stored record.
    /// </summary>
    Task<MergeGuardCheckRecord> SaveCheckAsync(Guid clientId, MergeGuardCheckRecord check, CancellationToken ct);
}

/// <summary>
///     JSON-file merge-guard store. No new database table and no EF migration: one JSON file
///     per client (<c>{clientId:N}.merge-guard.json</c>) under the same root as the
///     fix-proposal files (see <see cref="JsonFileFixProposalStore.ResolveRoot" />), so the
///     same persistence caveat applies — set <c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c> to a
///     persistent path on durable deployments, or the guard forgets its config and check
///     history on a restart. Writes are atomic (temp file in the same directory +
///     replace) and serialized through a semaphore. A corrupt file is treated as the
///     default state and logged, never allowed to fail reads. The check list is capped
///     at 50 entries per client, newest first, so the file cannot grow without bound.
/// </summary>
public sealed class JsonFileMergeGuardStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFileMergeGuardStore> logger) : IMergeGuardStore
{
    private const int MaxChecksPerClient = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly MergeGuardConfigRecord DefaultConfig = new(false, MergeGuardThreshold.Default);

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<MergeGuardStateRecord> GetStateAsync(Guid clientId, CancellationToken ct)
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
    public async Task<MergeGuardConfigRecord> SaveConfigAsync(Guid clientId, MergeGuardConfigRecord config, CancellationToken ct)
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
    public async Task<MergeGuardCheckRecord> SaveCheckAsync(Guid clientId, MergeGuardCheckRecord check, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var state = await this.ReadAllAsync(clientId, ct);
            var updated = state.Checks
                .Where(c => !IsSamePullRequest(c, check))
                .Prepend(check)
                .OrderByDescending(c => c.UpdatedAt)
                .Take(MaxChecksPerClient)
                .ToList();
            await this.WriteAllAsync(clientId, state with { Checks = updated }, ct);
            return check;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static bool IsSamePullRequest(MergeGuardCheckRecord left, MergeGuardCheckRecord right)
    {
        return string.Equals(left.RepositoryUrl, right.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
               && left.PullRequestNumber == right.PullRequestNumber;
    }

    private string FilePathFor(Guid clientId) =>
        Path.Combine(JsonFileFixProposalStore.ResolveRoot(options.Value), $"{clientId:N}.merge-guard.json");

    private async Task<MergeGuardStateRecord> ReadAllAsync(Guid clientId, CancellationToken ct)
    {
        var path = this.FilePathFor(clientId);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return new MergeGuardStateRecord(DefaultConfig, []);
        }
        catch (DirectoryNotFoundException)
        {
            return new MergeGuardStateRecord(DefaultConfig, []);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read merge-guard state for client {ClientId}; treating as default.", clientId);
            return new MergeGuardStateRecord(DefaultConfig, []);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new MergeGuardStateRecord(DefaultConfig, []);
        }

        try
        {
            return JsonSerializer.Deserialize<MergeGuardStateRecord>(json, JsonOptions)
                   ?? new MergeGuardStateRecord(DefaultConfig, []);
        }
        catch (JsonException ex)
        {
            // A corrupt file must not fail reads or block new checks; start fresh and log.
            logger.LogWarning(ex, "Merge-guard file for client {ClientId} is corrupt; treating as default.", clientId);
            return new MergeGuardStateRecord(DefaultConfig, []);
        }
    }

    private async Task WriteAllAsync(Guid clientId, MergeGuardStateRecord state, CancellationToken ct)
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
