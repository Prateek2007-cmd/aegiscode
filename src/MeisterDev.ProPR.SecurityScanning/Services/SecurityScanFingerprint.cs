// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.SecurityScanning.Models;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     Stable finding identity across scans. The fingerprint is a SHA-256 over the
///     scanner, rule id, package@version (for dependency findings), and the redacted
///     file:line location — never over raw secret values or volatile evidence text.
///     A finding seen again keeps its identity; a missing one resolves.
/// </summary>
public static class SecurityScanFingerprint
{
    /// <summary>Computes the fingerprint for one engine finding. Deterministic.</summary>
    public static string Compute(EngineFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var sb = new StringBuilder();
        sb.Append(finding.Scanner).Append('|');
        sb.Append(finding.RuleId ?? string.Empty).Append('|');
        sb.Append(finding.PackageName ?? string.Empty).Append('@');
        sb.Append(finding.PackageVersion ?? string.Empty).Append('|');
        sb.Append(finding.FilePath ?? string.Empty).Append(':');
        sb.Append(finding.Line?.ToString() ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
