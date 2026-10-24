// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text.Json;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Dependency-check outcome for the scan engine.</summary>
public sealed record EngineDependencyCheck(
    bool NodeAvailable,
    bool EngineAvailable,
    bool GitleaksAvailable,
    string? NodeVersion,
    string? Detail);

/// <summary>
///     Invokes the deterministic scan engine (vulnveto modules via the Node sidecar harness)
///     against a safely-cloned repository. Never executes repository scripts or build hooks;
///     never lets repository content influence process arguments (ArgumentList only).
/// </summary>
public interface ISecurityScanEngineRunner
{
    /// <summary>Checks node, engine checkout, and gitleaks availability. Never throws.</summary>
    Task<EngineDependencyCheck> CheckDependenciesAsync(CancellationToken ct);

    /// <summary>
    ///     Clones <paramref name="repositoryUrl" /> into an isolated temp dir, runs the engine,
    ///     and returns parsed findings. The temp dir is always cleaned up.
    ///     Throws <see cref="SecurityScanEngineException" /> with user-safe messages on failure.
    /// </summary>
    Task<EngineScanResult> RunAsync(
        Guid scanId,
        string repositoryUrl,
        string? gitRef,
        IReadOnlyList<string> scanners,
        IProgress<int> progress,
        CancellationToken ct);
}

/// <summary>User-safe engine failure (message is shown to users; no secrets, no stack traces).</summary>
public sealed class SecurityScanEngineException(string message) : Exception(message);

