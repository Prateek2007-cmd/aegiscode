// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>One vulnerability occurrence OSV reports for one package version.</summary>
public sealed record RadarVulnOccurrence(
    string VulnId,
    string PackageName,
    string PackageVersion,
    string Ecosystem,
    string Severity,
    string? Summary,
    string? FixedVersion,
    string? CveId);

/// <summary>
///     Pure new-vulnerability detection for the radar. No I/O, no dates, no
///     guessing: a fresh OSV occurrence is new only when neither its exact key
///     (<c>{vulnId}|{package}@{version}</c>) nor its coarse key
///     (<c>{vulnId}</c>) is in the known set. The coarse key exists because
///     past scan findings carry the vulnerability id but not always a
///     machine-readable package version; the exact key is preferred whenever
///     the version is known.
/// </summary>
public static partial class RadarDetector
{
    /// <summary>Exact ledger key: vulnerability + package + version.</summary>
    public static string ExactKey(string vulnId, string packageName, string packageVersion) =>
        $"{vulnId}|{packageName}@{packageVersion}";

    /// <summary>Only Critical and High vulnerabilities raise alerts. Medium and below are still recorded as seen.</summary>
    public static bool IsAlertSeverity(string severity) =>
        string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase)
        || string.Equals(severity, "High", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sort order for severities: lower is worse. Unknown severities sort last, never first.</summary>
    public static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        "info" => 4,
        _ => 5,
    };

    /// <summary>Returns the fresh occurrences that are not in the known set, in severity order.</summary>
    public static IReadOnlyList<RadarVulnOccurrence> DetectNew(
        IReadOnlyList<RadarVulnOccurrence> fresh,
        IReadOnlySet<string> knownKeys)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(knownKeys);

        var freshNew = fresh
            .Where(o => !IsKnown(o.VulnId, o.PackageName, o.PackageVersion, knownKeys))
            .OrderBy(o => SeverityRank(o.Severity))
            .ThenBy(o => o.VulnId, StringComparer.Ordinal)
            .ToList();
        return freshNew;
    }

    /// <summary>Known when the exact key or the coarse vulnerability-id key is present.</summary>
    public static bool IsKnown(string vulnId, string packageName, string packageVersion, IReadOnlySet<string> knownKeys) =>
        knownKeys.Contains(ExactKey(vulnId, packageName, packageVersion))
        || knownKeys.Contains(vulnId);

    /// <summary>
    ///     Builds the known set from past dep-scan findings. Each finding contributes
    ///     an exact key when its evidence parses as
    ///     <c>{package}@{version} is affected by {vulnId}</c> and the parsed id
    ///     matches the finding's rule id; otherwise it contributes only the coarse
    ///     rule-id key. Findings without a rule id contribute nothing.
    /// </summary>
    public static HashSet<string> BuildKnownKeys(IEnumerable<(string? RuleId, string? Evidence)> depScanFindings)
    {
        ArgumentNullException.ThrowIfNull(depScanFindings);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ruleId, evidence) in depScanFindings)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                continue;
            }

            var trimmedRuleId = ruleId.Trim();
            if (TryParseEvidence(evidence, out var parsed)
                && string.Equals(parsed.VulnId, trimmedRuleId, StringComparison.Ordinal))
            {
                keys.Add(ExactKey(parsed.VulnId, parsed.PackageName, parsed.PackageVersion));
            }

            // Coarse key always: a finding whose evidence does not parse must still
            // suppress re-alerts for the vulnerability it already reported.
            keys.Add(trimmedRuleId);
        }

        return keys;
    }

    /// <summary>
    ///     Parses dep-scan evidence of the form
    ///     <c>{package}@{version} is affected by {vulnId}</c>. The package name is
    ///     greedy so scoped names (<c>@types/node@18.0.0</c>) parse correctly.
    /// </summary>
    internal static bool TryParseEvidence(
        string? evidence,
        out (string PackageName, string PackageVersion, string VulnId) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        var match = EvidenceRegex().Match(evidence);
        if (!match.Success)
        {
            return false;
        }

        parsed = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        return true;
    }

    [GeneratedRegex(@"^(.+)@([0-9][^\s]*) is affected by (\S+)", RegexOptions.None)]
    private static partial Regex EvidenceRegex();
}
