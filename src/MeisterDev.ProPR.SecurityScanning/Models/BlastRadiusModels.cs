// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>What the blast-radius analysis traced the impact from.</summary>
public sealed record BlastRadiusSeed(
    string Kind, // "file" | "package"
    string? FilePath,
    string? PackageName,
    string? Language,
    string? Symbol);

/// <summary>
///     One verified impact link: a caller that reaches the vulnerable code.
///     <see cref="Verification" /> is "analyzer-confirmed" for identifier occurrences
///     the structural analyzer confirmed as real code (comments and string literals
///     excluded), or "import-match" for an import statement the analyzer's code-only
///     projection matched. Matches are name-based, never semantic.
/// </summary>
public sealed record BlastRadiusEdge(
    string FromFile,
    string? FromDefinition,
    int FromLine,
    string ToFile,
    string ToSymbol,
    int ToLine,
    string Verification);

/// <summary>One definition in an affected file that reaches the vulnerable code.</summary>
public sealed record BlastRadiusCaller(
    string? Name,
    string Kind,
    int StartLine,
    int EndLine,
    IReadOnlyList<int> ReferenceLines);

/// <summary>A file that calls into (or imports) the vulnerable code.</summary>
public sealed record BlastRadiusAffectedModule(
    string FilePath,
    IReadOnlyList<BlastRadiusCaller> Callers,
    IReadOnlyList<int> ImportLines);

/// <summary>An outermost caller: nothing in the analyzed files calls this.</summary>
public sealed record BlastRadiusEntryPoint(
    string FilePath,
    string? Definition,
    string Kind,
    int Line);

/// <summary>A file the analysis skipped, with the honest reason.</summary>
public sealed record BlastRadiusSkippedFile(string Path, string Reason);

/// <summary>
///     Blast-radius map for one finding. When <see cref="Status" /> is
///     "Unavailable", the map is empty and <see cref="Reason" /> says why — the
///     analysis never invents a map it could not compute.
/// </summary>
public sealed record BlastRadiusResponse(
    string Status, // "Available" | "Unavailable"
    string? Reason,
    BlastRadiusSeed? Seed,
    IReadOnlyList<BlastRadiusAffectedModule> AffectedModules,
    IReadOnlyList<BlastRadiusEntryPoint> EntryPoints,
    IReadOnlyList<BlastRadiusEdge> Edges,
    IReadOnlyList<string> Notes,
    int FilesScanned,
    IReadOnlyList<BlastRadiusSkippedFile> SkippedFiles,
    int TotalSkipped,
    bool Truncated,
    DateTimeOffset AnalyzedAt);
