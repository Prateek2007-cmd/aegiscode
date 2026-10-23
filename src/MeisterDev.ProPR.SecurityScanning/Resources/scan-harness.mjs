/**
 * AegisCode security-engine sidecar harness.
 *
 * Copyright (c) Andreas Rain.
 * Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
 *
 * Contract:
 *   argv[2] = absolute path to the vulnveto engine checkout (must contain dist/modules)
 *   argv[3] = absolute path to the cloned repository to scan
 *   argv[4] = absolute path to write the CycloneDX SBOM (sbom module writes relative to cwd;
 *             this harness chdirs to the SBOM dir first, then moves the file)
 *   env GITLEAKS_BINARY = gitleaks binary override (optional)
 *   env VULNVETO_AI = ai backend (sidecar always runs deterministic; default "none")
 *   env SCANNERS = comma list subset of: dep,secrets,risk,sbom (default: all)
 *
 * Stdout: single JSON EngineScanResult-ish object:
 *   { findings: [...], coverage: {...}, isPartial, partialReason, engineVersion, scannerVersions }
 * Exit 0 on success (partial results included). Exit non-zero only when nothing ran.
 *
 * Secret values are redacted by the engine (--redact) before they reach stdout.
 * AI enrichment is disabled: deterministic template guidance only.
 */
import { readFileSync, writeFileSync, renameSync, mkdirSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const ENGINE_DIR = resolve(process.argv[2]);
const REPO_DIR = resolve(process.argv[3]);
const SBOM_OUT = resolve(process.argv[4]);
const WANT = new Set((process.env.SCANNERS || "dep,secrets,risk,sbom").split(",").map((s) => s.trim()).filter(Boolean));
// Accept the canonical scanner names used by the .NET API as aliases for the short keys.
for (const [alias, key] of [["dep-scan", "dep"], ["secret-scan", "secrets"], ["package-risk", "risk"]]) {
  if (WANT.has(alias)) WANT.add(key);
}

const mod = (p) => import(pathToFileURL(join(ENGINE_DIR, p)).href);

const out = {
  findings: [],
  coverage: { manifestsFound: [], filesScanned: 0, scannersRun: [], scannersFailed: [], scannersSkipped: [] },
  isPartial: false,
  partialReason: null,
  engineVersion: "unknown",
  scannerVersions: {},
};

function fail(reason) {
  out.isPartial = true;
  out.partialReason = [out.partialReason, reason].filter(Boolean).join("; ");
}

try {
  const pkg = JSON.parse(readFileSync(join(ENGINE_DIR, "package.json"), "utf8"));
  out.engineVersion = `vulnveto@${pkg.version || "0.0.0"}`;
} catch { /* version unknown — not fatal */ }

// --- inventory (mirrors repo-intel output shape) ---
import { readdirSync, statSync } from "node:fs";
const MANIFESTS = [
  [/^package\.json$/, "npm", false], [/^package-lock\.json$/, "npm", true],
  [/^requirements\.txt$/, "pip", false], [/^Pipfile\.lock$/, "pip", true],
  [/^pyproject\.toml$/, "pip", false], [/\.csproj$/, "nuget", false],
  [/^packages\.lock\.json$/, "nuget", true], [/^pom\.xml$/, "maven", false],
  [/^build\.gradle$/, "gradle", false], [/^Cargo\.toml$/, "cargo", false],
  [/^Cargo\.lock$/, "cargo", true], [/^go\.mod$/, "go", false], [/^go\.sum$/, "go", true],
];
const manifests = [];
let filesScanned = 0;
function walk(dir, rel) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name), r = rel ? rel + "/" + e.name : e.name;
    if (e.isDirectory()) {
      if (["node_modules", ".git", "dist", "build", "target", "bin", "obj", "__pycache__", ".venv"].includes(e.name)) continue;
      walk(p, r);
    } else if (e.isFile()) {
      filesScanned++;
      const base = e.name;
      for (const [rx, kind, lock] of MANIFESTS) {
        if (rx.test(base)) { manifests.push({ kind, path: r, lockfile: lock }); break; }
      }
    }
  }
}
try { walk(REPO_DIR, ""); } catch (e) { fail(`inventory walk failed: ${e.message}`); }
out.coverage.manifestsFound = manifests.map((m) => m.path);
out.coverage.filesScanned = filesScanned;

const { OpenAiCompatibleClient } = await mod("dist/ai/adapter.js");
const { loadConfig } = await mod("dist/config.js");
process.env.VULNVETO_AI = process.env.VULNVETO_AI || "none";
const ai = new OpenAiCompatibleClient(loadConfig());
const ctx = {
  inventory: {
    repoUrl: "sidecar", localPath: REPO_DIR, defaultBranch: "HEAD",
    manifests, totalFiles: filesScanned, commitCount: 0,
  },
  ai, workdir: REPO_DIR,
};

const MODULES = [];
if (WANT.has("dep")) MODULES.push(["dep-scan", "dist/modules/dep-scan.js"]);
if (WANT.has("secrets")) MODULES.push(["secret-scan", "dist/modules/secret-scan.js"]);
if (WANT.has("risk")) MODULES.push(["package-risk", "dist/modules/package-risk.js"]);

/**
 * Normalizes one engine finding into the C# sidecar contract:
 * { scanner, ruleId, severity, confidence, title, category, filePath, line,
 *   evidence, remediation, packageName, packageVersion, fixedVersion }
 * Evidence/remediation are flattened to strings; locations are parsed.
 */
