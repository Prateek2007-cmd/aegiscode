// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>One package to check against OSV.dev. Ecosystem uses OSV's names (npm, PyPI, Maven, …).</summary>
public sealed record OsvPackageQuery(string Name, string Ecosystem, string Version);

/// <summary>One querybatch answer: the vulnerability ids OSV reports for one package version.</summary>
public sealed record OsvBatchEntry(OsvPackageQuery Query, IReadOnlyList<string> VulnIds);

/// <summary>One CVSS entry from an OSV record: the raw <c>score</c> string starts with the numeric score.</summary>
public sealed record OsvSeverityEntry(string Type, string Score);

/// <summary>
///     A full OSV vulnerability record, reduced to what the radar needs. Severity
///     mapping mirrors the vulnveto dep-scan module exactly (database_specific
///     first, then CVSS, then an honest medium/low-confidence fallback) so a
///     radar alert never disagrees with what a full scan would report.
/// </summary>
public sealed record OsvVulnDetail(
    string Id,
    string? Summary,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<OsvSeverityEntry> Severity,
    string? DatabaseSeverity,
    IReadOnlyList<string> FixedVersions)
{
    /// <summary>First CVE alias, if the record carries one.</summary>
    public string? CveId => this.Aliases.FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.Ordinal));

    /// <summary>First fixed version across all affected ranges, if OSV names one.</summary>
    public string? FixedVersion => this.FixedVersions.Count == 0 ? null : this.FixedVersions[0];
}

/// <summary>User-safe OSV failure (message is shown to users; no internals).</summary>
public sealed class OsvClientException(string message) : Exception(message);

/// <summary>Minimal OSV.dev client: batch queries plus single-vulnerability details. No API key; OSV is free.</summary>
public interface IOsvClient
{
    /// <summary>Returns one entry per query, in query order. Throws <see cref="OsvClientException"/> on transport or API failure.</summary>
    Task<IReadOnlyList<OsvBatchEntry>> QueryBatchAsync(IReadOnlyList<OsvPackageQuery> queries, CancellationToken ct);

    /// <summary>Returns the full record, or <c>null</c> when OSV has no such id. Throws <see cref="OsvClientException"/> on transport or API failure.</summary>
    Task<OsvVulnDetail?> GetVulnAsync(string id, CancellationToken ct);
}

/// <summary>Severity mapping shared by the radar and its tests. Mirrors the vulnveto dep-scan module.</summary>
public static class OsvSeverity
{
    /// <summary>Maps an OSV record to (severity, confidence). Never invents a severity: no signal means medium/low-confidence.</summary>
    public static (string Severity, string Confidence) Map(OsvVulnDetail vuln)
    {
        ArgumentNullException.ThrowIfNull(vuln);

        var dbSeverity = vuln.DatabaseSeverity?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(dbSeverity))
        {
            var mapped = dbSeverity switch
            {
                "critical" => "Critical",
                "high" => "High",
                "moderate" => "Medium",
                "low" => "Low",
                _ => null,
            };
            if (mapped is not null)
            {
                return (mapped, "high");
            }
        }

        foreach (var entry in vuln.Severity)
        {
            if (entry.Type is not ("CVSS_V3" or "CVSS_V31"))
            {
                continue;
            }

            // Score strings look like "9.8 CVSS:3.1/AV:N/..." — only the leading number is the score.
            var firstToken = entry.Score.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (firstToken is not null
                && double.TryParse(firstToken, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score))
            {
                return (ScoreToSeverity(score), "high");
            }
        }

        // No machine-readable severity — still a real vuln, say so honestly.
        return ("Medium", "low");
    }

    /// <summary>CVSS numeric score to severity word. Same bands the engine's cvss module uses.</summary>
    public static string ScoreToSeverity(double score) => score switch
    {
        >= 9.0 => "Critical",
        >= 7.0 => "High",
        >= 4.0 => "Medium",
        > 0 => "Low",
        _ => "Info",
    };
}

