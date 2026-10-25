// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.SecurityScanning;

/// <summary>
///     Configuration for deterministic security scanning. Bound from environment:
///     <c>AEGIS_SECURITY_*</c> preferred, <c>MEISTER_SECURITY_*</c> as legacy fallback
///     (the host's AEGIS→MEISTER bridge maps them before binding).
/// </summary>
public sealed class SecurityScanningOptions
{
    /// <summary>Master switch. When false, scan initiation returns 503 with a clear message.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Absolute path to the vulnveto engine checkout (must contain <c>dist/modules</c>).
    ///     When empty, the bundled engine under <c>tools/vulnveto</c> is resolved
    ///     automatically (see <see cref="ResolveDefaultEngineDir" />). If neither an
    ///     explicit path nor a built bundled engine is found, scans fail closed with
    ///     an actionable error — never silently clean.
    /// </summary>
    public string EngineDir { get; set; } = string.Empty;

    /// <summary>Node.js binary used to run the engine sidecar. Defaults to <c>node</c> on PATH.</summary>
    public string NodeBinary { get; set; } = "node";

    /// <summary>
    ///     Gitleaks binary for secret detection. Empty means "resolve via PATH".
    ///     When gitleaks is missing, secret scanning is skipped and the scan is marked
    ///     partial — never silently successful.
    /// </summary>
    public string GitleaksBinary { get; set; } = string.Empty;

    /// <summary>Root directory for per-scan temp workdirs. Defaults to the OS temp path.</summary>
    public string WorkRoot { get; set; } = string.Empty;

    /// <summary>Directory where SBOM artifacts are stored. Defaults to <c>{WorkRoot}/aegis-sboms</c>.</summary>
    public string SbomRoot { get; set; } = string.Empty;

    /// <summary>
    ///     Directory where fix-proposal JSON files are stored (one file per client).
    ///     Defaults to <c>{WorkRoot}/aegis-fix-proposals</c>. Set
    ///     <c>AEGIS_SECURITY_FIX_PROPOSALS_ROOT</c> to a persistent path on durable
    ///     deployments: the default work root is the OS temp directory.
    /// </summary>
    public string FixProposalsRoot { get; set; } = string.Empty;

    /// <summary>Maximum concurrent scans. Defaults to 2.</summary>
    public int MaxConcurrentScans { get; set; } = 2;

    /// <summary>Wall-clock timeout per scan, in seconds. Defaults to 600.</summary>
    public int ScanTimeoutSeconds { get; set; } = 600;

    /// <summary>Timeout for the initial git clone, in seconds. Defaults to 120.</summary>
    public int CloneTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum cloned repository size, in megabytes. Defaults to 512.</summary>
    public int MaxRepoSizeMb { get; set; } = 512;

    /// <summary>Maximum files walked per scan. Defaults to 50,000.</summary>
    public int MaxFileCount { get; set; } = 50_000;

    /// <summary>
    ///     Allowed repository hosts for scanning. Defaults to <c>github.com</c> only.
    ///     Only https URLs on these hosts are accepted (SSRF protection).
    /// </summary>
    public string[] AllowedHosts { get; set; } = ["github.com"];

    /// <summary>OSV.dev API base URL. Defaults to <c>https://api.osv.dev</c>.</summary>
    public string OsvBaseUrl { get; set; } = "https://api.osv.dev";

