// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>One explicit review rule as returned by the API, in creation order.</summary>
public sealed record ReviewRuleEntry(
    Guid Id,
    string Name,
    string Pattern,
    bool IsRegex,
    string Severity,
    string Message,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>List envelope for a client's custom review rules.</summary>
public sealed record ReviewRuleListResponse(IReadOnlyList<ReviewRuleEntry> Items);

/// <summary>Request to create a custom review rule. An invalid regex is a 400 with the real regex error, never stored.</summary>
public sealed record CreateReviewRuleRequest(
    string? Name,
    string? Pattern,
    bool IsRegex,
    string? Severity,
    string? Message);

/// <summary>Request to update a custom review rule. Every field is replaced as given; <c>Enabled</c> flips the rule on or off.</summary>
public sealed record UpdateReviewRuleRequest(
    string? Name,
    string? Pattern,
    bool IsRegex,
    string? Severity,
    string? Message,
    bool Enabled);

/// <summary>Request to enable or disable one custom review rule.</summary>
public sealed record SetReviewRuleEnabledRequest(bool Enabled);
