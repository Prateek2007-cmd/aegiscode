// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Computes the blast-radius map for one finding. Returns null when the scan or finding does not exist.</summary>
public interface IBlastRadiusService
{
    Task<BlastRadiusResponse?> GetBlastRadiusAsync(Guid clientId, Guid scanId, Guid findingId, CancellationToken ct);
}

/// <summary>
///     Blast-radius orchestration: loads the finding, clones the scanned repository
///     shallowly into an isolated temp dir (same safe clone posture as the scan
///     engine: no hooks, no file protocol, size/file caps, always cleaned up),
///     enumerates its readable files, and runs the impact analysis. The clone is
///     re-made per request — no repository state is kept between calls. Every
///     failure mode reports an honest Unavailable result, never an invented map.
/// </summary>
public sealed class BlastRadiusService(
    MeisterProPRDbContext db,
    IOptions<SecurityScanningOptions> options,
    ILogger<BlastRadiusService> logger,
    IStructuralCodeAnalyzer? analyzer = null) : IBlastRadiusService
{
    /// <summary>Wall-clock budget for the analysis itself (clone excluded), in seconds.</summary>
    public const int AnalysisTimeoutSeconds = 120;

    /// <summary>Maximum readable file size passed to the analyzer, in bytes.</summary>
    public const int MaxReadableFileBytes = 1_000_000;

    private static readonly string[] SkippedDirectories =
    [
        ".git", "node_modules", "dist", "build", "out", "target", "vendor",
        "bin", "obj", ".vs", ".idea", "coverage", "__pycache__", ".next", ".nuxt",
        "Pods", "DerivedData",
    ];

    private static readonly Regex DepScanTitlePattern = new(
        @"^(.+)@([0-9][^:\s]*): ",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task<BlastRadiusResponse?> GetBlastRadiusAsync(Guid clientId, Guid scanId, Guid findingId, CancellationToken ct)
    {
        var scan = await db.SecurityScans
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scanId && s.ClientId == clientId, ct);
        if (scan is null)
        {
            return null;
        }

        var finding = await db.SecurityFindings
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == findingId && f.ClientId == clientId && f.ScanId == scanId, ct);
        if (finding is null)
        {
            return null;
        }

        if (analyzer is null || !analyzer.IsAvailable)
        {
            return Unavailable(
                "Code analysis is unavailable on this server (no parser backend loaded).",
                SeedFor(finding));
        }

        var (seedKind, seedFile, seedLine, package) = ResolveSeed(finding);
        if (seedKind is null)
        {
            return Unavailable(
                "This finding has no file location and no readable package name, so there is nothing to trace from. "
                + "File findings trace from their file; dependency findings trace from their package.",
                SeedFor(finding));
        }

        var opts = options.Value;
        string normalizedUrl;
        try
        {
            normalizedUrl = SecurityScanUrlValidator.ValidateAndNormalize(scan.RepositoryUrl, opts.AllowedHosts);
        }
        catch (ArgumentException ex)
        {
            return Unavailable($"The scanned repository URL is no longer valid: {ex.Message}", SeedFor(finding));
        }

        var workRoot = string.IsNullOrWhiteSpace(opts.WorkRoot) ? Path.GetTempPath() : opts.WorkRoot;
        Directory.CreateDirectory(workRoot);
        var workDir = Path.Combine(workRoot, $"aegis-blast-{scanId:N}-{findingId:N}");

        try
        {
            await this.CloneRepositoryAsync(normalizedUrl, scan.Ref, workDir, opts, ct);
        }
        catch (SecurityScanEngineException ex)
        {
            return Unavailable(
                $"Could not clone the repository to analyze it ({ex.Message}). "
                + "The repository may be private or unreachable; blast radius needs a fresh clone of the scanned repository.",
                SeedFor(finding));
        }

        try
        {
            var (files, skipped, hitFileCap) = this.EnumerateReadableFiles(workDir, opts);
            var input = new BlastRadiusInput(seedKind, seedFile, seedLine, package, files);
            using var analysisTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            analysisTimeout.CancelAfter(TimeSpan.FromSeconds(AnalysisTimeoutSeconds));
            try
            {
                var impactAnalyzer = new BlastRadiusImpactAnalyzer(analyzer);
                var response = await impactAnalyzer.AnalyzeAsync(input, analysisTimeout.Token);
                return MergeSkipped(response, skipped, hitFileCap);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Unavailable(
                    $"Blast-radius analysis timed out after {AnalysisTimeoutSeconds} seconds. The repository may be too large to trace in one request.",
                    SeedFor(finding));
            }
        }
        finally
        {
            this.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    ///     Resolves the trace seed from the finding. File findings trace from their
    ///     file; dep-scan findings carry no file, so the package name is recovered
    ///     from the engine's fixed title format (<c>name@version: summary</c>).
    /// </summary>
    private static (string? Kind, string? File, int? Line, string? Package) ResolveSeed(SecurityFindingRecord finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.FilePath))
        {
            return ("file", finding.FilePath, finding.Line, null);
        }

        if (string.Equals(finding.Scanner, SecurityScanners.DepScan, StringComparison.Ordinal)
            && TryParseDepScanPackage(finding.Title) is { } parsed)
        {
            return ("package", null, null, parsed.Name);
        }

        return (null, null, null, null);
    }

    private static BlastRadiusSeed? SeedFor(SecurityFindingRecord finding)
    {
        var (kind, file, _, package) = ResolveSeed(finding);
        return kind is null ? null : new BlastRadiusSeed(kind, file, package, null, null);
    }

    /// <summary>
    ///     Parses the dep-scan engine's fixed title format (<c>name@version: summary</c>).
    ///     Returns null for any other shape — the caller reports Unavailable rather
    ///     than guessing a package name.
    /// </summary>
    internal static (string Name, string Version)? TryParseDepScanPackage(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var match = DepScanTitlePattern.Match(title);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static BlastRadiusResponse Unavailable(string reason, BlastRadiusSeed? seed) =>
        new("Unavailable", reason, seed, [], [], [], [], 0, [], 0, false, DateTimeOffset.UtcNow);

    private static BlastRadiusResponse MergeSkipped(BlastRadiusResponse response, IReadOnlyList<BlastRadiusSkippedFile> skipped, bool hitFileCap)
    {
        if (skipped.Count == 0 && !hitFileCap)
        {
            return response;
        }

        var merged = response.SkippedFiles.Concat(skipped).Take(BlastRadiusImpactAnalyzer.MaxSkippedShown).ToList();
        return response with
        {
            SkippedFiles = merged,
            TotalSkipped = response.TotalSkipped + skipped.Count,
            Truncated = response.Truncated || hitFileCap,
        };
    }

    private (Dictionary<string, string> Files, List<BlastRadiusSkippedFile> Skipped, bool HitFileCap) EnumerateReadableFiles(
        string workDir, SecurityScanningOptions opts)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<BlastRadiusSkippedFile>();
        var count = 0;
        var hitFileCap = false;

        foreach (var fullPath in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workDir, fullPath).Replace('\\', '/');
            if (IsInSkippedDirectory(relative) || IsGeneratedFile(relative))
            {
                continue;
            }

            if (++count > opts.MaxFileCount)
            {
                skipped.Add(new BlastRadiusSkippedFile(relative, "file-count limit reached"));
                hitFileCap = true;
                break;
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaxReadableFileBytes)
            {
                skipped.Add(new BlastRadiusSkippedFile(relative, "file too large"));
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                if (bytes.Contains((byte)0))
                {
                    skipped.Add(new BlastRadiusSkippedFile(relative, "binary file"));
                    continue;
                }

                files[relative] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                skipped.Add(new BlastRadiusSkippedFile(relative, "unreadable"));
            }
        }

        return (files, skipped, hitFileCap);
    }

    private static bool IsInSkippedDirectory(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.StartsWith('.') && segment.Length > 1)
            {
                return true;
            }

            if (SkippedDirectories.Contains(segment, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedFile(string relativePath) =>
        relativePath.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
        || (relativePath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            && !relativePath.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase));

    private async Task CloneRepositoryAsync(string url, string? gitRef, string workDir, SecurityScanningOptions opts, CancellationToken ct)
    {
        // --depth 1, no tags, no hooks, no LFS smudge: a malicious repo must not get code execution.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.CloneTimeoutSeconds));

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.hooksPath=/dev/null");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=never");
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--no-tags");
        psi.ArgumentList.Add("--no-recurse-submodules");
        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            psi.ArgumentList.Add("--branch");
            psi.ArgumentList.Add(gitRef);
        }

        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add(workDir);

        var (exitCode, _, stderr) = await RunProcessAsync(psi, cts.Token);
        if (exitCode != 0)
        {
            throw new SecurityScanEngineException(
                $"Could not clone repository (git exit {exitCode}). Check the URL and that the repository is public. Details: {Truncate(stderr, 300)}");
        }

        var sizeMb = GetDirectorySizeMb(workDir);
        if (sizeMb > opts.MaxRepoSizeMb)
        {
            throw new SecurityScanEngineException($"Repository too large ({sizeMb:F0} MB > {opts.MaxRepoSizeMb} MB limit).");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                /* already exited */
            }

            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static double GetDirectorySizeMb(string dir)
    {
        double bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                /* raced deletion; ignore */
            }
        }

        return bytes / (1024 * 1024);
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up blast-radius workdir {Dir}", dir);
        }
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength] + "…";
}
