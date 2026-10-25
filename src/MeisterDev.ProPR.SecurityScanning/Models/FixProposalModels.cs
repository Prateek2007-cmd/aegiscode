// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     Lifecycle of a fix proposal. A proposal sits in <see cref="FixProposalStatus.PendingApproval" />
///     until a human approves or rejects it — that decision is the human-approval gate for fixes.
///     Nothing in this API creates a GitHub pull request or applies code changes.
/// </summary>
public enum FixProposalStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>Known proposal sources. A proposal is rooted in either a security-scan finding or a review comment.</summary>
public static class FixProposalSources
{
    public const string Scan = "scan";
    public const string Review = "review";

    public static readonly string[] All = [Scan, Review];

    /// <summary>Normalizes a caller-supplied source to lowercase, or <c>null</c> when it is not a known source.</summary>
    public static string? Normalize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var normalized = source.Trim().ToLowerInvariant();
        return All.Contains(normalized) ? normalized : null;
    }
}

/// <summary>
///     One fix proposal as persisted in the per-client JSON store. Kept separate from the
///     response DTO so storage can evolve without changing the API contract.
/// </summary>
public sealed record FixProposalRecord(
    Guid Id,
    Guid ClientId,
    string FindingRef,
    string Source,
    string Title,
    string? FilePath,
    int? Line,
    string? Remediation,
    string? Confidence,
    FixProposalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecisionReason);
