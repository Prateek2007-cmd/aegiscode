// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Blast-radius impact computation against a fake structural analyzer: known
///     caller → shown, unrelated module → not shown, unsupported language →
///     honest Unavailable. The fake answers from per-file tables so the tests
///     verify the graph logic (hop depth, entry points, import tracing), not a
///     real parser backend.
/// </summary>
public sealed class BlastRadiusImpactAnalyzerTests
{
    private const string SeedFile = "src/vuln.ts";
    private const string SeedText = "export function vulnerable(x: string): string {\n  return x;\n}\n";
    private const string CallerFile = "src/use.ts";
    private const string CallerText = "import { vulnerable } from './vuln';\nexport function run(): void {\n  vulnerable('x');\n}\n";
    private const string EntryFile = "src/entry.ts";
    private const string EntryText = "import { run } from './use';\nexport function main(): void {\n  run();\n}\n";
    private const string UnrelatedFile = "src/other.ts";
    private const string UnrelatedText = "export function unrelated(): void {\n  console.log('hi');\n}\n";

    [Fact]
    public async Task FileSeed_KnownCallerIsShown_UnrelatedModuleIsNot()
    {
        var analyzer = new FakeAnalyzer();
        analyzer.Definitions[SeedFile] = [new DefinitionSummary(DefinitionKind.Function, "vulnerable", 1, 3)];
        analyzer.Definitions[CallerFile] = [new DefinitionSummary(DefinitionKind.Function, "run", 2, 4)];
        analyzer.Definitions[EntryFile] = [new DefinitionSummary(DefinitionKind.Function, "main", 2, 4)];
        analyzer.Definitions[UnrelatedFile] = [new DefinitionSummary(DefinitionKind.Function, "unrelated", 1, 3)];
        analyzer.References[(CallerFile, "vulnerable")] = [3];
        analyzer.References[(EntryFile, "run")] = [3];

        var input = new BlastRadiusInput(
            "file", SeedFile, 1, null,
            new Dictionary<string, string>
            {
                [SeedFile] = SeedText,
                [CallerFile] = CallerText,
                [EntryFile] = EntryText,
                [UnrelatedFile] = UnrelatedText,
            });
        var result = await new BlastRadiusImpactAnalyzer(analyzer).AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal("Available", result.Status);
        Assert.Equal(["src/entry.ts", "src/use.ts"], result.AffectedModules.Select(m => m.FilePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(result.AffectedModules, m => m.FilePath == UnrelatedFile);
        Assert.Equal(2, result.Edges.Count);
        Assert.All(result.Edges, e => Assert.Equal("analyzer-confirmed", e.Verification));
        var useModule = result.AffectedModules.Single(m => m.FilePath == CallerFile);
        Assert.Equal(["run"], useModule.Callers.Select(c => c.Name).ToArray());
        Assert.Single(result.EntryPoints);
        Assert.Equal("main", result.EntryPoints[0].Definition);
        Assert.Equal(EntryFile, result.EntryPoints[0].FilePath);
    }

    [Fact]
    public async Task FileSeed_NoCallers_ReportsEmptyHonestly()
    {
        var analyzer = new FakeAnalyzer();
        analyzer.Definitions[SeedFile] = [new DefinitionSummary(DefinitionKind.Function, "vulnerable", 1, 3)];

        var input = new BlastRadiusInput(
            "file", SeedFile, 1, null,
            new Dictionary<string, string> { [SeedFile] = SeedText });
        var result = await new BlastRadiusImpactAnalyzer(analyzer).AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal("Available", result.Status);
        Assert.Empty(result.AffectedModules);
        Assert.Empty(result.EntryPoints);
        Assert.Contains("No callers found", result.Notes[0]);
    }

    [Fact]
    public async Task FileSeed_UnsupportedLanguage_IsUnavailable()
    {
        var analyzer = new FakeAnalyzer();
        var input = new BlastRadiusInput(
            "file", "docs/notes.md", null, null,
            new Dictionary<string, string> { ["docs/notes.md"] = "# notes\n" });
        var result = await new BlastRadiusImpactAnalyzer(analyzer).AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("does not support", result.Reason);
        Assert.Empty(result.AffectedModules);
    }

    [Fact]
    public async Task PackageSeed_ImportersAreShown_CleanFileIsNot()
    {
        const string appFile = "src/app.ts";
        const string appText = "import lodash from 'lodash';\nexport function main(): void {\n  lodash.get({}, 'a');\n}\n";
        const string cleanFile = "src/clean.ts";
        const string cleanText = "export function clean(): void {\n  console.log('clean');\n}\n";

        var analyzer = new FakeAnalyzer();
        analyzer.Definitions[appFile] = [new DefinitionSummary(DefinitionKind.Function, "main", 2, 4)];
        analyzer.Definitions[cleanFile] = [new DefinitionSummary(DefinitionKind.Function, "clean", 1, 3)];
        analyzer.References[(appFile, "lodash")] = [3];

        var input = new BlastRadiusInput(
            "package", null, null, "lodash",
            new Dictionary<string, string> { [appFile] = appText, [cleanFile] = cleanText });
        var result = await new BlastRadiusImpactAnalyzer(analyzer).AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal("Available", result.Status);
        var module = Assert.Single(result.AffectedModules);
        Assert.Equal(appFile, module.FilePath);
        Assert.Equal([1], module.ImportLines);
        Assert.Equal(["main"], module.Callers.Select(c => c.Name).ToArray());
        Assert.Contains(result.Edges, e => e.Verification == "import-match" && e.FromFile == appFile && e.FromLine == 1);
        Assert.Single(result.EntryPoints);
        Assert.Equal("main", result.EntryPoints[0].Definition);
    }

    [Fact]
    public async Task AnalyzerUnavailable_IsUnavailable()
    {
        var analyzer = new FakeAnalyzer { IsAvailable = false };
        var input = new BlastRadiusInput(
            "file", SeedFile, 1, null,
            new Dictionary<string, string> { [SeedFile] = SeedText });
        var result = await new BlastRadiusImpactAnalyzer(analyzer).AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("unavailable", result.Reason);
    }

    /// <summary>Table-driven fake: definitions, confirmed reference lines, and enclosing definitions per file.</summary>
    private sealed class FakeAnalyzer : IStructuralCodeAnalyzer
    {
        private static readonly HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts", ".tsx", ".js", ".py", ".go", ".java", ".rb" };

        public bool IsAvailable { get; set; } = true;

        public Dictionary<string, List<DefinitionSummary>> Definitions { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string File, string Symbol), List<int>> References { get; } = new();

        public bool CanAnalyze(string path) =>
            this.IsAvailable && SupportedExtensions.Contains(Path.GetExtension(path));

        public Task<IReadOnlyList<DefinitionSummary>> GetDefinitionsAsync(StructuralParseRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DefinitionSummary>>(this.Definitions.TryGetValue(request.Path, out var defs) ? defs : []);

        public Task<IReadOnlyList<EnclosingDefinition>> ResolveEnclosingDefinitionsAsync(StructuralParseRequest request, CancellationToken ct)
        {
            var line = request.ChangedLineRanges.Count > 0 ? request.ChangedLineRanges[0].Start : 0;
            if (!this.Definitions.TryGetValue(request.Path, out var defs))
            {
                return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
            }

            var def = defs.FirstOrDefault(d => line >= d.StartLine && line <= d.EndLine);
            if (def is null)
            {
                return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
            }

            return Task.FromResult<IReadOnlyList<EnclosingDefinition>>(
            [
                new EnclosingDefinition(def.Kind, def.Name, def.StartLine, def.EndLine, def.EndLine - def.StartLine + 1),
            ]);
        }

        public Task<IReadOnlyList<int>> ConfirmReferenceLinesAsync(StructuralParseRequest request, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>>(this.References.TryGetValue((request.Path, symbol), out var lines) ? lines : []);

        public Task<string> ExtractCodeTextAsync(StructuralParseRequest request, CancellationToken ct) =>
            Task.FromResult(request.SourceText);
    }
}
