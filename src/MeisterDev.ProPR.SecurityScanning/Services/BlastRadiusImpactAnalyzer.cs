// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.SecurityScanning.Models;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Input to the blast-radius impact computation: the seed plus the repo's readable files.</summary>
/// <param name="SeedKind">"file" or "package".</param>
/// <param name="SeedFilePath">Repo-relative seed file, for file seeds.</param>
/// <param name="SeedLine">1-based finding line in the seed file, when known.</param>
/// <param name="PackageName">Package name, for package seeds.</param>
/// <param name="Files">Repo-relative path → source text for every readable file.</param>
public sealed record BlastRadiusInput(
    string SeedKind,
    string? SeedFilePath,
    int? SeedLine,
    string? PackageName,
    IReadOnlyDictionary<string, string> Files);

/// <summary>
///     Pure impact computation for the Blast Radius view: given a vulnerable file or
///     package plus the repository's readable files, finds everything that calls into
///     or imports it — using only the structural analyzer's confirmed identifier
///     occurrences (comments and string literals excluded) and its code-only text
///     projection for import matching. Never invents a link the analyzer did not
///     find; matches are name-based, never semantic. Deterministic: files, symbols
///     and edges are processed in sorted order and every cap is reported.
/// </summary>
public sealed class BlastRadiusImpactAnalyzer(IStructuralCodeAnalyzer analyzer)
{
    /// <summary>Maximum repo files scanned per analysis.</summary>
    public const int MaxFilesScanned = 300;

    /// <summary>Maximum file size analyzed, in bytes (larger files are skipped, honestly).</summary>
    public const int MaxFileBytes = 1_000_000;

    /// <summary>Maximum seed symbols traced.</summary>
    public const int MaxSeedSymbols = 20;

    /// <summary>Maximum impact edges reported.</summary>
    public const int MaxEdges = 200;

    /// <summary>Maximum data-flow notes generated.</summary>
    public const int MaxNotes = 5;

    /// <summary>Maximum entry points reported.</summary>
    public const int MaxEntryPoints = 25;

    /// <summary>Maximum skipped files listed individually (the total is always reported).</summary>
    public const int MaxSkippedShown = 50;

    /// <summary>Hop depth: seed → direct callers → callers of callers.</summary>
    public const int MaxHops = 2;

    private static readonly string[] SupportedLanguageNames =
        ["C#", "TypeScript", "JavaScript", "Python", "Go", "Java", "Ruby"];

