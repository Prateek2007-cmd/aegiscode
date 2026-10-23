// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     Defense-in-depth redaction verification. The engine redacts secret values with
///     <c>--redact</c> before they reach stdout; this verifier re-scans every persisted
///     string for shapes that look like unredacted credentials and fails the finding
///     (not the scan) if one slips through. Belt and suspenders: raw secret values must
///     never reach the database, API responses, or logs.
/// </summary>
public static partial class SecurityScanRedactor
{
    // Conservative shapes: long base64-ish tokens assigned to secret-ish keys, AWS key ids,
    // and PEM private key blocks. Intentionally broad — false positives here only cause a
    // finding to be quarantined for review, never a false finding shown to users.
    [GeneratedRegex("""(?i)(api[_-]?key|secret|passwd|password|token)\s*[:=]\s*['"]?[A-Za-z0-9_\-/+]{20,}['"]?""",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AwsKeyIdRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateKeyRegex();

    /// <summary>
    ///     Returns true when <paramref name="text" /> looks like it contains an unredacted
    ///     credential. Redacted markers (<c>***</c>, <c>REDACTED</c>) are ignored.
    /// </summary>
    public static bool LooksLikeUnredactedSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Strip the engine's own redaction markers before checking.
        var scrubbed = text.Replace("***", string.Empty, StringComparison.Ordinal)
                           .Replace("REDACTED", string.Empty, StringComparison.Ordinal);

        return CredentialAssignmentRegex().IsMatch(scrubbed)
            || AwsKeyIdRegex().IsMatch(scrubbed)
            || PrivateKeyRegex().IsMatch(scrubbed);
    }

    /// <summary>
    ///     Replaces any detected unredacted credential shape with <c>[REDACTED]</c>.
    ///     Used as a last resort before persistence — the finding is also flagged.
    /// </summary>
    public static string ForceRedact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = CredentialAssignmentRegex().Replace(text, "[REDACTED]");
        redacted = AwsKeyIdRegex().Replace(redacted, "[REDACTED]");
        redacted = PrivateKeyRegex().Replace(redacted, "[REDACTED]");
        return redacted;
    }
}
