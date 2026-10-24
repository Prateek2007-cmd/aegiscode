<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# Security Engine Integration Design

**Status: implemented and verified (2026-10-23, updated).** The C# module
`src/MeisterDev.ProPR.SecurityScanning/` implements this design: controller,
service, engine runner, background worker, EF entities + migration
(`20261023163919_AddSecurityScanning`), and the Node sidecar harness
(`Resources/scan-harness.mjs`). The module and the full API project compile;
42 unit checks pass; the sidecar contract was verified end-to-end against the
demo fixture (9 findings, honest partial when gitleaks is absent). The Vue
frontend (`frontend/src/features/security-scans/`) is wired to these endpoints.
What remains unverified here: `dotnet test` (vstest testhost won't start in
this VM — logic verified via console harness instead), and a live API run
against Postgres (no Docker here).

**Update 2026-10-24 — engine vendored into this repository.** The VulnVeto
engine no longer requires a separate repository checkout. It lives at
`tools/vulnveto/` (MIT, upstream `github.com/Prateek2007-cmd/vulnveto`
revision `000a9b992599770e7f7431f3e09dfe6e01dc586a` — see
`tools/vulnveto/VENDORED_FROM.md` and `docs/VULNVETO_INTEGRATION_REPORT.md`).
The backend resolves the bundled engine automatically; the old
`AEGIS_SECURITY_ENGINE_DIR`-to-external-checkout setup is superseded but the
variable is retained as a development/testing override. A database-backed
API run with **no** `AEGIS_SECURITY_ENGINE_DIR` set succeeded end to end
(scan of `github.com/Prateek2007-cmd/vulnveto` → Succeeded, all 4 scanners
ran, CycloneDX 1.5 SBOM with 3 components). `dotnet test` is still blocked
in this VM (testhost cannot start); the suppression-history JSON fix now has
regression tests (`tests/MeisterDev.ProPR.SecurityScanning.Tests/`) that
compile here and await a run on a machine where the test runner works.

## Setup: the bundled engine (no separate VulnVeto checkout)

Prerequisites: Node.js 20+ on PATH. The engine has **no runtime npm
dependencies** — only dev dependencies (TypeScript + Node types), installed
from the vendored lockfile.

```powershell
# Windows PowerShell (from the repo root)
scripts\Build-VulnvetoEngine.ps1
# equivalent manual steps:
cd tools\vulnveto; npm ci; npm run build; npm test; cd ..\..
```

```bash
# Linux / macOS (from the repo root)
scripts/build-vulnveto-engine.sh
# equivalent manual steps:
cd tools/vulnveto && npm ci && npm run build && npm test && cd ../..
```

The build script verifies the exact runtime files the harness imports
(`dist/modules/{dep-scan,secret-scan,package-risk,sbom}.js`,
`dist/ai/adapter.js`, `dist/config.js`) and fails early if any are missing.
`node_modules/` and `dist/` are gitignored and never committed.

**How the backend finds the engine.** `SecurityScanningOptions.Bind`
uses `AEGIS_SECURITY_ENGINE_DIR` when set; otherwise
`SecurityScanningOptions.ResolveDefaultEngineDir()` searches, in order:
`tools/vulnveto` next to the application binaries (published/Docker layout),
then walking up from the binaries (development layout:
`src/MeisterDev.ProPR.Api/bin/Debug/net10.0/` up to the repo root), then
walking up from the working directory. Only a directory containing a built
`dist/modules` is accepted — an unbuilt checkout never masquerades as a
working engine, and scans fail closed with a build instruction instead of
reporting a false clean result. Paths go through `Path.Combine` and
`ProcessStartInfo.ArgumentList` (no shell strings), so directories
containing spaces work on Windows and Linux.

**External binaries (not bundled).** Bundling the engine source does not
bundle the executables it shells out to:

