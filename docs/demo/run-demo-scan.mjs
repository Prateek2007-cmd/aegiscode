/**
 * AegisCode security-engine demo harness.
 *
 * Runs the real vulnveto detection modules (OSV.dev dependency scan, gitleaks
 * secret scan, package-risk heuristics, CycloneDX SBOM) against a local fixture
 * directory, WITHOUT the GitHub-URL gate — for local demo/verification only.
 * The shipped `vulnveto scan <github-url>` CLI keeps its URL validation.
 *
 * AI is disabled by default (VULNVETO_AI=none): deterministic findings +
 * template remediation guidance only. No repository content leaves the machine.
 *
 * Env:
 *   VULNVETO_ENGINE_DIR  path to the vulnveto checkout (default: alongside this file's ../../.. heuristic — set explicitly)
 *   DEMO_FIXTURE_DIR     fixture directory (default: ./fixture)
 *   DEMO_OUT_DIR         output directory (default: ./out)
 *   VULNVETO_AI          ai backend (default: none)
 *
 * Gitleaks note: the fixture must be a git repo with committed files
 * (like a real clone) for `gitleaks detect --source` to scan history.
 */
import { writeFileSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const ENGINE = process.env.VULNVETO_ENGINE_DIR || "/home/hatch/workspace/hackathon-domain2/vulnveto";
const FIXTURE = resolve(process.env.DEMO_FIXTURE_DIR || new URL("./fixture", import.meta.url).pathname);
const OUT = resolve(process.env.DEMO_OUT_DIR || new URL("./out", import.meta.url).pathname);
mkdirSync(OUT, { recursive: true });
process.chdir(OUT); // sbom module writes its SBOM_PATH relative to cwd

const mod = (p) => import(pathToFileURL(join(ENGINE, p)).href);
const { depScan } = await mod("dist/modules/dep-scan.js");
const { secretScan } = await mod("dist/modules/secret-scan.js");
const { packageRisk } = await mod("dist/modules/package-risk.js");
const { sbom, SBOM_PATH } = await mod("dist/modules/sbom.js");
const { enrichFindings } = await mod("dist/modules/ai-remediation.js");
const { OpenAiCompatibleClient } = await mod("dist/ai/adapter.js");
const { loadConfig } = await mod("dist/config.js");

const inventory = {
  repoUrl: `demo://local/${FIXTURE} (lodash 4.17.20 + synthetic secrets)`,
  localPath: FIXTURE,
  defaultBranch: "main",
  manifests: [
    { kind: "npm", path: "package.json", lockfile: false },
    { kind: "npm", path: "package-lock.json", lockfile: true },
  ],
  totalFiles: 3,
  commitCount: 1,
};

const ai = new OpenAiCompatibleClient(loadConfig());
const ctx = { inventory, ai, workdir: FIXTURE };

const t0 = Date.now();
const results = await Promise.allSettled([depScan, secretScan, packageRisk, sbom].map((m) => m.run(ctx)));
let findings = [];
for (const r of results) {
  if (r.status === "fulfilled") findings.push(...r.value);
  else console.error("module failed:", r.reason?.message);
}
findings = await enrichFindings(findings, ai); // graceful template fallback when AI is disabled

const summary = { critical: 0, high: 0, medium: 0, low: 0, info: 0 };
for (const f of findings) summary[f.severity] = (summary[f.severity] ?? 0) + 1;

const report = {
  engine: "vulnveto security modules (AegisCode demo harness)",
  repoUrl: inventory.repoUrl,
  scannedAt: new Date().toISOString(),
  durationMs: Date.now() - t0,
  aiEnrichment: "disabled (VULNVETO_AI=none) — deterministic template guidance only",
  sbomPath: SBOM_PATH,
  summary,
  findings,
};
writeFileSync(join(OUT, "aegiscode-demo-report.json"), JSON.stringify(report, null, 2));

console.log("findings:", findings.length, "| summary:", JSON.stringify(summary));
for (const f of findings)
  console.log(` - [${f.severity}] ${(f.title || "").slice(0, 100)}`);
console.log("report →", join(OUT, "aegiscode-demo-report.json"));
console.log("sbom   →", join(OUT, SBOM_PATH));