    /// <summary>Computes the blast-radius map. Never throws for analysis problems — returns Unavailable instead.</summary>
    public async Task<BlastRadiusResponse> AnalyzeAsync(BlastRadiusInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var state = new AnalysisState();

        if (!analyzer.IsAvailable)
        {
            return state.Unavailable("Code analysis is unavailable on this server (no parser backend loaded).", seed: null);
        }

        try
        {
            return input.SeedKind switch
            {
                "file" => await this.AnalyzeFileSeedAsync(input, state, ct),
                "package" => await this.AnalyzePackageSeedAsync(input, state, ct),
                _ => state.Unavailable($"Unknown seed kind '{input.SeedKind}'.", seed: null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Analysis must never fail the request: report honestly instead.
            return state.Unavailable($"Blast-radius analysis failed: {ex.Message}", seed: null);
        }
    }

    private async Task<BlastRadiusResponse> AnalyzeFileSeedAsync(BlastRadiusInput input, AnalysisState state, CancellationToken ct)
    {
        var seedPath = input.SeedFilePath!;
        var language = LanguagePaths.TryResolve(seedPath);
        if (language is null || !analyzer.CanAnalyze(seedPath))
        {
            return state.Unavailable(
                $"The seed file '{seedPath}' is written in a language the analyzer does not support. "
                + $"Supported: {string.Join(", ", SupportedLanguageNames)}.",
                new BlastRadiusSeed("file", seedPath, null, null, null));
        }

        if (!input.Files.TryGetValue(seedPath, out var seedText))
        {
            return state.Unavailable(
                $"The seed file '{seedPath}' was not found in the cloned repository. The scan may predate the current HEAD.",
                new BlastRadiusSeed("file", seedPath, null, language.Value.ToString(), null));
        }

        var candidates = this.CollectCandidates(input.Files, state);
        var seedRequest = new StructuralParseRequest(seedPath, language.Value, seedText, []);

        // Primary symbol: the definition enclosing the finding line, when one resolves.
        string? seedSymbol = null;
        var seedDefinitions = await this.SafeGetDefinitionsAsync(seedRequest, ct);
        if (input.SeedLine is { } line)
        {
            var enclosing = await this.SafeResolveEnclosingAsync(seedRequest, line, ct);
            if (enclosing?.Name is not null)
            {
                seedSymbol = enclosing.Name;
            }
        }

        var seedSymbols = seedSymbol is not null
            ? new List<string> { seedSymbol }
            : seedDefinitions
                .Where(d => d.Name is not null)
                .Take(MaxSeedSymbols)
                .Select(d => d.Name!)
                .ToList();

        var seed = new BlastRadiusSeed("file", seedPath, null, language.Value.ToString(), seedSymbol);

        if (seedSymbols.Count == 0)
        {
            // No named definitions to trace: fall back to finding importers of the file itself.
            return await this.AnalyzeByImportAsync(
                input, candidates, state, seed,
                specifier => SpecifierMatchesFile(specifier, seedPath),
                ct);
        }

        var edges = new EdgeSet();
        var hop1Callers = await this.ScanReferencesAsync(seedSymbols, candidates, seedPath, edges, state, ct);
        foreach (var (name, _, filePath) in hop1Callers
                     .Where(c => c.Name is not null)
                     .Distinct()
                     .Take(MaxSeedSymbols))
        {
            ct.ThrowIfCancellationRequested();
            await this.ScanReferencesAsync([name!], candidates, filePath, edges, state, ct);
        }

        return this.BuildResponse(input, candidates, state, seed, edges);
    }

    private async Task<BlastRadiusResponse> AnalyzePackageSeedAsync(BlastRadiusInput input, AnalysisState state, CancellationToken ct)
    {
        var package = input.PackageName!;
        var seed = new BlastRadiusSeed("package", null, package, null, null);
        var candidates = this.CollectCandidates(input.Files, state);
        return await this.AnalyzeByImportAsync(
            input, candidates, state, seed,
            specifier => SpecifierMatchesPackage(specifier, package),
            ct);
    }

    /// <summary>
    ///     Import-driven analysis: finds the files that import the seed (package name or
    ///     seed file path), traces usage of the imported bindings inside those files
    ///     (JavaScript/TypeScript and Python only), and follows one hop of callers
    ///     beyond them.
    /// </summary>
    private async Task<BlastRadiusResponse> AnalyzeByImportAsync(
        BlastRadiusInput input,
        IReadOnlyList<CandidateFile> candidates,
        AnalysisState state,
        BlastRadiusSeed seed,
        Func<string, bool> specifierMatches,
        CancellationToken ct)
    {
        var edges = new EdgeSet();
        var importers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var hop1Callers = new List<(string? Name, int StartLine, string FilePath)>();

        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var codeText = await this.SafeExtractCodeTextAsync(file, ct);
            if (string.IsNullOrEmpty(codeText))
            {
                continue;
            }

            var (importLines, bindings) = FindImports(codeText, file.Language, specifierMatches);
            if (importLines.Count == 0)
            {
                continue;
            }

            importers[file.Path] = importLines;
            foreach (var line in importLines)
            {
                edges.AddImport(file.Path, line, seed);
            }

            // Trace usage of the imported bindings inside the importing file.
            foreach (var binding in bindings)
            {
                var lines = await this.SafeConfirmReferencesAsync(file, binding, ct);
                foreach (var line in lines)
                {
                    if (importLines.Contains(line))
                    {
                        continue; // the import statement itself
                    }

                    var request = new StructuralParseRequest(file.Path, file.Language, file.Text, []);
                    var enclosing = await this.SafeResolveEnclosingAsync(request, line, ct);
                    edges.AddReference(file.Path, enclosing?.Name, line, file.Path, binding, importLines[0]);
                    if (enclosing?.Name is not null
                        && hop1Callers.All(c => c.Name != enclosing.Name || c.FilePath != file.Path))
                    {
                        hop1Callers.Add((enclosing.Name, enclosing.StartLine, file.Path));
                    }
                }
            }
        }

        // One hop beyond the importer files: who calls the definitions that use the import?
        foreach (var (name, _, filePath) in hop1Callers
                     .Where(c => c.Name is not null)
                     .Distinct()
                     .Take(MaxSeedSymbols))
        {
            ct.ThrowIfCancellationRequested();
            await this.ScanReferencesAsync([name!], candidates, filePath, edges, state, ct);
        }

        return this.BuildResponse(input, candidates, state, seed, edges, importers);
    }

    /// <summary>
    ///     Scans every candidate file for confirmed references to each symbol and records
    ///     the enclosing definition as the caller. Returns the distinct caller definitions
    ///     found. A definition referencing itself (e.g. recursion) is not impact and is
    ///     skipped.
    /// </summary>
    private async Task<List<(string? Name, int StartLine, string FilePath)>> ScanReferencesAsync(
        IReadOnlyList<string> symbols,
        IReadOnlyList<CandidateFile> candidates,
        string? targetFile,
        EdgeSet edges,
        AnalysisState state,
        CancellationToken ct)
    {
        var callers = new List<(string? Name, int StartLine, string FilePath)>();
        var seenCallers = new HashSet<(string, string?)>();

        foreach (var symbol in symbols)
        {
            var targetLine = targetFile is null
                ? 0
                : await this.FindDefinitionStartLineAsync(targetFile, symbol, candidates, ct);
            foreach (var file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (edges.Count >= MaxEdges)
                {
                    state.Truncated = true;
                    return callers;
                }

                var lines = await this.SafeConfirmReferencesAsync(file, symbol, ct);
                foreach (var line in lines)
                {
                    var request = new StructuralParseRequest(file.Path, file.Language, file.Text, []);
                    var enclosing = await this.SafeResolveEnclosingAsync(request, line, ct);
                    var callerName = enclosing?.Name;
                    var callerLine = enclosing?.StartLine ?? line;

                    if (targetFile is not null
                        && string.Equals(file.Path, targetFile, StringComparison.Ordinal)
                        && string.Equals(callerName, symbol, StringComparison.Ordinal))
                    {
                        continue; // self-reference (e.g. recursion) is not impact
                    }

                    edges.AddReference(file.Path, callerName, line, targetFile ?? file.Path, symbol, targetLine);
                    if (seenCallers.Add((file.Path, callerName)))
                    {
                        callers.Add((callerName, callerLine, file.Path));
                    }
                }
            }
        }

        return callers;
    }

    private async Task<int> FindDefinitionStartLineAsync(string filePath, string symbol, IReadOnlyList<CandidateFile> candidates, CancellationToken ct)
    {
        var file = candidates.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.Ordinal));
        if (file is null)
        {
            return 0;
        }