/// <inheritdoc />
public sealed class OsvClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SecurityScanningOptions> options,
    ILogger<OsvClient> logger) : IOsvClient
{
    private const int BatchChunkSize = 100;
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("radar-osv");

    /// <inheritdoc />
    public async Task<IReadOnlyList<OsvBatchEntry>> QueryBatchAsync(IReadOnlyList<OsvPackageQuery> queries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var baseUrl = ValidateBaseUrl(options.Value.OsvBaseUrl);

        var results = new List<OsvBatchEntry>(queries.Count);
        foreach (var chunk in queries.Chunk(BatchChunkSize))
        {
            var chunkResults = await this.QueryBatchChunkAsync(baseUrl, chunk, ct);
            results.AddRange(chunkResults);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<OsvVulnDetail?> GetVulnAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var baseUrl = ValidateBaseUrl(options.Value.OsvBaseUrl);
        var url = $"{baseUrl}/v1/vulns/{Uri.EscapeDataString(id)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DetailTimeout);

        HttpResponseMessage response;
        try
        {
            response = await this.httpClient.GetAsync(url, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OsvClientException($"OSV.dev did not answer in time for vulnerability {id}.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OSV.dev request failed for vulnerability {VulnId}", id);
            throw new OsvClientException($"Could not reach OSV.dev for vulnerability {id}. Check the network and try again.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OsvClientException($"OSV.dev answered {(int)response.StatusCode} for vulnerability {id}.");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return ParseVulnDetail(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OSV.dev returned unreadable JSON for vulnerability {VulnId}", id);
            throw new OsvClientException($"OSV.dev returned unreadable data for vulnerability {id}.");
        }
    }

    private async Task<IReadOnlyList<OsvBatchEntry>> QueryBatchChunkAsync(
        string baseUrl,
        OsvPackageQuery[] chunk,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BatchTimeout);

        var payload = new
        {
            queries = chunk.Select(q => new
            {
                package = new { name = q.Name, ecosystem = q.Ecosystem },
                version = q.Version,
            }).ToArray(),
        };

        HttpResponseMessage response;
        try
        {
            // PostAsJsonAsync is ambiguous here (System.Net.Http vs System.Net.Http.Json
            // both define it), so post explicit JSON content instead.
            using var content = JsonContent.Create(payload);
            response = await this.httpClient.PostAsync($"{baseUrl}/v1/querybatch", content, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OsvClientException("OSV.dev did not answer the batch query in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OSV.dev batch query failed");
            throw new OsvClientException("Could not reach OSV.dev. Check the network and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OsvClientException($"OSV.dev answered {(int)response.StatusCode} to the batch query.");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var results = doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                ? resultsElement
                : default;

            var entries = new List<OsvBatchEntry>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var ids = new List<string>();
                if (results.ValueKind == JsonValueKind.Array && i < results.GetArrayLength())
                {
                    var result = results[i];
                    if (result.TryGetProperty("vulns", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                        {
                            if (vuln.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            {
                                var vulnId = id.GetString();
                                if (!string.IsNullOrWhiteSpace(vulnId))
                                {
                                    ids.Add(vulnId);
                                }
                            }
                        }
                    }
                }

                entries.Add(new OsvBatchEntry(chunk[i], ids));
            }

            return entries;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OSV.dev returned unreadable JSON to the batch query");
            throw new OsvClientException("OSV.dev returned unreadable data to the batch query.");
        }
    }

    /// <summary>Parses one <c>/v1/vulns/{id}</c> record into the fields the radar needs.</summary>
    internal static OsvVulnDetail ParseVulnDetail(JsonDocument doc)
    {
        var root = doc.RootElement;

        var id = GetString(root, "id") ?? string.Empty;
        var summary = GetString(root, "summary");
        var aliases = GetStringArray(root, "aliases");

        var severity = new List<OsvSeverityEntry>();
        if (root.TryGetProperty("severity", out var severityElement) && severityElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in severityElement.EnumerateArray())
            {
                var type = GetString(entry, "type");
                var score = GetString(entry, "score");
                if (!string.IsNullOrWhiteSpace(type) && score is not null)
                {
                    severity.Add(new OsvSeverityEntry(type, score));
                }
            }
        }

        string? databaseSeverity = null;
        if (root.TryGetProperty("database_specific", out var dbSpecific) && dbSpecific.ValueKind == JsonValueKind.Object)
        {
            databaseSeverity = GetString(dbSpecific, "severity");
        }

        var fixedVersions = new List<string>();
        if (root.TryGetProperty("affected", out var affected) && affected.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in affected.EnumerateArray())
            {
                if (!entry.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var range in ranges.EnumerateArray())
                {
                    if (!range.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var evt in events.EnumerateArray())
                    {
                        var fixedVersion = GetString(evt, "fixed");
                        if (!string.IsNullOrWhiteSpace(fixedVersion) && !fixedVersions.Contains(fixedVersion, StringComparer.Ordinal))
                        {
                            fixedVersions.Add(fixedVersion);
                        }
                    }
                }
            }
        }

        return new OsvVulnDetail(id, summary, aliases, severity, databaseSeverity, fixedVersions);
    }

    /// <summary>
    ///     Validates the operator-configured OSV base URL. Only public https endpoints
    ///     are accepted: the URL comes from environment configuration, not user input,
    ///     but a loopback or private address here would still be an SSRF hole.
    /// </summary>
    internal static string ValidateBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new OsvClientException("The OSV base URL is not a valid https URL (AEGIS_SECURITY_OSV_BASE_URL).");
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IsPrivateOrLoopback(address)))
        {
            throw new OsvClientException("The OSV base URL must be a public https endpoint, not a local address.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // IPv6 loopback/link-local/site-local are not public endpoints either.
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        var result = new List<string>();
        if (element.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }
}