| Tool | Required? | If missing | Install from |
|---|---|---|---|
| Node.js 20+ | Yes — runs the sidecar | Scans fail closed with an install message | https://nodejs.org |
| git | Yes — safe shallow clones | Clone fails with a user-safe error | https://git-scm.com |
| gitleaks | No — secret scanning only | `secret-scan` is reported **skipped**, the scan is marked **partial** (`isPartial: true` + reason naming gitleaks), never silently clean. The UI shows it under "Skipped". | https://github.com/gitleaks/gitleaks — download the release for your OS (Windows: the `windows_x64` zip), put the binary on PATH, or set `AEGIS_SECURITY_GITLEAKS_BINARY` to its full path |
| OSV.dev API | No — dependency advisories only | `dep-scan` reports the fetch failure honestly in coverage | Network access at scan time; override via `AEGIS_SECURITY_OSV_BASE_URL` |

Binaries are never committed to this repository and never downloaded or
executed from an unverified source by the application itself. Docker
deploys (`Dockerfile.deploy`) install gitleaks from the official GitHub
release tarball (pinned version, Linux x64) and build the engine from the
vendored copy — no external repository clone at deploy time either.

## Engine contract (v1, as implemented)

- **Invocation:** `node Resources/scan-harness.mjs <engineDir> <clonedRepo> <sbomOut>`
  with `VULNVETO_AI=none` (deterministic only, never sends code to AI) and
  `SCANNERS` as a comma list subset of `dep,secrets,risk,sbom`. The harness
  (shipped next to the module assembly via the csproj `Content` entry)
  dynamic-imports the built engine modules from `<engineDir>/dist/`.
  Exit 0 on success (partial results included); non-zero only when nothing ran.
- **Result output (stdout, single JSON):** `{ findings: [...], coverage:
  { manifestsFound, filesScanned, scannersRun, scannersFailed,
  scannersSkipped }, isPartial, partialReason, engineVersion,
  scannerVersions }`.
- **Finding shape (engine-native, normalized by the harness):**
  `{ scanner, ruleId, severity, confidence, title, category, filePath, line,
  evidence, remediation, packageName, packageVersion, fixedVersion }`
  — see `docs/demo/aegiscode-demo-report.json` for a real example (9 findings).
- **Secrets:** the engine redacts matched values (`--redact`) before they reach
  stdout; the worker must treat stdout as untrusted and re-verify no raw secret
  patterns persist before persistence.
- **Resource limits:** workdir per scan (no cross-scan contamination), wall-clock
  timeout (default 10 min), max repo size / file count (mirror vulnveto's
  `assertSupportedRepoUrl`: https github.com only, until other hosts are vetted).

## Goal

Wire deterministic security scanning — dependency/CVE matching (OSV.dev), secret
detection (gitleaks), CycloneDX SBOM — into the AegisCode .NET backend so scan
results flow through the existing, proven infrastructure: background jobs,
client-scoped authorization, the code-insights findings model, and the Vue
frontend.

## Architecture

```
Vue frontend ──▶ AegisCode API (.NET 10)
                      │  (1) POST clients/{id}/security-scans → 202 { scanId }
                      │  (2) ScanJob enqueued on the existing job protocol
                      ▼
               ┌──────────────┐   sidecar, stdin/stdout JSON   ┌──────────────────┐
               │ ScanJobWorker │ ─────────────────────────────▶ │ vulnveto engine  │
               │  (.NET)       │ ◀───────────────────────────── │ (Node, pinned)   │
               └──────┬───────┘   findings[] + sbom.cyclonedx.json └──────────────────┘
                      │  (3) persist: SecurityScans / SecurityFindings (EF migration)
                      │  (4) findings queryable via code-quality/findings (+ scanner filter)
                      ▼
               Vue: scan progress → findings list → finding detail → SBOM download
```

**Why a sidecar, not a rewrite:** the engine is already built, tested (9/9), and
exercised against live OSV.dev in `docs/demo/`. Rewriting it in C# would duplicate
a verified component and could not be tested in this environment. The sidecar
boundary is narrow and versioned (see contract below), matching how the platform
already shells out to its runner fleet (`Dockerfile.runner`).

## Engine contract (v1)

- **Invocation:** `node <engine>/dist/scan-sidecar.js` with a single JSON job on
  stdin, result JSON on stdout, exit code 0 on success (partial failures reported
  inside the payload, never as crashes).