    /// <summary>Binds <c>AEGIS_SECURITY_*</c> / <c>MEISTER_SECURITY_*</c> keys onto this options instance.</summary>
    public static void Bind(SecurityScanningOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        string? Get(string aegisKey, string meisterKey) =>
            configuration[$"AEGIS_SECURITY_{aegisKey}"] ?? configuration[$"MEISTER_SECURITY_{meisterKey}"];

        options.Enabled = bool.TryParse(Get("ENABLED", "ENABLED"), out var enabled) ? enabled : true;
        options.EngineDir = Get("ENGINE_DIR", "ENGINE_DIR") ?? ResolveDefaultEngineDir() ?? string.Empty;
        options.NodeBinary = Get("NODE_BINARY", "NODE_BINARY") ?? "node";
        options.GitleaksBinary = Get("GITLEAKS_BINARY", "GITLEAKS_BINARY") ?? string.Empty;
        options.WorkRoot = Get("WORK_ROOT", "WORK_ROOT") ?? string.Empty;
        options.SbomRoot = Get("SBOM_ROOT", "SBOM_ROOT") ?? string.Empty;
        options.FixProposalsRoot = Get("FIX_PROPOSALS_ROOT", "FIX_PROPOSALS_ROOT") ?? string.Empty;

        if (int.TryParse(Get("MAX_CONCURRENT_SCANS", "MAX_CONCURRENT_SCANS"), out var maxConc) && maxConc > 0)
        {
            options.MaxConcurrentScans = Math.Min(maxConc, 16);
        }
        if (int.TryParse(Get("SCAN_TIMEOUT_SECONDS", "SCAN_TIMEOUT_SECONDS"), out var scanTimeout) && scanTimeout > 0)
        {
            options.ScanTimeoutSeconds = Math.Min(scanTimeout, 3600);
        }
        if (int.TryParse(Get("CLONE_TIMEOUT_SECONDS", "CLONE_TIMEOUT_SECONDS"), out var cloneTimeout) && cloneTimeout > 0)
        {
            options.CloneTimeoutSeconds = Math.Min(cloneTimeout, 600);
        }
        if (int.TryParse(Get("MAX_REPO_SIZE_MB", "MAX_REPO_SIZE_MB"), out var maxMb) && maxMb > 0)
        {
            options.MaxRepoSizeMb = Math.Min(maxMb, 4096);
        }
        if (int.TryParse(Get("MAX_FILE_COUNT", "MAX_FILE_COUNT"), out var maxFiles) && maxFiles > 0)
        {
            options.MaxFileCount = Math.Min(maxFiles, 500_000);
        }

        var hosts = Get("ALLOWED_HOSTS", "ALLOWED_HOSTS");
        if (!string.IsNullOrWhiteSpace(hosts))
        {
            options.AllowedHosts = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        options.OsvBaseUrl = Get("OSV_BASE_URL", "OSV_BASE_URL") ?? "https://api.osv.dev";
    }

    /// <summary>
    ///     Resolves the bundled vulnveto engine directory without any environment
    ///     configuration, so a normal checkout works out of the box once the engine
    ///     has been built (<c>tools/vulnveto: npm ci &amp;&amp; npm run build</c>).
    ///     Returns <c>null</c> when no built bundled engine is found.
    ///     Search order (first directory containing <c>dist/modules</c> wins):
    ///     <list type="number">
    ///         <item><c>tools/vulnveto</c> next to the application binaries (published layout).</item>
    ///         <item>Walking up from the application binaries (development layout:
    ///         <c>src/MeisterDev.ProPR.Api/bin/Debug/net10.0/</c> up to the repo root).</item>
    ///         <item>Walking up from the current working directory.</item>
    ///     </list>
    ///     Only directories whose <c>dist/modules</c> subdirectory exists are
    ///     accepted, so an unbuilt checkout never masquerades as a working engine.
    ///     Path handling uses System.IO.Path.Combine throughout, so paths
    ///     containing spaces work on Windows and Linux alike.
    /// </summary>
    public static string? ResolveDefaultEngineDir()
    {
        var candidates = new List<string>();

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "tools", "vulnveto"));
        candidates.AddRange(WalkUpForEngine(baseDir));

        try
        {
            candidates.AddRange(WalkUpForEngine(Directory.GetCurrentDirectory()));
        }
        catch (IOException) { /* current directory unavailable; ignore */ }
        catch (UnauthorizedAccessException) { /* ignore */ }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(candidate, "dist", "modules")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> WalkUpForEngine(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            yield return Path.Combine(dir.FullName, "tools", "vulnveto");
            dir = dir.Parent;
        }
    }
}
