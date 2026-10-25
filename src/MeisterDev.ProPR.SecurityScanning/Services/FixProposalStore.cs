// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for fix proposals. Implementations are client-scoped: a client can only ever see its own proposals.</summary>
public interface IFixProposalStore
{
    Task<IReadOnlyList<FixProposalRecord>> ListAsync(Guid clientId, CancellationToken ct);
    Task<FixProposalRecord?> GetAsync(Guid clientId, Guid proposalId, CancellationToken ct);
    Task<FixProposalRecord> AddAsync(FixProposalRecord proposal, CancellationToken ct);
    Task<FixProposalRecord> UpdateAsync(FixProposalRecord proposal, CancellationToken ct);
}

/// <summary>
///     JSON-file fix-proposal store. No new database table and no EF migration: one JSON file
///     per client (<c>{clientId:N}.fix-proposals.json</c>) under the configured proposals root
///     (<c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c>, defaulting to <c>{WorkRoot}/aegis-fix-proposals</c>,
///     mirroring the SBOM-root pattern). Proposals survive an API restart because they live on
///     disk, not in memory — but only if the root itself is on persistent storage: the default
///     work root is the OS temp directory, so durable deployments must set the env var.
///     Writes are atomic (temp file in the same directory + replace) and serialized through a
///     semaphore, so a crash mid-write cannot leave a half-written file. A corrupt file is
///     treated as empty and logged, never allowed to fail reads — the same tolerance the
///     suppression-history JSON uses. Cross-process writers are last-writer-wins; the API
///     runs a single writer per deployment.
/// </summary>
public sealed class JsonFileFixProposalStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFileFixProposalStore> logger) : IFixProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FixProposalRecord>> ListAsync(Guid clientId, CancellationToken ct)
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
    public async Task<FixProposalRecord?> GetAsync(Guid clientId, Guid proposalId, CancellationToken ct)
    {
        var proposals = await this.ListAsync(clientId, ct);
        return proposals.FirstOrDefault(p => p.Id == proposalId);
    }

    /// <inheritdoc />
    public async Task<FixProposalRecord> AddAsync(FixProposalRecord proposal, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var proposals = await this.ReadAllAsync(proposal.ClientId, ct);
            var updated = proposals.Append(proposal).ToList();
            await this.WriteAllAsync(proposal.ClientId, updated, ct);
            return proposal;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<FixProposalRecord> UpdateAsync(FixProposalRecord proposal, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var proposals = await this.ReadAllAsync(proposal.ClientId, ct);
            var updated = proposals.Select(p => p.Id == proposal.Id ? proposal : p).ToList();
            await this.WriteAllAsync(proposal.ClientId, updated, ct);
            return proposal;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    ///     Resolves the proposals root, mirroring the worker's SBOM-root resolution:
    ///     explicit <c>FixProposalsRoot</c>, else <c>{WorkRoot}/aegis-fix-proposals</c>,
    ///     else <c>{temp}/aegis-fix-proposals</c>.
    /// </summary>
    internal static string ResolveRoot(SecurityScanningOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.FixProposalsRoot))
        {
            return opts.FixProposalsRoot;
        }

        var workRoot = string.IsNullOrWhiteSpace(opts.WorkRoot) ? Path.GetTempPath() : opts.WorkRoot;
        return Path.Combine(workRoot, "aegis-fix-proposals");
    }

    private string FilePathFor(Guid clientId) =>
        Path.Combine(ResolveRoot(options.Value), $"{clientId:N}.fix-proposals.json");

    private async Task<IReadOnlyList<FixProposalRecord>> ReadAllAsync(Guid clientId, CancellationToken ct)
    {
        var path = this.FilePathFor(clientId);
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
            logger.LogWarning(ex, "Could not read fix proposals for client {ClientId}; treating as empty.", clientId);
            return [];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FixProposalRecord>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt file must not fail reads or block new proposals; start fresh and log.
            logger.LogWarning(ex, "Fix proposals file for client {ClientId} is corrupt; treating as empty.", clientId);
            return [];
        }
    }

    private async Task WriteAllAsync(Guid clientId, IReadOnlyList<FixProposalRecord> proposals, CancellationToken ct)
    {
        var root = ResolveRoot(options.Value);
        Directory.CreateDirectory(root);

        var path = this.FilePathFor(clientId);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(proposals, JsonOptions);

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
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }
}
