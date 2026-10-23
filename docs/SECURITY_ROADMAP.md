<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# AegisCode Security Roadmap

**Status as of 2026-10-23.** This document is the single source of truth for which
security capabilities exist, which are planned, and what backs each claim. If the UI
ever implies otherwise, the UI is wrong — fix the UI, not this document.

## What works today

| Capability | Backed by | Evidence |
|---|---|---|
| AI code review of PRs/MRs (GitHub, GitLab, Azure DevOps, Forgejo) | `src/MeisterDev.ProPR.Reviewing`, `ProRV` check catalog, runner fleet | Review history UI, `openapi.json` review endpoints |
| Review insights: hotspots, survival, remediation trends | `src/MeisterDev.ProPR.CodeInsights` | CodeQuality views, code-insights API |
| BYO-AI providers (OpenAI, Anthropic, Azure, Bedrock, Vertex, LiteLLM, Ollama…) | `src/MeisterDev.Ai.Providers*` | Provider connection UI |
| Deterministic security scanning **outside** the web app | vulnveto engine (sibling project): OSV.dev dep-scan, gitleaks secret-scan, CycloneDX SBOM | `docs/demo/aegiscode-demo-report.json` — real scan output, 9 findings |

## Planned (not implemented — no UI may claim these as active)

1. **Secret scanning in PR diffs and repos** — gitleaks-based, redacted findings,
   fingerprint dedup, suppression with reason + audit trail.
2. **Dependency vulnerability scanning** — manifest/lockfile parsing (npm, pip,
   NuGet, Maven, Gradle, Go, Cargo) → OSV.dev matching with affected-version
   ranges, fixed-version guidance, transitive path explanation.
3. **SBOM generation** — CycloneDX 1.5/1.6 (primary) + SPDX, per scan, viewable
   and downloadable, schema-validated.
4. **Unified findings schema** — one finding shape across scanners (rule id,
   severity, confidence, file:line, evidence, remediation, scanner source,
   suppression status), mapped into the CodeInsights taxonomy.
5. **Explainable prioritization** — documented scoring over CVSS, fix
   availability, direct-vs-transitive, secret exposure, and confidence.
   No invented exploitability.
6. **Human-approved remediation** — proposed fix → explicit approval → draft PR
   (least-privilege token) → rescan verification. Never auto-merge.
7. **SARIF export + CI integration** — CLI exit codes on severity thresholds,
   GitHub Actions example.

## Integration design

See `docs/SECURITY_ENGINE_INTEGRATION.md` for the API contracts, background-job
flow, and findings-schema mapping that will wire the deterministic engine into
the .NET backend.

## Non-goals / honesty rules

- No fabricated scores, grades, leak counts, or CVE counts — ever.
- No "X% secure" claims. A security score may only ship with a documented,
  deterministic methodology over real evidence, with stated limitations.
- Mock scanners exist only in automated tests, never as production results.
- AI remediation is advisory, labelled as such, and never auto-applied.
- Repository contents and suspected secrets are never sent to external AI
  providers without explicit user authorization and disclosure.
