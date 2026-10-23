// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     Explainable, deterministic priority scoring. Source severity is preserved as-is;
///     the priority score is AegisCode's own ordering signal, computed only from
///     evidence — never invented. Every missing input is named in <c>ScoreGaps</c>.
/// </summary>
public static class SecurityScanPrioritizer
{
    /// <summary>Computes (priority 0–100, gaps) for one engine finding.</summary>
    public static (int Score, IReadOnlyList<string> Gaps) Score(EngineFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var gaps = new List<string>();
        var score = finding.Severity.ToLowerInvariant() switch
        {
            "critical" => 90,
            "high" => 75,
            "medium" => 50,
            "low" => 25,
            _ => 10,
        };

        // A known fixed version with a concrete upgrade path is the most actionable signal.
        if (!string.IsNullOrWhiteSpace(finding.FixedVersion))
        {
            score += 15;
        }
        else
        {
            gaps.Add("no fixed version published");
        }

        // Secret exposure outranks most dependency issues: leaked credentials are
        // actionable immediately (rotate/revoke) regardless of CVE machinery.
        if (string.Equals(finding.Scanner, SecurityScanners.SecretScan, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(finding.Confidence, "high", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else
            {
                gaps.Add("secret confidence is not high");
            }
        }

        if (string.Equals(finding.Confidence, "low", StringComparison.OrdinalIgnoreCase))
        {
            score -= 10;
        }

        // We do not have reachability or exploit-maturity data from the current engine.
        gaps.Add("no reachability evidence");
        gaps.Add("no exploit-maturity data");

        return (Math.Clamp(score, 0, 100), gaps);
    }
}