/// <inheritdoc />
public sealed class SecurityScanEngineRunner(
    IOptions<SecurityScanningOptions> options,
    ILogger<SecurityScanEngineRunner> logger) : ISecurityScanEngineRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    ///     Effective engine directory: the explicit <c>EngineDir</c> option when set,
    ///     otherwise the bundled <c>tools/vulnveto</c> engine resolved relative to
    ///     the checkout. Centralized so the dependency check and the scan run
    ///     always agree on which engine they mean.
    /// </summary>
    internal static string? EffectiveEngineDir(SecurityScanningOptions opts) =>
        string.IsNullOrWhiteSpace(opts.EngineDir)
            ? SecurityScanningOptions.ResolveDefaultEngineDir()
            : opts.EngineDir;

    /// <inheritdoc />
    public async Task<EngineDependencyCheck> CheckDependenciesAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var nodeVersion = await this.TryGetVersionAsync(opts.NodeBinary, ["--version"], ct);
        var engineDir = SecurityScanEngineRunner.EffectiveEngineDir(opts);
        var engineAvailable = engineDir is not null
            && Directory.Exists(Path.Combine(engineDir, "dist", "modules"));
        var gitleaksBinary = string.IsNullOrWhiteSpace(opts.GitleaksBinary) ? "gitleaks" : opts.GitleaksBinary;
        var gitleaksVersion = await this.TryGetVersionAsync(gitleaksBinary, ["version"], ct);

        var detail = engineAvailable
            ? null
            : "Scan engine not found: no built bundled engine under tools/vulnveto and no AEGIS_SECURITY_ENGINE_DIR set. "
            + "Build the bundled engine (cd tools/vulnveto && npm ci && npm run build, or scripts/Build-VulnvetoEngine.ps1 on Windows), "
            + "or set AEGIS_SECURITY_ENGINE_DIR to a built engine directory containing dist/modules.";

        return new EngineDependencyCheck(
            nodeVersion is not null,
            engineAvailable,
            gitleaksVersion is not null,
            nodeVersion,
            detail);
    }

    /// <inheritdoc />
    public async Task<EngineScanResult> RunAsync(
        Guid scanId,
        string repositoryUrl,
        string? gitRef,
        IReadOnlyList<string> scanners,
        IProgress<int> progress,
        CancellationToken ct)
    {
        var opts = options.Value;

        var deps = await this.CheckDependenciesAsync(ct);
        if (!deps.NodeAvailable)
        {
            throw new SecurityScanEngineException(
                $"Node.js is not available ('{opts.NodeBinary}'). Install Node 20+ and ensure it is on PATH.");
        }
        if (!deps.EngineAvailable)
        {
            throw new SecurityScanEngineException(deps.Detail ?? "Scan engine is not configured.");
        }
        var engineDir = SecurityScanEngineRunner.EffectiveEngineDir(opts)
            ?? throw new SecurityScanEngineException(deps.Detail ?? "Scan engine is not configured.");

        var normalizedUrl = SecurityScanUrlValidator.ValidateAndNormalize(repositoryUrl, opts.AllowedHosts);

        var workRoot = string.IsNullOrWhiteSpace(opts.WorkRoot) ? Path.GetTempPath() : opts.WorkRoot;
        Directory.CreateDirectory(workRoot);
        var workDir = Path.Combine(workRoot, $"aegis-scan-{scanId:N}");
        var sbomRoot = string.IsNullOrWhiteSpace(opts.SbomRoot)
            ? Path.Combine(workRoot, "aegis-sboms")
            : opts.SbomRoot;
        Directory.CreateDirectory(sbomRoot);
        var sbomPath = Path.Combine(sbomRoot, $"{scanId:N}.cyclonedx.json");

        try
        {
            progress.Report(5);
            await this.CloneRepositoryAsync(normalizedUrl, gitRef, workDir, opts, ct);
            progress.Report(25);

            var harnessPath = GetHarnessPath();
            var result = await this.RunHarnessAsync(
                opts, engineDir, harnessPath, workDir, sbomPath, scanners, opts.ScanTimeoutSeconds, progress, ct);
            progress.Report(90);

            return this.ToEngineResult(result, sbomPath);
        }
        finally
        {
            // Always clean up the clone. The SBOM artifact is kept under sbomRoot.
            this.TryDeleteDirectory(workDir);
            progress.Report(100);
        }
    }

    private static string GetHarnessPath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(SecurityScanEngineRunner).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var harness = Path.Combine(assemblyDir, "Resources", "scan-harness.mjs");
        if (!File.Exists(harness))
        {
            throw new SecurityScanEngineException("Scan harness (scan-harness.mjs) is missing from the module output.");
        }
        return harness;
    }

    private async Task CloneRepositoryAsync(
        string url, string? gitRef, string workDir, SecurityScanningOptions opts, CancellationToken ct)
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

        // Size cap: clone into the dir, then verify before scanning.
        var (exitCode, _, stderr) = await RunProcessAsync(psi, cts.Token);
        if (exitCode != 0)
        {
            throw new SecurityScanEngineException(
                $"Could not clone repository (git exit {exitCode}). Check the URL and that the repository is public. Details: {Truncate(stderr, 300)}");
        }

        var sizeMb = GetDirectorySizeMb(workDir);
        if (sizeMb > opts.MaxRepoSizeMb)
        {
            throw new SecurityScanEngineException(
                $"Repository too large ({sizeMb:F0} MB > {opts.MaxRepoSizeMb} MB limit).");
        }

        var fileCount = CountFiles(workDir, opts.MaxFileCount + 1);
        if (fileCount > opts.MaxFileCount)
        {
            throw new SecurityScanEngineException(
                $"Repository has too many files ({fileCount} > {opts.MaxFileCount} limit).");
        }
    }

    private async Task<HarnessOutput> RunHarnessAsync(
        SecurityScanningOptions opts,
        string engineDir,
        string harnessPath,
        string workDir,
        string sbomPath,
        IReadOnlyList<string> scanners,
        int timeoutSeconds,
        IProgress<int> progress,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var psi = new ProcessStartInfo(opts.NodeBinary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(harnessPath);
        psi.ArgumentList.Add(engineDir);
        psi.ArgumentList.Add(workDir);
        psi.ArgumentList.Add(sbomPath);
        psi.Environment["VULNVETO_AI"] = "none"; // deterministic only; never send code to AI
        psi.Environment["SCANNERS"] = string.Join(",", scanners);
        if (!string.IsNullOrWhiteSpace(opts.GitleaksBinary))
        {
            psi.Environment["GITLEAKS_BINARY"] = opts.GitleaksBinary;
        }
        if (!string.IsNullOrWhiteSpace(opts.OsvBaseUrl) && opts.OsvBaseUrl != "https://api.osv.dev")
        {
            psi.Environment["OSV_BASE_URL"] = opts.OsvBaseUrl;
        }
        // Keep the engine's PATH predictable but allow gitleaks resolution.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localBin = Path.Combine(home, ".local", "bin");
        if (Directory.Exists(localBin))
        {
            psi.Environment["PATH"] = localBin + Path.PathSeparator + psi.Environment["PATH"];
        }

        progress.Report(40);
        var (exitCode, stdout, stderr) = await RunProcessAsync(psi, cts.Token);
        progress.Report(80);

        if (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SecurityScanEngineException($"Scan timed out after {timeoutSeconds} seconds.");
        }

        if (exitCode != 0)
        {
            logger.LogWarning("Scan harness exited {ExitCode}: {Stderr}", exitCode, Truncate(stderr, 500));
            throw new SecurityScanEngineException(
                $"Scan engine failed (exit {exitCode}). Details: {Truncate(stderr, 300)}");
        }

        HarnessOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<HarnessOutput>(stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Scan harness produced invalid JSON");
            throw new SecurityScanEngineException("Scan engine produced unreadable output.");
        }

        if (output is null)
        {
            throw new SecurityScanEngineException("Scan engine produced empty output.");
        }

        return output;
    }

    private EngineScanResult ToEngineResult(HarnessOutput output, string sbomPath)
    {
        var findings = new List<EngineFinding>(output.Findings.Count);
        foreach (var f in output.Findings)
        {
            // Defense in depth: re-verify redaction before anything is persisted.
            var evidence = f.Evidence;
            var remediation = f.Remediation;
            if (SecurityScanRedactor.LooksLikeUnredactedSecret(evidence)
                || SecurityScanRedactor.LooksLikeUnredactedSecret(remediation)
                || SecurityScanRedactor.LooksLikeUnredactedSecret(f.Title))
            {
                logger.LogWarning("Redaction verifier quarantined a finding from scanner {Scanner}", f.Scanner);
                evidence = SecurityScanRedactor.ForceRedact(evidence);
                remediation = SecurityScanRedactor.ForceRedact(remediation);
            }

            findings.Add(new EngineFinding(
                f.Scanner ?? "unknown",
                f.RuleId,
                NormalizeSeverity(f.Severity),
                NormalizeConfidence(f.Confidence),
                Truncate(f.Title ?? "Untitled finding", 512),
                f.Category ?? "other",
                f.FilePath,
                f.Line,
                evidence,
                remediation,
                f.PackageName,
                f.PackageVersion,
                f.FixedVersion));
        }

        var coverage = new SecurityScanCoverage(
            output.Coverage?.ManifestsFound ?? [],
            output.Coverage?.FilesScanned ?? 0,
            output.Coverage?.ScannersRun ?? [],
            output.Coverage?.ScannersFailed ?? [],
            output.Coverage?.ScannersSkipped ?? []);

        string? sbomJson = null;
        if (File.Exists(sbomPath))
        {
            try { sbomJson = File.ReadAllText(sbomPath); }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not read SBOM artifact at {Path}", sbomPath);
            }
        }

        return new EngineScanResult(findings, sbomJson, coverage, output.IsPartial, output.PartialReason);
    }

    private static string NormalizeSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => "critical",
        "high" => "high",
        "medium" => "medium",
        "low" => "low",
        _ => "info",
    };

    private static string NormalizeConfidence(string? confidence) => confidence?.ToLowerInvariant() switch
    {
        "high" => "high",
        "low" => "low",
        _ => "medium",
    };

    private async Task<string?> TryGetVersionAsync(string binary, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (exitCode, stdout, _) = await RunProcessAsync(psi, cts.Token);
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
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
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static double GetDirectorySizeMb(string dir)
    {
        double bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(file).Length; }
            catch (IOException) { /* raced deletion; ignore */ }
        }
        return bytes / (1024 * 1024);
    }

    private static int CountFiles(string dir, int cap)
    {
        var count = 0;
        foreach (var _ in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (++count >= cap)
            {
                break;
            }
        }
        return count;
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
            logger.LogWarning(ex, "Could not clean up scan workdir {Dir}", dir);
        }
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength] + "…";

    private sealed record HarnessCoverage(
        IReadOnlyList<string> ManifestsFound,
        int FilesScanned,
        IReadOnlyList<string> ScannersRun,
        IReadOnlyList<string> ScannersFailed,
        IReadOnlyList<string> ScannersSkipped);

    private sealed record HarnessFinding(
        string? Scanner,
        string? RuleId,
        string? Severity,
        string? Confidence,
        string? Title,
        string? Category,
        string? FilePath,
        int? Line,
        string? Evidence,
        string? Remediation,
        string? PackageName,
        string? PackageVersion,
        string? FixedVersion);

    private sealed record HarnessOutput(
        IReadOnlyList<HarnessFinding> Findings,
        HarnessCoverage? Coverage,
        bool IsPartial,
        string? PartialReason,
        string? EngineVersion);
}