function normalize(f) {
  const scanner = f.module || f.scanner || "unknown";
  let ruleId = f.ruleId || null;
  let packageName = f.packageName || null;
  let packageVersion = f.packageVersion || null;
  let fixedVersion = f.fixedVersion || null;

  // dep-scan ids look like: osv-lodash-4.17.20-GHSA-29mw-wpgm-hmr9
  if (scanner === "dep-scan" && typeof f.id === "string") {
    const m = /^osv-(.+)-([0-9][^-]*(?:-[^-]+)*)-(GHSA-[A-Za-z0-9-]+|CVE-[0-9-]+)$/.exec(f.id);
    if (m) {
      packageName = packageName || m[1];
      packageVersion = packageVersion || m[2];
      ruleId = ruleId || m[3];
    } else {
      const g = /(GHSA-[A-Za-z0-9-]+|CVE-[0-9-]+)/.exec(f.id);
      if (g) ruleId = ruleId || g[1];
    }
  }
  // secret-scan ids look like: secret-aws-access-token-0-config_deploy_env
  if (scanner === "secret-scan" && typeof f.id === "string") {
    const m = /^secret-([a-z0-9-]+)-\d+-/.exec(f.id);
    if (m) ruleId = ruleId || m[1];
  }

  // evidence.location looks like: config/deploy.env:4@d8b04eb8  or  package.json.
  // Dependency findings carry package@version as location — not a file path, so leave it null
  // (package identity is already in packageName/packageVersion).
  let filePath = f.filePath || null;
  let line = f.line ?? null;
  const loc = f.evidence && typeof f.evidence === "object" ? f.evidence.location : null;
  if (!filePath && typeof loc === "string" && scanner !== "dep-scan") {
    const m = /^([^:@]+?)(?::(\d+))?(?:@.+)?$/.exec(loc);
    if (m) {
      filePath = m[1] || null;
      line = m[2] ? parseInt(m[2], 10) : null;
    }
  }

  const evidence = typeof f.evidence === "string" ? f.evidence
    : f.evidence && typeof f.evidence.summary === "string" ? f.evidence.summary : null;
  const remediation = typeof f.fix === "string" ? f.fix
    : f.fix && typeof f.fix.description === "string" ? f.fix.description : null;

  // "Upgrade lodash from 4.17.20 to 4.17.21 ..." -> 4.17.21
  if (!fixedVersion && typeof remediation === "string") {
    const m = /[Uu]pgrade [^\s]+ from [^\s]+ to ([0-9][A-Za-z0-9.\-+]*)/.exec(remediation);
    if (m) fixedVersion = m[1];
  }

  return {
    scanner, ruleId,
    severity: f.severity || "info",
    confidence: f.confidence || "medium",
    title: f.title || "Untitled finding",
    category: f.category || "other",
    filePath, line, evidence, remediation,
    packageName, packageVersion, fixedVersion,
  };
}

for (const [name, p] of MODULES) {
  try {
    const m = await mod(p);
    const key = Object.keys(m).find((k) => m[k] && m[k].name === name);
    const findings = await m[key].run(ctx);
    out.findings.push(...findings.map(normalize));
    out.coverage.scannersRun.push(name);
  } catch (e) {
    out.coverage.scannersFailed.push(name);
    fail(`${name}: ${e.message}`);
  }
}

// --- SBOM (writes relative to cwd; isolate via temp chdir) ---
if (WANT.has("sbom")) {
  try {
    const sbomDir = dirname(SBOM_OUT);
    mkdirSync(sbomDir, { recursive: true });
    const prev = process.cwd();
    process.chdir(sbomDir);
    try {
      const { sbom } = await mod("dist/modules/sbom.js");
      const findings = await sbom.run(ctx);
      out.findings.push(...findings.map(normalize));
      out.coverage.scannersRun.push("sbom");
      const { SBOM_PATH } = await mod("dist/modules/sbom.js");
      const produced = join(sbomDir, SBOM_PATH);
      if (produced !== SBOM_OUT) renameSync(produced, SBOM_OUT);
    } finally {
      process.chdir(prev);
    }
  } catch (e) {
    out.coverage.scannersFailed.push("sbom");
    fail(`sbom: ${e.message}`);
  }
}

// --- gitleaks availability (engine degrades gracefully; record it honestly) ---
// The engine's secret-scan returns zero findings (not an error) when gitleaks is
// missing, so we must detect that here — otherwise the scan looks fully successful.
let gitleaksFound = false;
const gitleaksProbed = [];
for (const bin of [process.env.GITLEAKS_BINARY, join(process.env.HOME || "", ".local", "bin", "gitleaks"), "gitleaks"].filter(Boolean)) {
  gitleaksProbed.push(bin);
  try {
    const { execFileSync } = await import("node:child_process");
    const v = execFileSync(bin, ["version"], { encoding: "utf8", timeout: 10000 }).trim();
    out.scannerVersions.gitleaks = `${v} (${bin})`;
    gitleaksFound = true;
    break;
  } catch { /* try next */ }
}
if (!gitleaksFound) {
  out.scannerVersions.gitleaks = `not found (tried: ${gitleaksProbed.join(", ")})`;
}
if (WANT.has("secrets") && !gitleaksFound) {
  // Remove the misleading "ran" entry: the engine returned empty, it did not scan.
  out.coverage.scannersRun = out.coverage.scannersRun.filter((s) => s !== "secret-scan");
  if (!out.coverage.scannersSkipped.includes("secret-scan")) {
    out.coverage.scannersSkipped.push("secret-scan");
  }
  fail("gitleaks not installed: secret scanning skipped (install from https://github.com/gitleaks/gitleaks)");
}

if (out.coverage.scannersRun.length === 0 && !out.isPartial) {
  fail("no scanners ran");
  out.isPartial = true;
}

process.stdout.write(JSON.stringify(out));
