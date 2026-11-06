// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Persistence for explicit per-client review rules. Implementations are client-scoped: a client can only ever see its own rules.</summary>
public interface IReviewRuleStore
{
    Task<IReadOnlyList<ReviewRuleRecord>> ListAsync(Guid clientId, CancellationToken ct);
    Task<ReviewRuleRecord?> GetAsync(Guid clientId, Guid ruleId, CancellationToken ct);
    Task<ReviewRuleRecord> SaveAsync(ReviewRuleRecord record, CancellationToken ct);
    Task<bool> DeleteAsync(Guid clientId, Guid ruleId, CancellationToken ct);
}

/// <summary>
///     JSON-file review-rule store. No new database table and no EF migration: one JSON
///     file per client (<c>{clientId:N}.review-rules.json</c>) under the same root as the
///     fix-proposals store (<c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c>, defaulting to
///     <c>{WorkRoot}/aegis-fix-proposals</c>). Same durability caveat and same
///     atomic-write / corrupt-file-tolerant behaviour as the reviewer-memory store:
///     writes go through a temp file + replace under a semaphore, and a corrupt file
///     is treated as empty and logged, never allowed to fail reads.
/// </summary>
public sealed class JsonFileReviewRuleStore(
    IOptions<SecurityScanningOptions> options,
    ILogger<JsonFileReviewRuleStore> logger) : IReviewRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRuleRecord>> ListAsync(Guid clientId, CancellationToken ct)
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
    public async Task<ReviewRuleRecord?> GetAsync(Guid clientId, Guid ruleId, CancellationToken ct)
    {
        var records = await this.ListAsync(clientId, ct);
        return records.FirstOrDefault(r => r.Id == ruleId);
    }

    /// <inheritdoc />
    public async Task<ReviewRuleRecord> SaveAsync(ReviewRuleRecord record, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var records = await this.ReadAllAsync(record.ClientId, ct);
            var updated = records
                .Where(r => r.Id != record.Id)
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
    public async Task<bool> DeleteAsync(Guid clientId, Guid ruleId, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            var records = await this.ReadAllAsync(clientId, ct);
            var updated = records.Where(r => r.Id != ruleId).ToList();
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
        Path.Combine(JsonFileFixProposalStore.ResolveRoot(options.Value), $"{clientId:N}.review-rules.json");

    private async Task<IReadOnlyList<ReviewRuleRecord>> ReadAllAsync(Guid clientId, CancellationToken ct)
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
            logger.LogWarning(ex, "Could not read review rules for client {ClientId}; treating as empty.", clientId);
            return [];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReviewRuleRecord>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt file must not fail reads or block new rules; start fresh and log.
            logger.LogWarning(ex, "Review rules file for client {ClientId} is corrupt; treating as empty.", clientId);
            return [];
        }
    }

    private async Task WriteAllAsync(Guid clientId, IReadOnlyList<ReviewRuleRecord> records, CancellationToken ct)
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
