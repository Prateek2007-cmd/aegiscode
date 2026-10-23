// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>
///     SSRF-safe repository URL validation. Only https URLs on explicitly allowed hosts
///     (default: github.com) with a strict owner/repo path shape are accepted.
///     Everything else — other schemes, ports, credentials, query strings, fragments,
///     private-range hosts — is rejected before any network access happens.
/// </summary>
public static partial class SecurityScanUrlValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+?(\.git)?/?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepoPathRegex();

    /// <summary>
    ///     Validates a repository URL. Returns the normalized URL, or throws
    ///     <see cref="ArgumentException" /> with a user-safe message.
    /// </summary>
    public static string ValidateAndNormalize(string? repositoryUrl, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("Repository URL is required.", nameof(repositoryUrl));
        }

        var trimmed = repositoryUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Repository URL is not a valid absolute URL.", nameof(repositoryUrl));
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only https repository URLs are supported.", nameof(repositoryUrl));
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Repository URLs must not contain credentials.", nameof(repositoryUrl));
        }

        if (!uri.IsDefaultPort)
        {
            throw new ArgumentException("Repository URLs must not specify a port.", nameof(repositoryUrl));
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("Repository URLs must not contain query strings or fragments.", nameof(repositoryUrl));
        }

        var host = uri.Host.ToLowerInvariant();
        if (allowedHosts.All(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Repository host '{uri.Host}' is not allowed for scanning.", nameof(repositoryUrl));
        }

        // Reject private-range / loopback / link-local hosts even if allowlisted by name.
        if (IsPrivateOrLoopbackHost(host))
        {
            throw new ArgumentException("Repository host resolves to a private or loopback address.", nameof(repositoryUrl));
        }

        var path = uri.AbsolutePath.Trim('/');
        if (!RepoPathRegex().IsMatch(path))
        {
            throw new ArgumentException("Repository URL must have the form https://host/owner/repo.", nameof(repositoryUrl));
        }

        // Normalized: lowercase host, no trailing slash, no .git suffix.
        var normalizedPath = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4].TrimEnd('/')
            : path.TrimEnd('/');
        return $"https://{host}/{normalizedPath}";
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        // Hostnames first: literal IPs are checked; DNS names are resolved at clone time
        // by git, so we additionally block the well-known local names here.
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10/8, 172.16/12, 192.168/16, 169.254/16, 0/8
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0;
        }

        // IPv6: loopback already handled; block unique-local (fc00::/7) and link-local (fe80::/10).
        return (bytes[0] & 0xfe) == 0xfc || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }
}