- **Job input:** `{ scanId, repoUrl, ref, workdir, scanners: ["dep","secrets","sbom"], osvTimeoutMs, maxBytes }`
- **Result output:** `{ scanId, startedAt, finishedAt, findings: [...], sbomPath, coverage: { manifestsFound, filesScanned, scannersRun, scannersFailed } }`
- **Finding shape (engine-native):**
  `{ id, module, ruleId, severity, confidence, title, category, evidence, fix }`
  — see `docs/demo/aegiscode-demo-report.json` for a real example (9 findings).
- **Secrets:** the engine redacts matched values (`--redact`) before they reach
  stdout; the worker must treat stdout as untrusted and re-verify no raw secret
  patterns persist before persistence.
- **Resource limits:** workdir per scan (no cross-scan contamination), wall-clock
  timeout (default 10 min), max repo size / file count (mirror vulnveto's
  `assertSupportedRepoUrl`: https github.com only, until other hosts are vetted).

## API contracts (new, following existing conventions)

All routes client-scoped via the existing `scopeResolver` pattern; 401/403
semantics identical to `CodeQualityController`.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `clients/{clientId:guid}/security-scans` | Start scan. Body: `{ repositoryUrl, ref?, scanners? }`. → `202 { scanId, status: "queued" }`. Validates URL (SSRF-safe allowlist), returns `422` on unsupported host/format. |
| `GET` | `clients/{clientId:guid}/security-scans/{scanId:guid}` | Status + progress: `{ status, progressPct, startedAt, finishedAt, error? }`. |
| `DELETE` | `clients/{clientId:guid}/security-scans/{scanId:guid}` | Cancel a queued/running scan. |
| `GET` | `clients/{clientId:guid}/security-scans/{scanId:guid}/findings` | Paginated findings: `?severity=&scanner=&status=&limit=&offset=`. Same envelope style as `code-quality/findings`. |
| `GET` | `clients/{clientId:guid}/security-scans/{scanId:guid}/sbom` | `application/json` CycloneDX download (`Content-Disposition: attachment`). |
| `GET` | `clients/{clientId:guid}/security-scans/{scanId:guid}/report` | Full JSON report (metadata + findings + coverage + limitations). |
| `GET` | `clients/{clientId:guid}/security-scans` | Scan history for the client (paginated, newest first). |
| `POST` | `clients/{clientId:guid}/security-scans/{scanId:guid}/findings/{findingId}/suppress` | Suppress with `{ reason }` → audited, reversible. |
| `POST` | `.../findings/{findingId}/acknowledge` | Manual triage acknowledgement (explicitly labelled manual; rescans can reopen). |

## Findings schema mapping

Engine findings map into the code-insights finding model with a `scanner`
discriminator, so the existing `code-quality/findings` endpoint, filters, and
Vue tables are reused:

| Engine field | Stored as | Notes |
|---|---|---|
| `id` | `externalId` + SHA-256 fingerprint | Fingerprint = scanner‖ruleId‖package@version‖file:line (redacted). Drives dedup + regression detection across scans. |
| `module` | `scanner` (`dep-scan`, `secret-scan`, `package-risk`, `sbom`) | New indexed column / discriminator. |
| `ruleId` | `ruleId` | OSV/GHSA id, gitleaks rule id, or engine rule. |
| `severity` | `severity` | Engine severity is source severity; AegisCode priority score stored separately (see below). |
| `confidence` | `confidence` | Drives "likely vs low-confidence" display. |
| `title` / `category` | `title` / mapped taxonomy type | Category mapped into `CodeInsightCoreTaxonomy` where a natural fit exists; otherwise `security:<module>`. |
| `evidence` | `evidence` (redacted) | File:line always; secret values never. |
| `fix` | `remediation` | Template guidance now; AI enrichment later, labelled. |
| — | `firstSeenScanId`, `lastSeenScanId` | Regression: new / recurring / resolved computed by fingerprint diff. |
| — | `suppressions[]` | `{ reason, by, at }` — audit trail, reversible. |

## Explainable prioritization (documented scoring)

Priority is computed deterministically from evidence, stored beside (never
replacing) source severity:

