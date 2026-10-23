<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# AegisCode Security-Engine Demo Script

Reproducible demo of the deterministic security engine that AegisCode will
integrate (see `../SECURITY_ENGINE_INTEGRATION.md`). Every result below is real:
live OSV.dev advisories, genuine gitleaks detections on synthetic test data, and
a schema-valid CycloneDX SBOM. Nothing is mocked.

**Prerequisites:** Node 20+, network access to `api.osv.dev`, gitleaks ≥ 8.27
in `PATH` or `~/.local/bin`, and the vulnveto engine checkout (sibling project;
its compiled `dist/` is used as-is — no modifications).

**Fixture:** `fixture/` — `lodash@4.17.20` (real CVEs) + `config/deploy.env`
(two **synthetic, clearly-fake** credentials for the secret-detection step).

## Steps and expected outcomes

### 1. Show the fixture (30s)
Open `fixture/package.json`: one dependency, `lodash@4.17.20`.
Open `fixture/config/deploy.env`: note the header — *synthetic test data*.

### 2. Run the scan (60–90s)
```bash
VULNVETO_AI=none node run-demo-scan.mjs   # from docs/demo/
```
Expected: `findings: 9 | summary: {"critical":0,"high":4,"medium":3,"low":1,"info":1}`.

### 3. Dependency vulnerabilities (1 min)
Open `aegiscode-demo-report.json`. Point out real OSV.dev findings, e.g.:
- **Command Injection in lodash** (high) via `_.template`
- **Prototype Pollution** (medium) via `_.unset` / array path bypass
- **ReDoS** (medium) via `toNumber`/`trim`
Each finding carries: severity, confidence, affected package@version, evidence,
and fixed-version guidance (`>= 4.17.21`). Emphasize: *versions are matched
against OSV ranges — mere presence is never treated as proof.*

### 4. Secret detection (1 min)
- `generic-api-key` at `config/deploy.env:5`
- `aws-access-token` at `config/deploy.env:4`
Both values **redacted** in the report (`--redact`). Fingerprint-based dedup;
raw values never leave the scanner.

### 5. SBOM (30s)
Open `sbom.cyclonedx.json`: CycloneDX 1.5, `lodash@4.17.20` component with
package URL. This is what the future `GET …/security-scans/{id}/sbom` endpoint
will serve.

### 6. Prioritization (1 min)
Explain the scoring contract from `../SECURITY_ENGINE_INTEGRATION.md`:
source severity is preserved; AegisCode's priority is computed deterministically
(CVSS base + fix availability + direct-vs-transitive + secret exposure −
low confidence), with `scoreGaps[]` listing every missing input. No invented
exploitability.

### 7. Regression story (1 min, narrated)
"A follow-up scan after upgrading lodash to ≥ 4.17.21 and rotating the exposed
credentials would show these findings as **resolved** by fingerprint diff, while
any new finding appears as **new** — that's the regression detection the scan
history API is designed around."

### 8. Honest close (30s)
"Deterministic scanning runs as a tested engine today; the web UI wiring —
scan initiation, live progress, findings views, SBOM download — is specified in
`../SECURITY_ENGINE_INTEGRATION.md` and is next. The dashboard shows no scan
that didn't run."

## Regenerating the artifacts

```bash
cd docs/demo
cp -r fixture /tmp/aegis-demo-fixture && cd /tmp/aegis-demo-fixture
git init -q && git add -A && git -c user.email=demo@x -c user.name=demo commit -qm fixture
VULNVETO_AI=none PATH="$HOME/.local/bin:$PATH" node /path/to/run-demo-scan.mjs
```

(The harness shells to gitleaks, which needs the fixture committed, like a real clone.)
