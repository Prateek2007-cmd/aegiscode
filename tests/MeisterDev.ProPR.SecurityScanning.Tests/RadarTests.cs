// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the Dependency Radar: new-CVE detection against the known
///     set, OSV severity/fixed-version mapping, SBOM parsing, the JSON alert/
///     state store, and one end-to-end pass through <see cref="RadarService"/>
///     with an in-memory database and a stubbed OSV client (no HTTP, no real
///     OSV data). Tests run against fresh temp directories per test.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class RadarTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-radar-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempRoot))
            {
                Directory.Delete(this.tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            /* best-effort cleanup */
        }
        catch (UnauthorizedAccessException)
        {
            /* best-effort cleanup */
        }
    }

    // --- Detector: new vs known -------------------------------------------------

    [Fact]
    public void DetectNew_FreshCve_NotInKnownSet_IsReturned()
    {
        var fresh = new[]
        {
            new RadarVulnOccurrence("GHSA-new1", "lodash", "4.17.21", "npm", "Critical", "Bad", "4.17.22", "CVE-2026-0001"),
        };

        var found = RadarDetector.DetectNew(fresh, new HashSet<string>());

        Assert.Single(found);
        Assert.Equal("GHSA-new1", found[0].VulnId);
    }

    [Fact]
    public void DetectNew_AlreadyKnownExactKey_IsIgnored()
    {
        var fresh = new[]
        {
            new RadarVulnOccurrence("GHSA-old1", "lodash", "4.17.20", "npm", "High", "Old", null, null),
        };
        var known = new HashSet<string> { RadarDetector.ExactKey("GHSA-old1", "lodash", "4.17.20") };

        Assert.Empty(RadarDetector.DetectNew(fresh, known));
    }

    [Fact]
    public void DetectNew_CoarseKeySuppressesAnyVersion()
    {
        // A finding whose evidence did not parse contributes only its rule id;
        // the same CVE on a different version is still treated as known.
        var fresh = new[]
        {
            new RadarVulnOccurrence("GHSA-old1", "lodash", "4.17.21", "npm", "High", "Old", null, null),
        };
        var known = new HashSet<string> { "GHSA-old1" };

        Assert.Empty(RadarDetector.DetectNew(fresh, known));
    }

    [Fact]
    public void DetectNew_NothingChanged_ReturnsEmpty()
    {
        var fresh = new[]
        {
            new RadarVulnOccurrence("GHSA-old1", "lodash", "4.17.20", "npm", "High", "Old", null, null),
            new RadarVulnOccurrence("GHSA-old2", "express", "4.18.2", "npm", "Medium", "Old", null, null),
        };
        var known = new HashSet<string>
        {
            RadarDetector.ExactKey("GHSA-old1", "lodash", "4.17.20"),
            RadarDetector.ExactKey("GHSA-old2", "express", "4.18.2"),
        };

        Assert.Empty(RadarDetector.DetectNew(fresh, known));
    }

    [Fact]
    public void DetectNew_SameCveDifferentVersion_IsNew()
    {
        // Exact keys are version-precise: the same CVE on a version the radar
        // has never seen for that package is new.
        var fresh = new[]
        {
            new RadarVulnOccurrence("GHSA-old1", "lodash", "4.17.21", "npm", "High", "Old", null, null),
        };
        var known = new HashSet<string> { RadarDetector.ExactKey("GHSA-old1", "lodash", "4.17.20") };

        Assert.Single(RadarDetector.DetectNew(fresh, known));
    }

    [Fact]
    public void IsAlertSeverity_OnlyCriticalAndHigh()
    {
        Assert.True(RadarDetector.IsAlertSeverity("Critical"));
        Assert.True(RadarDetector.IsAlertSeverity("High"));
        Assert.False(RadarDetector.IsAlertSeverity("Medium"));
        Assert.False(RadarDetector.IsAlertSeverity("Low"));
        Assert.False(RadarDetector.IsAlertSeverity("Info"));
    }

    [Fact]
    public void BuildKnownKeys_EvidenceParsesToExactKeyPlusCoarse()
    {
        var keys = RadarDetector.BuildKnownKeys(
            new (string?, string?)[]
            {
                ("GHSA-old1", "lodash@4.17.20 is affected by GHSA-old1 (CVE-2021-12345). Some details."),
            });

        Assert.Contains(RadarDetector.ExactKey("GHSA-old1", "lodash", "4.17.20"), keys);
        Assert.Contains("GHSA-old1", keys);
    }

    [Fact]
    public void BuildKnownKeys_ScopedPackageParses()
    {
        var keys = RadarDetector.BuildKnownKeys(
            new (string?, string?)[]
            {
                ("GHSA-scoped", "@types/node@18.0.0 is affected by GHSA-scoped."),
            });

        Assert.Contains(RadarDetector.ExactKey("GHSA-scoped", "@types/node", "18.0.0"), keys);
    }

    [Fact]
    public void BuildKnownKeys_UnparseableEvidence_FallsBackToCoarseKeyOnly()
    {
        var keys = RadarDetector.BuildKnownKeys(
            new (string?, string?)[]
            {
                ("GHSA-mystery", "some free-form evidence without the expected shape"),
                (null, "lodash@4.17.20 is affected by GHSA-norule."),
            });

        Assert.DoesNotContain(RadarDetector.ExactKey("GHSA-mystery", "lodash", "4.17.20"), keys);
        Assert.Contains("GHSA-mystery", keys);
        Assert.DoesNotContain("GHSA-norule", keys);
    }

    // --- OSV severity / detail parsing ------------------------------------------

    [Fact]
    public void OsvSeverity_DatabaseSpecificWinsOverCvss()
    {
        var detail = new OsvVulnDetail("GHSA-x", null, [], [new OsvSeverityEntry("CVSS_V3", "9.8 CVSS:3.1/AV:N")], "low", []);
        var (severity, confidence) = OsvSeverity.Map(detail);

        Assert.Equal("Low", severity);
        Assert.Equal("high", confidence);
    }

    [Theory]
    [InlineData("9.8 CVSS:3.1/AV:N", "Critical")]
    [InlineData("7.5 CVSS:3.1/AV:N", "High")]
    [InlineData("5.0 CVSS:3.1/AV:N", "Medium")]
    [InlineData("2.0 CVSS:3.1/AV:N", "Low")]
    public void OsvSeverity_CvssScoreBands(string score, string expected)
    {
        var detail = new OsvVulnDetail("GHSA-x", null, [], [new OsvSeverityEntry("CVSS_V3", score)], null, []);
        Assert.Equal(expected, OsvSeverity.Map(detail).Severity);
    }

    [Fact]
    public void OsvSeverity_NoSignal_IsMediumLowConfidence()
    {
        var detail = new OsvVulnDetail("GHSA-x", null, [], [], null, []);
        var (severity, confidence) = OsvSeverity.Map(detail);

        Assert.Equal("Medium", severity);
        Assert.Equal("low", confidence);
    }

    [Fact]
    public void ParseVulnDetail_ExtractsAliasesAndFixedVersions()
    {
        const string json = """
                            {
                              "id": "GHSA-xxxx-yyyy-zzzz",
                              "summary": "Test vuln",
                              "aliases": ["CVE-2026-0001", "GHSA-other"],
                              "severity": [{ "type": "CVSS_V3", "score": "9.8 CVSS:3.1/AV:N" }],
                              "affected": [
                                { "ranges": [{ "type": "ECOSYSTEM", "events": [{ "introduced": "0" }, { "fixed": "1.2.3" }] }] },
                                { "ranges": [{ "type": "ECOSYSTEM", "events": [{ "introduced": "0" }, { "fixed": "2.0.0" }] }] }
                              ]
                            }
                            """;
        using var doc = JsonDocument.Parse(json);

        var detail = OsvClient.ParseVulnDetail(doc);

        Assert.Equal("GHSA-xxxx-yyyy-zzzz", detail.Id);
        Assert.Equal("CVE-2026-0001", detail.CveId);
        Assert.Equal("1.2.3", detail.FixedVersion);
        Assert.Equal("Critical", OsvSeverity.Map(detail).Severity);
    }

    [Fact]
    public void ParseVulnDetail_NoFixedVersion_IsNull()
    {
        const string json = """
                            { "id": "GHSA-nofix", "affected": [{ "ranges": [{ "type": "ECOSYSTEM", "events": [{ "introduced": "0" }] }] }] }
                            """;
        using var doc = JsonDocument.Parse(json);

        Assert.Null(OsvClient.ParseVulnDetail(doc).FixedVersion);
    }

    // --- SBOM parsing -----------------------------------------------------------

    [Fact]
    public void SbomParser_NpmAndPypiComponents_ParsedWithEcosystems()
    {
        const string sbom = """
                            { "bomFormat": "CycloneDX", "specVersion": "1.5", "components": [
                              { "type": "library", "name": "lodash", "version": "4.17.21", "purl": "pkg:npm/lodash@4.17.21" },
                              { "type": "library", "name": "requests", "version": "2.31.0", "purl": "pkg:pypi/requests@2.31.0" },
                              { "type": "library", "name": "@types/node", "version": "18.0.0", "purl": "pkg:npm/%40types%2Fnode@18.0.0" }
                            ] }
                            """;

        var result = SbomComponentParser.Parse(sbom);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(3, result.Components.Count);
        Assert.Contains(result.Components, c => c.Name == "lodash" && c.Ecosystem == "npm");
        Assert.Contains(result.Components, c => c.Name == "requests" && c.Ecosystem == "PyPI");
        Assert.Contains(result.Components, c => c.Name == "@types/node" && c.Version == "18.0.0");
    }

    [Fact]
    public void SbomParser_ComponentWithoutPurl_IsSkippedNotGuessed()
    {
        const string sbom = """
                            { "components": [
                              { "type": "library", "name": "mystery", "version": "1.0.0" },
                              { "type": "library", "name": "weird", "version": "2.0.0", "purl": "pkg:unknown/weird@2.0.0" }
                            ] }
                            """;

        var result = SbomComponentParser.Parse(sbom);

        Assert.Empty(result.Components);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void SbomParser_MalformedJson_ReportsError()
    {
        var result = SbomComponentParser.Parse("{ not json");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Components);
    }

    // --- Store ------------------------------------------------------------------

    [Fact]
    public async Task Store_AlertsSurviveAcrossInstancesAndStayClientScoped()
    {
        var options = Options.Create(new SecurityScanningOptions { RadarRoot = this.tempRoot });
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var alert = AlertRecord(clientA, "GHSA-new1", "lodash", "4.17.21");

        var store1 = new JsonFileRadarStore(options, NullLogger<JsonFileRadarStore>.Instance);
        await store1.AddAlertsAsync(clientA, [alert], CancellationToken.None);

        var store2 = new JsonFileRadarStore(options, NullLogger<JsonFileRadarStore>.Instance);
        var listedA = await store2.ListAlertsAsync(clientA, CancellationToken.None);
        var listedB = await store2.ListAlertsAsync(clientB, CancellationToken.None);

        Assert.Single(listedA);
        Assert.Equal("GHSA-new1", listedA[0].VulnId);
        Assert.Empty(listedB);
    }

    [Fact]
    public async Task Store_AddAlerts_DedupesByTriple()
    {
        var options = Options.Create(new SecurityScanningOptions { RadarRoot = this.tempRoot });
        var store = new JsonFileRadarStore(options, NullLogger<JsonFileRadarStore>.Instance);
        var clientId = Guid.NewGuid();

        var first = await store.AddAlertsAsync(clientId, [AlertRecord(clientId, "GHSA-x", "lodash", "4.17.21")], CancellationToken.None);
        var second = await store.AddAlertsAsync(clientId, [AlertRecord(clientId, "GHSA-x", "lodash", "4.17.21")], CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(await store.ListAlertsAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task Store_StateDefaultsAndRoundTrip()
    {
        var options = Options.Create(new SecurityScanningOptions { RadarRoot = this.tempRoot });
        var store = new JsonFileRadarStore(options, NullLogger<JsonFileRadarStore>.Instance);
        var clientId = Guid.NewGuid();

        var defaults = await store.GetStateAsync(clientId, 360, CancellationToken.None);
        Assert.True(defaults.Enabled);
        Assert.Equal(360, defaults.IntervalMinutes);
        Assert.Null(defaults.LastPassAt);

        var saved = defaults with { Enabled = false, IntervalMinutes = 60, SeenKeys = ["GHSA-x|lodash@4.17.21"] };
        await store.SaveStateAsync(clientId, saved, CancellationToken.None);

        var reloaded = await store.GetStateAsync(clientId, 360, CancellationToken.None);
        Assert.False(reloaded.Enabled);
        Assert.Equal(60, reloaded.IntervalMinutes);
        Assert.Contains("GHSA-x|lodash@4.17.21", reloaded.SeenKeys);
    }

    // --- Service: one full pass -------------------------------------------------

    [Fact]
    public async Task CheckNow_NewCriticalCve_AlertsOnce_ThenNothingNew()
    {
        var clientId = Guid.NewGuid();
        var scanId = Guid.NewGuid();
        var sbomPath = await this.WriteSbomAsync(
            scanId, """
                    { "bomFormat": "CycloneDX", "specVersion": "1.5", "components": [
                      { "type": "library", "name": "lodash", "version": "4.17.21", "purl": "pkg:npm/lodash@4.17.21" },
                      { "type": "library", "name": "express", "version": "4.18.2", "purl": "pkg:npm/express@4.18.2" }
                    ] }
                    """);

        await using var db = this.CreateDb();
        db.SecurityScans.Add(
            new SecurityScanRecord
            {
                Id = scanId,
                ClientId = clientId,
                RepositoryUrl = "https://github.com/o/r",
                Status = (int)SecurityScanStatus.Succeeded,
                QueuedAt = DateTimeOffset.UtcNow.AddDays(-1),
                FinishedAt = DateTimeOffset.UtcNow.AddDays(-1),
                SbomPath = sbomPath,
            });
        // The last full scan already reported GHSA-old1 for lodash 4.17.20.
        db.SecurityFindings.Add(
            new SecurityFindingRecord
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ScanId = scanId,
                Fingerprint = "fp-old1",
                Scanner = SecurityScanners.DepScan,
                RuleId = "GHSA-old1",
                Severity = (int)SecurityFindingSeverity.High,
                Title = "lodash@4.17.20: old vuln",
                Category = "vulnerable-dependency",
                Evidence = "lodash@4.17.20 is affected by GHSA-old1 (CVE-2021-12345).",
                Status = (int)SecurityFindingStatus.Open,
                FirstSeenScanId = scanId,
                LastSeenScanId = scanId,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
                LastSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
        await db.SaveChangesAsync();

        var osv = new FakeOsvClient(
            batch: new Dictionary<(string Name, string Version), string[]>
            {
                [("lodash", "4.17.21")] = ["GHSA-old1", "GHSA-new1"],
                [("express", "4.18.2")] = ["GHSA-new2"],
            },
            details: new Dictionary<string, OsvVulnDetail>
            {
                ["GHSA-new1"] = new OsvVulnDetail(
                    "GHSA-new1", "Critical new vuln", ["CVE-2026-0001"],
                    [new OsvSeverityEntry("CVSS_V3", "9.8 CVSS:3.1/AV:N")], null, ["4.17.22"]),
                ["GHSA-new2"] = new OsvVulnDetail(
                    "GHSA-new2", "Medium new vuln", [],
                    [new OsvSeverityEntry("CVSS_V3", "5.0 CVSS:3.1/AV:N")], null, []),
            });
        var service = this.CreateService(db, osv);

        var first = await service.CheckNowAsync(clientId, CancellationToken.None);

        Assert.Equal(1, first.ReposChecked);
        Assert.Equal(2, first.ComponentsChecked);
        Assert.Equal(2, first.NewOccurrences);
        Assert.Equal(1, first.NewAlerts);
        Assert.False(first.NothingNew);

        var alerts = await service.ListAlertsAsync(clientId, CancellationToken.None);
        Assert.Single(alerts);
        var alert = alerts[0];
        Assert.Equal("GHSA-new1", alert.VulnId);
        Assert.Equal("CVE-2026-0001", alert.CveId);
        Assert.Equal("Critical", alert.Severity);
        Assert.Equal("lodash", alert.PackageName);
        Assert.Equal("4.17.21", alert.PackageVersion);
        Assert.Equal("4.17.22", alert.FixedVersion);
        Assert.Equal("https://osv.dev/vulnerability/GHSA-new1", alert.OsvUrl);
        Assert.Equal("New", alert.Status);
        Assert.Equal(scanId, alert.ScanId);

        // Same OSV data again: everything is now in the ledger — nothing new, no duplicate alert.
        var second = await service.CheckNowAsync(clientId, CancellationToken.None);
        Assert.Equal(0, second.NewOccurrences);
        Assert.Equal(0, second.NewAlerts);
        Assert.True(second.NothingNew);
        Assert.Single(await service.ListAlertsAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task CheckNow_NoScansWithSbom_SaysSoInsteadOfNothingNew()
    {
        await using var db = this.CreateDb();
        var service = this.CreateService(db, new FakeOsvClient(new Dictionary<(string, string), string[]>(), new Dictionary<string, OsvVulnDetail>()));

        var result = await service.CheckNowAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0, result.ReposChecked);
        Assert.False(result.NothingNew);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task CheckNow_OsvDown_ReportsErrorWithoutFakeAlerts()
    {
        var clientId = Guid.NewGuid();
        var scanId = Guid.NewGuid();
        var sbomPath = await this.WriteSbomAsync(
            scanId, """
                    { "components": [{ "type": "library", "name": "lodash", "version": "4.17.21", "purl": "pkg:npm/lodash@4.17.21" }] }
                    """);

        await using var db = this.CreateDb();
        db.SecurityScans.Add(
            new SecurityScanRecord
            {
                Id = scanId,
                ClientId = clientId,
                RepositoryUrl = "https://github.com/o/r",
                Status = (int)SecurityScanStatus.Succeeded,
                QueuedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                SbomPath = sbomPath,
            });
        await db.SaveChangesAsync();

        var service = this.CreateService(db, new ThrowingOsvClient());

        var result = await service.CheckNowAsync(clientId, CancellationToken.None);

        Assert.Equal(1, result.ReposChecked);
        Assert.Equal(0, result.NewAlerts);
        Assert.False(result.NothingNew);
        Assert.NotNull(result.Repos[0].Error);
        Assert.Empty(await service.ListAlertsAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task AcknowledgeAlert_IsIdempotentAndClientScoped()
    {
        await using var db = this.CreateDb();
        var service = this.CreateService(db, new FakeOsvClient(new Dictionary<(string, string), string[]>(), new Dictionary<string, OsvVulnDetail>()));
        var clientId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var store = new JsonFileRadarStore(
            Options.Create(new SecurityScanningOptions { RadarRoot = this.tempRoot }),
            NullLogger<JsonFileRadarStore>.Instance);
        await store.AddAlertsAsync(clientId, [AlertRecord(clientId, "GHSA-x", "lodash", "4.17.21")], CancellationToken.None);

        var acknowledged = await service.AcknowledgeAlertAsync(
            clientId, (await store.ListAlertsAsync(clientId, CancellationToken.None))[0].Id, CancellationToken.None);
        Assert.Equal("Acknowledged", acknowledged?.Status);

        var again = await service.AcknowledgeAlertAsync(clientId, acknowledged!.Id, CancellationToken.None);
        Assert.Equal("Acknowledged", again?.Status);

        Assert.Null(await service.AcknowledgeAlertAsync(otherId, acknowledged.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSettings_RejectsOutOfRangeInterval()
    {
        await using var db = this.CreateDb();
        var service = this.CreateService(db, new FakeOsvClient(new Dictionary<(string, string), string[]>(), new Dictionary<string, OsvVulnDetail>()));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateSettingsAsync(Guid.NewGuid(), new UpdateRadarSettingsRequest(null, 5), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateSettingsAsync(Guid.NewGuid(), new UpdateRadarSettingsRequest(null, 20000), CancellationToken.None));

        var ok = await service.UpdateSettingsAsync(Guid.NewGuid(), new UpdateRadarSettingsRequest(false, 120), CancellationToken.None);
        Assert.False(ok.Enabled);
        Assert.Equal(120, ok.IntervalMinutes);
    }

    // --- Helpers ----------------------------------------------------------------

    private static RadarAlertRecord AlertRecord(Guid clientId, string vulnId, string package, string version) =>
        new(
            Guid.NewGuid(),
            clientId,
            Guid.NewGuid(),
            "https://github.com/o/r",
            package,
            version,
            "npm",
            vulnId,
            null,
            "High",
            "Summary",
            null,
            $"https://osv.dev/vulnerability/{vulnId}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            RadarAlertStatus.New,
            null,
            null);

    private RadarService CreateService(MeisterProPRDbContext db, IOsvClient osv)
    {
        var options = Options.Create(new SecurityScanningOptions { RadarRoot = this.tempRoot });
        var store = new JsonFileRadarStore(options, NullLogger<JsonFileRadarStore>.Instance);
        return new RadarService(db, osv, store, options, NullLogger<RadarService>.Instance);
    }

    private MeisterProPRDbContext CreateDb()
    {
        var dbOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase("radar-tests-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new MeisterProPRDbContext(dbOptions);
    }

    private async Task<string> WriteSbomAsync(Guid scanId, string json)
    {
        var path = Path.Combine(this.tempRoot, $"{scanId:N}.cyclonedx.json");
        Directory.CreateDirectory(this.tempRoot);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    /// <summary>Stub OSV client returning canned batch/detail data. No HTTP.</summary>
    private sealed class FakeOsvClient(
        Dictionary<(string Name, string Version), string[]> batch,
        Dictionary<string, OsvVulnDetail> details) : IOsvClient
    {
        public Task<IReadOnlyList<OsvBatchEntry>> QueryBatchAsync(IReadOnlyList<OsvPackageQuery> queries, CancellationToken ct)
        {
            var entries = queries
                .Select(q => new OsvBatchEntry(
                    q,
                    batch.TryGetValue((q.Name, q.Version), out var ids) ? ids : []))
                .ToList();
            return Task.FromResult<IReadOnlyList<OsvBatchEntry>>(entries);
        }

        public Task<OsvVulnDetail?> GetVulnAsync(string id, CancellationToken ct) =>
            Task.FromResult(details.TryGetValue(id, out var detail) ? detail : null);
    }

    /// <summary>OSV client that always fails, for the honest-failure test.</summary>
    private sealed class ThrowingOsvClient : IOsvClient
    {
        public Task<IReadOnlyList<OsvBatchEntry>> QueryBatchAsync(IReadOnlyList<OsvPackageQuery> queries, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<OsvBatchEntry>>(new OsvClientException("Could not reach OSV.dev."));

        public Task<OsvVulnDetail?> GetVulnAsync(string id, CancellationToken ct) =>
            Task.FromException<OsvVulnDetail?>(new OsvClientException("Could not reach OSV.dev."));
    }
}
