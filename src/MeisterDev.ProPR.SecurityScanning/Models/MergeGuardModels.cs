// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>
///     The severity a finding must reach to block a merge. Stored as a severity name so the
///     JSON stays readable; <c>Info</c> is deliberately not a valid threshold — blocking a
///     merge on informational findings would be noise, not safety.
/// </summary>
public static class MergeGuardThreshold
{
    public static readonly string[] All = ["Critical", "High", "Medium", "Low"];

    /// <summary>The threshold for a client that never configured one: High (Critical + High block).</summary>
    public const string Default = "High";

    /// <summary>Normalizes a caller-supplied threshold, or <c>null</c> when it is not a known threshold.</summary>
    public static SecurityFindingSeverity? Normalize(string? threshold)
    {
        if (string.IsNullOrWhiteSpace(threshold))
        {
            return null;
        }

        if (!Enum.TryParse<SecurityFindingSeverity>(threshold.Trim(), ignoreCase: true, out var severity))
        {
            return null;
        }

        return severity is SecurityFindingSeverity.Critical
            or SecurityFindingSeverity.High
            or SecurityFindingSeverity.Medium
            or SecurityFindingSeverity.Low
            ? severity
            : null;
    }
}

/// <summary>
///     Per-client merge-guard configuration as persisted in the JSON store. Kept separate
///     from the response DTO so storage can evolve without changing the API contract.
/// </summary>
public sealed record MergeGuardConfigRecord(bool Enabled, string Threshold);

/// <summary>
///     The latest check the guard reported for one pull request, as persisted in the
///     JSON store. The stored <c>CheckRunId</c> is what lets a later evaluation for the
///     same head update the check run instead of opening a second one; the stored
///     conclusion and reason are what the dashboard shows without another GitHub read.
/// </summary>
public sealed record MergeGuardCheckRecord(
    string RepositoryUrl,
    int PullRequestNumber,
    string HeadSha,
    long? CheckRunId,
    string? CheckRunUrl,
    string Conclusion,
    string Title,
    string Reason,
    int BlockingScanFindings,
    int BlockingReviewFindings,
    DateTimeOffset UpdatedAt,
    bool Posted,
    string? PostError);

/// <summary>One client's whole merge-guard file: its config plus its latest checks, newest first.</summary>
public sealed record MergeGuardStateRecord(
    MergeGuardConfigRecord Config,
    IReadOnlyList<MergeGuardCheckRecord> Checks);