```
priority = base(severity)                                  # CVSS-derived where available
         + 15 if fixVersion known and upgrade is minor
         + 10 if dependency is direct (vs transitive)
         + 20 if scanner == secret-scan and confidence == high
         - 10 if confidence == low
         (clamped 0–100; missing inputs contribute 0 and are listed in `scoreGaps[]`)
```

The `scoreGaps[]` array names every missing input (e.g. "no CVSS", "no reachability
data") so the UI can show *why* a score is uncertain. Exploitability and
reachability are never invented.

## Background execution

- Scans run as jobs on the existing job protocol (`reviewing/jobs` family):
  queued → running (progress events) → succeeded/failed/cancelled.
- Cancellation propagates via `CancellationToken` to the sidecar (SIGTERM, then
  SIGKILL after grace period); temp workdir is always cleaned up.
- Concurrent scans are isolated by workdir; a per-client concurrency cap
  (default 2) rate-limits expensive operations.
- OSV.dev responses cached by (ecosystem, name, version) with a 24h TTL to
  respect rate limits; cache key includes the OSV `modified` timestamp when
  present.

## Persistence (EF Core migration sketch)

- `SecurityScans`: `Id, ClientId, RepositoryUrl, Ref, Status, ProgressPct,
  StartedAt, FinishedAt, Error, EngineVersion, ScannerVersions (json),
  Coverage (json)`.
- `SecurityFindings`: `Id, ScanId, Fingerprint (unique per client), Scanner,
  RuleId, Severity, PriorityScore, ScoreGaps (json), Title, Category, FilePath,
  Line, Evidence (redacted), Remediation, Confidence, FirstSeenScanId,
  LastSeenScanId, Status (open/acknowledged/suppressed/resolved), Suppressions (json)`.
- Retention: raw engine stdout kept 30 days; SBOMs and finding records kept per
  client retention policy (default 13 months). No unbounded archive storage.

## Threat-model notes (must be addressed in implementation)

- **SSRF:** repository URL validated against an allowlist (https github.com
  initially); no redirects to private ranges; DNS rebinding mitigations at the
  egress proxy where deployed.
- **Malicious repos:** shallow clone, no hooks (`core.hooksPath=/dev/null`),
  no LFS/smudge filters by default, symlink and zip-slip safe extraction,
  file-count and size caps enforced before scanning.
- **Scanner sandbox:** sidecar runs as an unprivileged user, no network except
  OSV.dev (egress allowlist), CPU/memory/time limits; never executes repo
  scripts or build hooks.
- **Secret handling:** redaction at the engine boundary *and* re-verified at
  persistence; raw values never logged, never exported, never sent to AI
  providers without explicit opt-in.
- **AuthZ:** every new endpoint goes through the existing client-scope
  resolver; findings are tenant-isolated by `ClientId`.

## What remains to build (in order)

1. ~~`ScanSidecar` Node entrypoint (stdin/stdout JSON wrapper around the
   existing modules) + pinned engine version.~~ **Superseded:** the argv-based
   `Resources/scan-harness.mjs` sidecar is the implemented contract (see
   "Engine contract" above), and the engine is now vendored + pinned at
   `tools/vulnveto/` (see `tools/vulnveto/VENDORED_FROM.md`).
2. ~~`MeisterDev.ProPR.SecurityScanning` .NET module: controller(s), worker,
   EF entities + migration, prioritization scorer, cache.~~ **Done.**
3. OpenAPI regeneration from code (`generate:api` workflow) — never hand-edit
   generated clients.
4. ~~Vue: scan initiation dialog, live progress, findings list/detail reusing
   code-insights components, SBOM viewer + download, history + regression
   badges, suppression dialogs with reason.~~ **Done**
   (`frontend/src/features/security-scans/`).
5. Tests: fixture-based scanner tests (reuse `docs/demo/fixture`), API
   authZ tests, cancellation tests, regression-diff tests. **Partially done:**
   sidecar fixture checks (`docs/overnight-fixtures/`) + suppression-history
   regression tests exist; `dotnet test` execution still awaits a machine
   where the vstest testhost starts (blocked in this VM — see
   `docs/VULNVETO_INTEGRATION_REPORT.md`).
