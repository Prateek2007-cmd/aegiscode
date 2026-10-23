// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>Redaction verification is the last line of defense before persistence.</summary>
public sealed class SecurityScanRedactorTests
{
    [Theory]
    [InlineData("api_key = \"xK9mQ2vL8nP4wR7tY3uI6oP5aS1dF0gHjKlZx\"")]
    [InlineData("AKIAQWERTYUIOPASDF23")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...")]
    [InlineData("password: hunter2supersecretvalue123")]
    public void Detects_unredacted_secrets(string text)
    {
        Assert.True(SecurityScanRedactor.LooksLikeUnredactedSecret(text));
    }

    [Theory]
    [InlineData("api_key = \"***\"")]
    [InlineData("AKIA***")]
    [InlineData("REDACTED")]
    [InlineData("no secrets here, just a lodash vulnerability description")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_redacted_or_benign_text(string? text)
    {
        Assert.False(SecurityScanRedactor.LooksLikeUnredactedSecret(text));
    }

    [Fact]
    public void ForceRedact_removes_credential_shapes()
    {
        var result = SecurityScanRedactor.ForceRedact("leaked AKIAQWERTYUIOPASDF23 in logs");
        Assert.DoesNotContain("AKIAQWERTYUIOPASDF23", result);
        Assert.Contains("[REDACTED]", result);
    }
}