        var request = new StructuralParseRequest(file.Path, file.Language, file.Text, []);
        var definitions = await this.SafeGetDefinitionsAsync(request, ct);
        return definitions.FirstOrDefault(d => string.Equals(d.Name, symbol, StringComparison.Ordinal))?.StartLine ?? 0;
    }

    private BlastRadiusResponse BuildResponse(
        BlastRadiusInput input,
        IReadOnlyList<CandidateFile> candidates,
        AnalysisState state,
        BlastRadiusSeed seed,
        EdgeSet edges,
        IReadOnlyDictionary<string, List<int>>? importers = null)
    {
        // Affected modules: group edges + imports by file.
        var modules = new Dictionary<string, BlastRadiusAffectedModuleBuilder>(StringComparer.Ordinal);
        foreach (var edge in edges.Edges)
        {
            if (!modules.TryGetValue(edge.FromFile, out var module))
            {
                module = new BlastRadiusAffectedModuleBuilder(edge.FromFile);
                modules[edge.FromFile] = module;
            }

            module.AddReference(edge.FromDefinition, edge.FromLine);
        }

        if (importers is not null)
        {
            foreach (var (file, lines) in importers)
            {
                if (!modules.TryGetValue(file, out var module))
                {
                    module = new BlastRadiusAffectedModuleBuilder(file);
                    modules[file] = module;
                }

                module.AddImports(lines);
            }
        }

        // Entry points: caller definitions no other analyzed definition references.
        var targeted = new HashSet<(string, string)>(
            edges.Edges
                .Where(e => e.FromDefinition is not null && e.Verification == "analyzer-confirmed")
                .Select(e => (e.ToFile, e.ToSymbol)));
        var entryPoints = new List<BlastRadiusEntryPoint>();
        foreach (var edge in edges.Edges)
        {
            if (edge.FromDefinition is null)
            {
                continue;
            }

            var key = (edge.FromFile, edge.FromDefinition);
            if (!targeted.Contains(key) && entryPoints.All(p => p.FilePath != key.Item1 || p.Definition != key.Item2))
            {
                entryPoints.Add(new BlastRadiusEntryPoint(edge.FromFile, edge.FromDefinition, "definition", edge.FromLine));
            }
        }

        // Import-only entry points: importer files with no traced callers.
        if (importers is not null)
        {
            foreach (var (file, lines) in importers)
            {
                if (!modules[file].HasCallers && entryPoints.All(p => p.FilePath != file))
                {
                    entryPoints.Add(new BlastRadiusEntryPoint(file, null, "file", lines[0]));
                }
            }
        }

        if (entryPoints.Count > MaxEntryPoints)
        {
            state.Truncated = true;
        }

        entryPoints = entryPoints
            .OrderBy(p => p.FilePath, StringComparer.Ordinal)
            .ThenBy(p => p.Line)
            .Take(MaxEntryPoints)
            .ToList();

        var notes = BuildNotes(seed, edges, entryPoints);

        return new BlastRadiusResponse(
            "Available",
            state.Truncated ? "Results were capped (file, edge, or entry-point limits reached); the map may be incomplete." : null,
            seed,
            modules.Values
                .OrderBy(m => m.FilePath, StringComparer.Ordinal)
                .Select(m => m.Build())
                .ToList(),
            entryPoints,
            edges.Edges
                .OrderBy(e => e.FromFile, StringComparer.Ordinal)
                .ThenBy(e => e.FromLine)
                .ToList(),
            notes,
            state.FilesScanned,
            state.Skipped.Take(MaxSkippedShown).ToList(),
            state.Skipped.Count,
            state.Truncated,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Template-generated data-flow notes, built only from verified edges. No AI, no invention.</summary>
    private static List<string> BuildNotes(BlastRadiusSeed seed, EdgeSet edges, List<BlastRadiusEntryPoint> entryPoints)
    {
        var notes = new List<string>();
        if (edges.Edges.Count == 0)
        {
            notes.Add("No callers found in the analyzed files — nothing in this repository references the vulnerable code.");
            return notes;
        }

        var seedLabel = seed.Kind == "package"
            ? $"package {seed.PackageName}"
            : $"{seed.FilePath}{(seed.Symbol is not null ? $":{seed.Symbol}" : string.Empty)}";

        var chains = new List<(List<BlastRadiusEdge> Chain, BlastRadiusEntryPoint Entry)>();
        foreach (var entry in entryPoints.Take(MaxNotes))
        {
            if (entry.Definition is null)
            {
                // Import-only entry point: the import itself is the whole chain.
                notes.Add($"{entry.FilePath} imports {seedLabel} (line {entry.Line}).");
                continue;
            }

            // Walk back from the entry point toward the seed (at most MaxHops + 1 edges).
            var chain = new List<BlastRadiusEdge>();
            var current = (entry.FilePath, entry.Definition);
            var guard = 0;
            while (guard++ < MaxHops + 1)
            {
                var edge = edges.Edges.FirstOrDefault(e =>
                    string.Equals(e.FromFile, current.Item1, StringComparison.Ordinal)
                    && string.Equals(e.FromDefinition, current.Item2, StringComparison.Ordinal));
                if (edge is null)
                {
                    break;
                }

                chain.Add(edge);
                current = (edge.ToFile, edge.ToSymbol);
            }

            chains.Add((chain, entry));
        }

        foreach (var (chain, _) in chains
                     .Where(c => c.Chain.Count > 0)
                     .OrderByDescending(c => c.Chain.Count)
                     .Take(MaxNotes))
        {
            var steps = chain
                .Select(e => $"{e.FromDefinition ?? e.FromFile} ({e.FromFile}:{e.FromLine})")
                .ToList();
            var last = chain[^1];
            var seedStep = last.Verification == "import-match"
                ? $"imports {seedLabel} ({last.FromFile}:{last.FromLine})"
                : $"calls {last.ToSymbol} ({last.ToFile}:{last.ToLine})";
            notes.Add(
                chain.Count == 1
                    ? $"{steps[0]} {seedStep}."
                    : $"{string.Join(", which ", steps)}, which {seedStep}.");
        }

        return notes;
    }

    private IReadOnlyList<CandidateFile> CollectCandidates(IReadOnlyDictionary<string, string> files, AnalysisState state)
    {
        var candidates = new List<CandidateFile>();
        foreach (var (path, text) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var language = LanguagePaths.TryResolve(path);
            if (language is null || !analyzer.CanAnalyze(path))
            {
                state.Skipped.Add(new BlastRadiusSkippedFile(path, "unsupported language"));
                continue;
            }

            candidates.Add(new CandidateFile(path, language.Value, text));
        }

        state.FilesScanned = candidates.Count;
        if (candidates.Count > MaxFilesScanned)
        {
            state.Truncated = true;
            return candidates.Take(MaxFilesScanned).ToList();
        }

        return candidates;
    }

    private async Task<IReadOnlyList<DefinitionSummary>> SafeGetDefinitionsAsync(StructuralParseRequest request, CancellationToken ct)
    {
        try
        {
            return await analyzer.GetDefinitionsAsync(request, ct);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<EnclosingDefinition?> SafeResolveEnclosingAsync(StructuralParseRequest request, int line, CancellationToken ct)
    {
        try
        {
            var ranges = new[] { new ChangedLineRange(line, line) };
            var result = await analyzer.ResolveEnclosingDefinitionsAsync(request with { ChangedLineRanges = ranges }, ct);
            return result.Count > 0 ? result[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<int>> SafeConfirmReferencesAsync(CandidateFile file, string symbol, CancellationToken ct)
    {
        try
        {
            var request = new StructuralParseRequest(file.Path, file.Language, file.Text, []);
            return await analyzer.ConfirmReferenceLinesAsync(request, symbol, ct);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<string> SafeExtractCodeTextAsync(CandidateFile file, CancellationToken ct)
    {
        try
        {
            var request = new StructuralParseRequest(file.Path, file.Language, file.Text, []);
            return await analyzer.ExtractCodeTextAsync(request, ct);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Finds import statements for the seed in code-only text, line by line, and
    ///     extracts the local binding names (JavaScript/TypeScript and Python only).
    ///     Multi-line imports are caught because the <c>from 'x'</c> / <c>require('x')</c>
    ///     clause is matched on its own line.
    /// </summary>
    private static (List<int> ImportLines, List<string> Bindings) FindImports(string codeText, SupportedLanguage language, Func<string, bool> specifierMatches)
    {
        var importLines = new List<int>();
        var bindings = new List<string>();
        var lines = codeText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var specifier = MatchImportSpecifier(line, language);
            if (specifier is null || !specifierMatches(specifier))
            {
                continue;
            }

            importLines.Add(i + 1);
            bindings.AddRange(ExtractBindings(line, language));
        }

        return (importLines, bindings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static string? MatchImportSpecifier(string line, SupportedLanguage language)
    {
        var trimmed = line.Trim();
        return language switch
        {
            SupportedLanguage.TypeScript or SupportedLanguage.Tsx or SupportedLanguage.JavaScript =>
                MatchRegex(trimmed, """from\s*['"]([^'"]+)['"]""")
                ?? MatchRegex(trimmed, """require\(\s*['"]([^'"]+)['"]""")
                ?? MatchRegex(trimmed, """import\(\s*['"]([^'"]+)['"]"""),
            SupportedLanguage.Python =>
                MatchRegex(trimmed, """^\s*import\s+([\w\.]+)""")
                ?? MatchRegex(trimmed, """^\s*from\s+([\w\.]+)\s+import\s+"""),
            SupportedLanguage.Go =>
                MatchRegex(trimmed, """^\s*(?:import\s+)?(?:[\w\.]+\s+)?"([^"]+)"\s*$"""),
            SupportedLanguage.Java =>
                MatchRegex(trimmed, """^\s*import\s+(?:static\s+)?([\w\.]+)\s*;"""),
            SupportedLanguage.CSharp =>
                MatchRegex(trimmed, """^\s*using\s+(?:static\s+)?([\w\.]+)\s*;"""),
            SupportedLanguage.Ruby =>
                MatchRegex(trimmed, """^\s*require(?:_relative)?\s+['"]([^'"]+)['"]"""),
            _ => null,
        };
    }

    private static IEnumerable<string> ExtractBindings(string line, SupportedLanguage language)
    {
        var trimmed = line.Trim();
        switch (language)
        {
            case SupportedLanguage.TypeScript:
            case SupportedLanguage.Tsx:
            case SupportedLanguage.JavaScript:
            {
                var @default = MatchRegex(trimmed, """import\s+(\w+)\s+from\s+""");
                if (@default is not null)
                {
                    yield return @default;
                }

                var ns = MatchRegex(trimmed, """import\s*\*\s*as\s+(\w+)\s+from\s+""");
                if (ns is not null)
                {
                    yield return ns;
                }

                var named = MatchRegex(trimmed, """import\s*\{([^}]*)\}\s*from\s+""");
                if (named is not null)
                {
                    foreach (var part in named.Split(','))
                    {
                        var name = part.Split([" as "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()?.Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            yield return name;
                        }
                    }
                }

                var required = MatchRegex(trimmed, """(?:const|let|var)\s+(\w+)\s*=\s*require\(""");
                if (required is not null)
                {
                    yield return required;
                }

                break;
            }

            case SupportedLanguage.Python:
            {
                var importMatch = Regex.Match(trimmed, """^\s*import\s+([\w\.]+)(?:\s+as\s+(\w+))?""");
                if (importMatch.Success)
                {
                    yield return importMatch.Groups[2].Success
                        ? importMatch.Groups[2].Value
                        : importMatch.Groups[1].Value.Split('.')[0];
                }

                var fromMatch = Regex.Match(trimmed, """^\s*from\s+[\w\.]+\s+import\s+(.+)$""");
                if (fromMatch.Success)
                {
                    foreach (var part in fromMatch.Groups[1].Value.Split(','))
                    {
                        var name = part.Split([" as "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()?.Trim();
                        if (!string.IsNullOrEmpty(name) && name != "*")
                        {
                            yield return name;
                        }
                    }
                }

                break;
            }

            default:
                break;
        }
    }

    private static string? MatchRegex(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }

    /// <summary>
    ///     Language-agnostic package match: accepts any language's convention (exact,
    ///     subpath, Python top-level segment, Go final path segment, or the last
    ///     hyphen segment for PyPI names whose import name differs, e.g.
    ///     python-dateutil → dateutil). Approximate by design; import edges are
    ///     labelled "import-match", never "analyzer-confirmed".
    /// </summary>
    private static bool SpecifierMatchesPackage(string specifier, string package) =>
        specifier.Equals(package, StringComparison.OrdinalIgnoreCase)
        || specifier.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase)
        || specifier.Split('.')[0].Equals(package, StringComparison.OrdinalIgnoreCase)
        || specifier.Split('/').Last().Equals(package, StringComparison.OrdinalIgnoreCase)
        || LastHyphenSegment(package).Equals(specifier.Split('.')[0], StringComparison.OrdinalIgnoreCase);

    private static string LastHyphenSegment(string package)
    {
        var index = package.LastIndexOf('-');
        return index >= 0 ? package[(index + 1)..] : package;
    }

    private static bool SpecifierMatchesFile(string specifier, string seedPath)
    {
        var seedNoExt = StripExtension(seedPath);
        var seedBase = Path.GetFileNameWithoutExtension(seedPath);
        var spec = specifier;
        while (spec.StartsWith("../", StringComparison.Ordinal))
        {
            spec = spec[3..];
        }

        spec = spec.TrimStart('.', '/');
        var specNoExt = StripExtension(spec);
        return specNoExt.Equals(seedNoExt, StringComparison.Ordinal)
               || specNoExt.Equals(seedBase, StringComparison.Ordinal)
               || specNoExt.EndsWith("/" + seedBase, StringComparison.Ordinal);
    }

    private static string StripExtension(string path)
    {
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        return lastDot > lastSlash && lastDot > 0 ? path[..lastDot] : path;
    }

    private sealed record CandidateFile(string Path, SupportedLanguage Language, string Text);

    private sealed class AnalysisState
    {
        public int FilesScanned { get; set; }
        public List<BlastRadiusSkippedFile> Skipped { get; } = [];
        public bool Truncated { get; set; }

        public BlastRadiusResponse Unavailable(string reason, BlastRadiusSeed? seed) =>
            new(
                "Unavailable", reason, seed, [], [], [], [], this.FilesScanned,
                this.Skipped.Take(MaxSkippedShown).ToList(), this.Skipped.Count, this.Truncated,
                DateTimeOffset.UtcNow);
    }

    private sealed class EdgeSet
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<BlastRadiusEdge> _edges = [];

        public IReadOnlyList<BlastRadiusEdge> Edges => _edges;
        public int Count => _edges.Count;

        public void AddReference(
            string fromFile, string? fromDefinition, int fromLine,
            string toFile, string toSymbol, int toLine)
        {
            var key = string.Join("\n", [fromFile, fromDefinition ?? "", fromLine.ToString(), toFile, toSymbol, toLine.ToString(), "analyzer-confirmed"]);
            if (!_seen.Add(key))
            {
                return;
            }

            _edges.Add(new BlastRadiusEdge(fromFile, fromDefinition, fromLine, toFile, toSymbol, toLine, "analyzer-confirmed"));
        }

        public void AddImport(string fromFile, int fromLine, BlastRadiusSeed seed)
        {
            var toSymbol = seed.Kind == "package" ? seed.PackageName! : seed.FilePath!;
            var key = string.Join("\n", [fromFile, "", fromLine.ToString(), seed.FilePath ?? "", toSymbol, "0", "import-match"]);
            if (!_seen.Add(key))
            {
                return;
            }

            _edges.Add(new BlastRadiusEdge(fromFile, null, fromLine, seed.FilePath ?? "", toSymbol, 0, "import-match"));
        }
    }

    private sealed class BlastRadiusAffectedModuleBuilder(string filePath)
    {
        private readonly Dictionary<string, BlastRadiusCaller> _callers = new(StringComparer.Ordinal);
        private readonly List<int> _importLines = [];

        public string FilePath { get; } = filePath;
        public bool HasCallers => _callers.Count > 0;

        public void AddReference(string? definition, int line)
        {
            var key = definition ?? "";
            if (!_callers.TryGetValue(key, out var caller))
            {
                caller = new BlastRadiusCaller(definition, "definition", line, line, []);
                _callers[key] = caller;
            }

            var lines = caller.ReferenceLines.ToList();
            if (!lines.Contains(line))
            {
                lines.Add(line);
            }

            lines.Sort();
            _callers[key] = caller with
            {
                StartLine = Math.Min(caller.StartLine, line),
                EndLine = Math.Max(caller.EndLine, line),
                ReferenceLines = lines,
            };
        }

        public void AddImports(IEnumerable<int> lines)
        {
            foreach (var line in lines)
            {
                if (!_importLines.Contains(line))
                {
                    _importLines.Add(line);
                }
            }

            _importLines.Sort();
        }

        public BlastRadiusAffectedModule Build() => new(
            this.FilePath,
            _callers.Values.OrderBy(c => c.StartLine).ToList(),
            _importLines);
    }
}
