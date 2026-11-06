// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <inheritdoc />
public sealed class ReviewerMemoryService(IReviewerMemoryStore store, ILogger<ReviewerMemoryService> logger) : IReviewerMemoryService
{
    /// <inheritdoc />
    public async Task<ReviewerMemoryEntry> RecordOutcomeAsync(
        Guid clientId,
        string ruleKey,
        string label,
        string source,
        ReviewerMemoryOutcome outcome,
        CancellationToken ct)
    {
        var key = ReviewerMemoryRules.NormalizeRuleKey(ruleKey)
                  ?? throw new ArgumentException("ruleKey must be a well-formed \"source:rule\" key.", nameof(ruleKey));
        var normalizedSource = NormalizeSource(source);
        var displayLabel = ReviewerMemoryRules.DisplayLabel(label)
                           ?? throw new ArgumentException("label is required.", nameof(label));

        var now = DateTimeOffset.UtcNow;
        var existing = await store.GetAsync(clientId, key, ct);
        var record = existing is null
            ? new ReviewerMemoryRecord(clientId, key, displayLabel, normalizedSource, 0, 0, false, null, null, now, now)
            : existing with { Label = displayLabel, Source = normalizedSource, LastOutcomeAt = now };

        record = outcome == ReviewerMemoryOutcome.Accepted
            ? record with { Accepts = record.Accepts + 1 }
            : record with { Dismissals = record.Dismissals + 1 };

        // Muting is sticky: once muted, only an explicit un-mute by the user lifts it.
        // A later accept still counts, but it never silently un-mutes the rule.
        if (!record.Muted && ReviewerMemoryRules.ShouldMute(record.Accepts, record.Dismissals))
        {
            record = record with
            {
                Muted = true,
                MutedAt = now,
                MutedReason = $"Dismissed {record.Dismissals} times with no accepts.",
            };
            logger.LogInformation(
                "Reviewer memory muted rule {RuleKey} for client {ClientId} after {Dismissals} dismissals with no accepts.",
                key, clientId, record.Dismissals);
        }

        var saved = await store.SaveAsync(record, ct);
        return ToEntry(saved);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewerMemoryEntry>> ListAsync(Guid clientId, CancellationToken ct)
    {
        var records = await store.ListAsync(clientId, ct);
        return records
            .OrderByDescending(r => r.LastOutcomeAt)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal)
            .Select(ToEntry)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReviewerMemoryEntry?> UnmuteAsync(Guid clientId, string ruleKey, CancellationToken ct)
    {
        var key = ReviewerMemoryRules.NormalizeRuleKey(ruleKey)
                  ?? throw new ArgumentException("ruleKey must be a well-formed \"source:rule\" key.", nameof(ruleKey));

        var existing = await store.GetAsync(clientId, key, ct);
        if (existing is null)
        {
            return null;
        }

        if (!existing.Muted)
        {
            return ToEntry(existing);
        }

        var unmuted = existing with { Muted = false, MutedAt = null, MutedReason = null };
        var saved = await store.SaveAsync(unmuted, ct);
        logger.LogInformation("Reviewer memory un-muted rule {RuleKey} for client {ClientId}.", key, clientId);
        return ToEntry(saved);
    }

    /// <inheritdoc />
    public async Task<bool> ResetAsync(Guid clientId, string ruleKey, CancellationToken ct)
    {
        var key = ReviewerMemoryRules.NormalizeRuleKey(ruleKey)
                  ?? throw new ArgumentException("ruleKey must be a well-formed \"source:rule\" key.", nameof(ruleKey));

        var deleted = await store.DeleteAsync(clientId, key, ct);
        if (deleted)
        {
            logger.LogInformation("Reviewer memory reset rule {RuleKey} for client {ClientId}.", key, clientId);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetMutedRuleKeysAsync(Guid clientId, CancellationToken ct)
    {
        var records = await store.ListAsync(clientId, ct);
        return records
            .Where(r => r.Muted)
            .Select(r => r.RuleKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> BuildPromptGuidanceAsync(Guid clientId, CancellationToken ct)
    {
        var records = await store.ListAsync(clientId, ct);
        return records
            .Where(r => r.Muted)
            .OrderByDescending(r => r.Dismissals)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal)
            .Take(ReviewerMemoryRules.MaxPromptGuidanceLines)
            .Select(r => ReviewerMemoryRules.GuidanceLine(r.Label, r.Dismissals))
            .ToList();
    }

    internal static string NormalizeSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            ReviewerMemoryRules.ReviewSource => ReviewerMemoryRules.ReviewSource,
            ReviewerMemoryRules.ScanSource => ReviewerMemoryRules.ScanSource,
            _ => throw new ArgumentException("source must be \"review\" or \"scan\".", nameof(source)),
        };
    }

    internal static ReviewerMemoryEntry ToEntry(ReviewerMemoryRecord record)
    {
        return new ReviewerMemoryEntry(
            record.RuleKey,
            record.Label,
            record.Source,
            record.Accepts,
            record.Dismissals,
            record.Muted,
            record.MutedAt,
            record.MutedReason,
            record.FirstSeenAt,
            record.LastOutcomeAt);
    }
}
