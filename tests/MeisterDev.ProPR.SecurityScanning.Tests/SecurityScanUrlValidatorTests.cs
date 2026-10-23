// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>URL validation is the SSRF boundary: only strict https github.com owner/repo URLs pass.</summary>
public sealed class SecurityScanUrlValidatorTests
{
    private static readonly string[] AllowedHosts = ["github.com"];

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://GITHUB.COM/Owner/Repo", "https://github.com/Owner/Repo")]
    [InlineData("  https://github.com/owner/repo  ", "https://github.com/owner/repo")]
    public void Accepts_valid_github_urls(string input, string expected)
    {
        Assert.Equal(expected, SecurityScanUrlValidator.ValidateAndNormalize(input, AllowedHosts));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")] // wrong scheme
    [InlineData("https://github.com/owner/repo?x=1")] // query string
    [InlineData("https://github.com/owner/repo#frag")] // fragment
    [InlineData("https://user:pass@github.com/owner/repo")] // credentials
    [InlineData("https://github.com:8443/owner/repo")] // non-default port
    [InlineData("https://gitlab.com/owner/repo")] // disallowed host
    [InlineData("https://evil-github.com/owner/repo")] // lookalike host
    [InlineData("https://github.com.evil.com/owner/repo")] // subdomain trick
    [InlineData("https://github.com/owner")] // missing repo
    [InlineData("https://github.com/")] // missing path
    [InlineData("https://github.com/owner/repo/extra/path")] // too deep
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_malformed_or_disallowed_urls(string? input)
    {
        Assert.Throws<ArgumentException>(() =>
            SecurityScanUrlValidator.ValidateAndNormalize(input, AllowedHosts));
    }

    [Theory]
    [InlineData("https://localhost/owner/repo")]
    [InlineData("https://127.0.0.1/owner/repo")]
    [InlineData("https://10.0.0.5/owner/repo")]
    [InlineData("https://192.168.1.1/owner/repo")]
    [InlineData("https://172.16.0.1/owner/repo")]
    [InlineData("https://169.254.169.254/owner/repo")] // cloud metadata
    [InlineData("https://app.internal/owner/repo")]
    public void Rejects_private_and_loopback_hosts(string input)
    {
        // Note: these hosts are not in the allowlist anyway; this pins the defense-in-depth
        // check for configurations where an internal host might be allowlisted.
        var hosts = new[] { "github.com", new Uri(input).Host };
        Assert.Throws<ArgumentException>(() =>
            SecurityScanUrlValidator.ValidateAndNormalize(input, hosts));
    }
}
