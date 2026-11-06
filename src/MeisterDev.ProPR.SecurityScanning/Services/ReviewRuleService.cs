// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     CRUD for explicit per-client review rules, plus the enforcement read side
///     (<see cref="ICustomReviewRuleService" />). Validation is fail-closed: a missing
///     name, pattern, or message, an unknown severity, or a regex that does not compile
///     is an <see cref="ArgumentException" /> carrying the real reason — the controller
///     turns it into a 400 and nothing invalid is ever stored.
/// </summary>
public interface IReviewRuleService : ICustomReviewRuleService
{
    Task<IReadOnlyList<ReviewRuleEntry>> ListAsync(Guid clientId, CancellationToken ct);
    Task<ReviewRuleEntry> CreateAsync(Guid clientId, string? name, string? pattern, bool isRegex, string? severity, string? message, CancellationToken ct);

    Task<ReviewRuleEntry?> UpdateAsync(
        Guid clientId, Guid ruleId, string? name, string? pattern, bool isRegex, string? severity, string? message, bool enabled, CancellationToken ct);

    Task<ReviewRuleEntry?> SetEnabledAsync(Guid clientId, Guid ruleId, bool enabled, CancellationToken ct);
    Task<bool> DeleteAsync(Guid clientId, Guid ruleId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ReviewRuleService(IReviewRuleStore store, ILogger<ReviewRuleService> logger) : IReviewRuleService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRuleEntry>> ListAsync(Guid clientId, CancellationToken ct)
    {
        var records = await store.ListAsync(clientId, ct);
        return records
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToEntry)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CustomReviewRule>> ListEnabledAsync(Guid clientId, CancellationToken ct)
    {
        var records = await store.ListAsync(clientId, ct);
        return records
            .Where(r => r.Enabled)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new CustomReviewRule(r.Id, r.Name, r.Pattern, r.IsRegex, r.Severity, r.Message, r.Enabled))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReviewRuleEntry> CreateAsync(
        Guid clientId, string? name, string? pattern, bool isRegex, string? severity, string? message, CancellationToken ct)
    {
        var validated = Validate(name, pattern, isRegex, severity, message);
        var now = DateTimeOffset.UtcNow;
        var record = new ReviewRuleRecord(
            Guid.NewGuid(), clientId, validated.Name, validated.Pattern, isRegex,
            validated.Severity, validated.Message, Enabled: true, now, now);
        var saved = await store.SaveAsync(record, ct);
        logger.LogInformation("Created custom review rule {RuleId} ({RuleName}) for client {ClientId}.", saved.Id, saved.Name, clientId);
        return ToEntry(saved);
    }

    /// <inheritdoc />
    public async Task<ReviewRuleEntry?> UpdateAsync(
        Guid clientId, Guid ruleId, string? name, string? pattern, bool isRegex, string? severity, string? message, bool enabled, CancellationToken ct)
    {
        var existing = await store.GetAsync(clientId, ruleId, ct);
        if (existing is null)
        {
            return null;
        }

        var validated = Validate(name, pattern, isRegex, severity, message);
        var updated = existing with
        {
            Name = validated.Name,
            Pattern = validated.Pattern,
            IsRegex = isRegex,
            Severity = validated.Severity,
            Message = validated.Message,
            Enabled = enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var saved = await store.SaveAsync(updated, ct);
        logger.LogInformation("Updated custom review rule {RuleId} ({RuleName}) for client {ClientId}.", saved.Id, saved.Name, clientId);
        return ToEntry(saved);
    }

    /// <inheritdoc />
    public async Task<ReviewRuleEntry?> SetEnabledAsync(Guid clientId, Guid ruleId, bool enabled, CancellationToken ct)
    {
        var existing = await store.GetAsync(clientId, ruleId, ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.Enabled == enabled)
        {
            return ToEntry(existing);
        }

        var updated = existing with { Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow };
        var saved = await store.SaveAsync(updated, ct);
        logger.LogInformation(
            "{Action} custom review rule {RuleId} ({RuleName}) for client {ClientId}.",
            enabled ? "Enabled" : "Disabled", saved.Id, saved.Name, clientId);
        return ToEntry(saved);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid clientId, Guid ruleId, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(clientId, ruleId, ct);
        if (deleted)
        {
            logger.LogInformation("Deleted custom review rule {RuleId} for client {ClientId}.", ruleId, clientId);
        }

        return deleted;
    }

    internal static (string Name, string Pattern, string Severity, string Message) Validate(
        string? name, string? pattern, bool isRegex, string? severity, string? message)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new ArgumentException("name is required.", nameof(name));
        }

        if (cleanName.Length > CustomReviewRules.MaxNameLength)
        {
            throw new ArgumentException($"name must be at most {CustomReviewRules.MaxNameLength} characters.", nameof(name));
        }

        var cleanPattern = pattern?.Trim();
        if (!CustomReviewRules.TryValidatePattern(cleanPattern, isRegex, out var patternError))
        {
            throw new ArgumentException(patternError, nameof(pattern));
        }

        if (!CustomReviewRules.TryParseSeverity(severity, out var parsedSeverity))
        {
            throw new ArgumentException("severity must be \"Error\", \"Warning\", \"Suggestion\", or \"Info\".", nameof(severity));
        }

        var cleanMessage = message?.Trim();
        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            throw new ArgumentException("message is required.", nameof(message));
        }

        if (cleanMessage.Length > CustomReviewRules.MaxMessageLength)
        {
            throw new ArgumentException($"message must be at most {CustomReviewRules.MaxMessageLength} characters.", nameof(message));
        }

        return (cleanName, cleanPattern!, parsedSeverity.ToString(), cleanMessage);
    }

    internal static ReviewRuleEntry ToEntry(ReviewRuleRecord record)
    {
        return new ReviewRuleEntry(
            record.Id,
            record.Name,
            record.Pattern,
            record.IsRegex,
            record.Severity,
            record.Message,
            record.Enabled,
            record.CreatedAt,
            record.UpdatedAt);
    }
}
