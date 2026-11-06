// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for learned reviewer-memory rules. Implementations are client-scoped: a client can only ever see its own rules.</summary>
public interface IReviewerMemoryStore
{
    Task<IReadOnlyList<ReviewerMemoryRecord>> ListAsync(Guid clientId, CancellationToken ct);
    Task<ReviewerMemoryRecord?> GetAsync(Guid clientId, string ruleKey, CancellationToken ct);
    Task<ReviewerMemoryRecord> SaveAsync(ReviewerMemoryRecord record, CancellationToken ct);
    Task<bool> DeleteAsync(Guid clientId, string ruleKey, CancellationToken ct);
}

/// <summary>
///     JSON-file reviewer-memory store. No new database table and no EF migration: one JSON file
///     per client (<c>{clientId:N}.reviewer-memory.json</c>) under the same root as the
///     fix-proposals store (<c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c>, defaulting to
///     <c>{WorkRoot}/aegis-fix-proposals</c>). The same durability caveat applies: the
///     default work root is the OS temp directory, so durable deployments must set the env var.
///     Writes are atomic (temp file in the same directory + replace) and serialized through a
///     semaphore, so a crash mid-write cannot leave a half-written file. A corrupt file is
///     treated as empty and logged, never allowed to fail reads — the same tolerance the
///     fix-proposals store uses. Cross-process writers are last-writer-wins; the API runs a
///     single writer per deployment.
/// </summary>
public sealed class JsonFileReviewerMemoryStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFileReviewerMemoryStore> logger) : IReviewerMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewerMemoryRecord>> ListAsync(Guid clientId, CancellationToken ct)
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
    public async Task<ReviewerMemoryRecord?> GetAsync(Guid clientId, string ruleKey, CancellationToken ct)
    {
        var records = await this.ListAsync(clientId, ct);
        return records.FirstOrDefault(r => string.Equals(r.RuleKey, ruleKey, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<ReviewerMemoryRecord> SaveAsync(ReviewerMemoryRecord record, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var records = await this.ReadAllAsync(record.ClientId, ct);
            var updated = records
                .Where(r => !string.Equals(r.RuleKey, record.RuleKey, StringComparison.Ordinal))
                .Append(record)
                .ToList();
            await this.WriteAllAsync(record.ClientId, updated, ct);
            return record;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid clientId, string ruleKey, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var records = await this.ReadAllAsync(clientId, ct);
            var updated = records
                .Where(r => !string.Equals(r.RuleKey, ruleKey, StringComparison.Ordinal))
                .ToList();
            if (updated.Count == records.Count)
            {
                return false;
            }

            await this.WriteAllAsync(clientId, updated, ct);
            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private string FilePathFor(Guid clientId) =>
        Path.Combine(JsonFileFixProposalStore.ResolveRoot(options.Value), $"{clientId:N}.reviewer-memory.json");

    private async Task<IReadOnlyList<ReviewerMemoryRecord>> ReadAllAsync(Guid clientId, CancellationToken ct)
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
            logger.LogWarning(ex, "Could not read reviewer memory for client {ClientId}; treating as empty.", clientId);
            return [];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReviewerMemoryRecord>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt file must not fail reads or block new learning; start fresh and log.
            logger.LogWarning(ex, "Reviewer memory file for client {ClientId} is corrupt; treating as empty.", clientId);
            return [];
        }
    }

    private async Task WriteAllAsync(Guid clientId, IReadOnlyList<ReviewerMemoryRecord> records, CancellationToken ct)
    {
        var root = JsonFileFixProposalStore.ResolveRoot(options.Value);
        Directory.CreateDirectory(root);

        var path = this.FilePathFor(clientId);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(records, JsonOptions);

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
