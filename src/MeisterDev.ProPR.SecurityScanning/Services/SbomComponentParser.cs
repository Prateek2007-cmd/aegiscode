// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>One SBOM component the radar can check: a named package version in a known ecosystem.</summary>
public sealed record SbomComponent(string Name, string Version, string Ecosystem, string Purl);

/// <summary>Outcome of parsing one CycloneDX SBOM document.</summary>
public sealed record SbomParseResult(IReadOnlyList<SbomComponent> Components, int Skipped, string? Error);

/// <summary>
///     Pure CycloneDX SBOM parser for the radar. Components need a name, a version,
///     and a package URL whose type maps to an OSV ecosystem; anything else is
///     counted as skipped, never silently dropped and never guessed. A document
///     that is not JSON at all reports an error instead of an empty component list.
/// </summary>
public static class SbomComponentParser
{
    /// <summary>Package-URL type to OSV ecosystem name. Only ecosystems OSV.dev answers for.</summary>
    internal static readonly IReadOnlyDictionary<string, string> EcosystemByPurlType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["npm"] = "npm",
            ["pypi"] = "PyPI",
            ["maven"] = "Maven",
            ["nuget"] = "NuGet",
            ["golang"] = "Go",
            ["cargo"] = "crates.io",
            ["composer"] = "Packagist",
            ["gem"] = "RubyGems",
            ["hex"] = "Hex",
            ["pub"] = "Pub",
        };

    /// <summary>Parses a CycloneDX SBOM document. Never throws on malformed input.</summary>
    public static SbomParseResult Parse(string? sbomJson)
    {
        if (string.IsNullOrWhiteSpace(sbomJson))
        {
            return new SbomParseResult([], 0, "The stored SBOM is empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(sbomJson);
        }
        catch (JsonException ex)
        {
            return new SbomParseResult([], 0, $"The stored SBOM is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                return new SbomParseResult([], 0, null);
            }

            var parsed = new List<SbomComponent>();
            var skipped = 0;
            foreach (var component in components.EnumerateArray())
            {
                if (TryParseComponent(component) is { } parsedComponent)
                {
                    parsed.Add(parsedComponent);
                }
                else
                {
                    skipped++;
                }
            }

            return new SbomParseResult(parsed, skipped, null);
        }
    }

    /// <summary>
    ///     Parses one component. Name and version come from the fields, falling back
    ///     to the package URL; the ecosystem comes from the package-URL type only,
    ///     because a CycloneDX component <c>type</c> (library, application, …) is
    ///     not an ecosystem and must never be guessed from.
    /// </summary>
    internal static SbomComponent? TryParseComponent(JsonElement component)
    {
        if (component.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var purl = GetString(component, "purl");
        string? ecosystem = null;
        string? purlName = null;
        string? purlVersion = null;
        if (!string.IsNullOrWhiteSpace(purl))
        {
            (ecosystem, purlName, purlVersion) = ParsePurl(purl);
        }

        if (ecosystem is null)
        {
            return null;
        }

        var name = purlName ?? GetString(component, "name");
        var version = GetString(component, "version") ?? purlVersion;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new SbomComponent(name.Trim(), version.Trim(), ecosystem, purl!);
    }

    /// <summary>
    ///     Splits a package URL (<c>pkg:&lt;type&gt;/&lt;name&gt;@&lt;version&gt;</c>) into
    ///     (ecosystem, name, version). Returns nulls when the URL is malformed or its
    ///     type has no OSV ecosystem mapping.
    /// </summary>
    internal static (string? Ecosystem, string? Name, string? Version) ParsePurl(string purl)
    {
        if (!purl.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, null);
        }

        var rest = purl[4..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return (null, null, null);
        }

        var type = rest[..slash];
        if (!EcosystemByPurlType.TryGetValue(type, out var ecosystem))
        {
            return (null, null, null);
        }

        var remainder = rest[(slash + 1)..];
        var at = remainder.LastIndexOf('@');
        if (at < 0)
        {
            return (ecosystem, null, null);
        }

        var name = Uri.UnescapeDataString(remainder[..at]);
        var version = remainder[(at + 1)..];
        // Qualifiers (?...) and subpaths (#...) are not part of the version.
        var qualifier = version.IndexOfAny(['?', '#']);
        if (qualifier >= 0)
        {
            version = version[..qualifier];
        }

        return (ecosystem, name, version);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
