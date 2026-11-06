<p align="center">
  <img src="frontend/src/assets/aegis_logo.svg" alt="AegisCode" width="120">
  <br>
  <h1 align="center">AegisCode</h1>
  <p align="center"><em>Intelligent Developer Security, Automated Code Review &amp; Supply Chain Guardrails</em></p>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-ELv2-blue.svg" alt="License: ELv2"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Vue-3.5-4FC08D?logo=vuedotjs" alt="Vue 3.5">
  <img src="https://img.shields.io/badge/Security-OWASP%20%7C%20CWE%20%7C%20Secrets-emerald" alt="Security Guardrails">
</p>

---

## Overview

**AegisCode** is a self-hosted, AI-powered code review platform for pull and merge requests, evolving toward
deterministic developer-security guardrails (secret detection, dependency vulnerability scanning, SBOM generation).

Operating across **GitHub**, **GitLab**, **Azure DevOps**, and **Forgejo**, AegisCode reviews code changes with
semantically aware AI models (Roslyn and Tree-sitter assisted analysis where applicable), posting inline findings
and review summaries directly in your SCM threads.

> **Honesty note:** the deterministic security scanners below are **implemented
> and proven live** (security scan of the PRIVAGENT repo, AI review posted on
> ACM PR #1 — 34s, 1 finding published, 1 withheld for weak evidence).
> Nothing in the UI implies a scan that did not run: a missing tool never
> produces a successful scan, and the merge guard reports NEUTRAL — never a
> fake pass — when there is no scan or it is stale. See [the demo script](docs/DEMO.md)
> for the real numbers and the honest scorecard.

## Capabilities

### Working today

- 🤖 **AI Code Review:** Automated PR/MR reviews with inline findings, summaries, and review history.
- 🔒 **Self-Hosted (Zero Code Exfiltration):** You control where code and data reside. AegisCode runs within your
  perimeter, connecting directly to your SCM hosts and self-managed AI gateways.
- 🧠 **Bring Your Own AI (BYO-AI):** Connect Azure OpenAI, OpenAI, Anthropic Claude, AWS Bedrock, Google Vertex AI,
  LiteLLM gateways, or local LLMs (e.g. Ollama, vLLM). No proprietary AI models or third-party training on your code.
- 🏢 **Multi-Tenancy & Fleet Runners:** Multi-tenant architecture with distributed runner fleet management and
  tenant-scoped budgets.
- 📊 **Review Insights:** Aggregated code hotspots, insight survival across revisions, and remediation trends.

### Security — working today

- 🛡️ **Secret Scanning:** Detect leaked API keys, tokens, and credentials in diffs and repository files (gitleaks engine, vendored vulnveto at `tools/vulnveto/`).
- 📦 **Dependency & CVE Scanning:** Match manifests/lockfiles against OSV.dev, flag known CVEs, suggest fixes. Findings are fingerprint-deduped across scans.
- 📄 **SBOM Generation:** CycloneDX software bill of materials per scan, viewable as a tappable constellation and downloadable.
- 💡 **Guided Remediation:** Human-approved fix patches for verified findings — approve in the Fix Queue, then **Create draft PR** opens a real draft PR on GitHub (fail-closed: a changed file refuses with 422 instead of force-applying).

### The six features (all in the main tree, all building clean)

- 🔀 **Draft PRs** — approved fix → real draft PR on GitHub, branch `aegiscode/fix-…`.
- 🛡️ **Merge Guard** — FAIL / NEUTRAL / SUCCESS from the latest scan + review, posted as a GitHub Check Run ("AegisCode Merge Guard"). Never a fake pass.
- 💬 **Ask your codebase** — grounded Q&A with file:line citations, or an honest "I don't know from this codebase".
- 📡 **Dependency Radar** — scheduled OSV re-checks of stored SBOMs; alerts only on CVEs **new** since the last scan.
- 💥 **Blast Radius** — impact map for a finding (affected modules, entry points, inferred-vs-verified labels).
- 🧠 **Learning Reviewer** — rules dismissed 5+ times (never accepted) are muted visibly, with un-mute/reset; dismissed patterns feed the review prompt.

### Phase 3 — the loop, closed (all in the main tree, all building clean)

- 🔄 **Push-scans** — a verified GitHub push webhook for a covered repo starts a security scan automatically (deduped by commit SHA, per-client on/off + branch filter, default on for `main`). The scan links back to the push. An uncovered repo is logged and ignored, never a fake scan.
- 📈 **Posture trends** — findings by severity over time, mean time to resolve (from real first-seen → disappeared), new-vs-fixed per scan. Fewer than 2 scans → honest "not enough history yet", never an invented trend line.
- 📏 **Custom review rules** — explicit team policy per repo: {name, pattern (substring/regex on path/content), severity, message}. A match adds a finding or upgrades severity with the rule name cited; an invalid regex is rejected at save with the real error. A rule never invents a finding. (Distinct from the Learning Reviewer, which adapts implicitly from dismissals.)

See [docs/DEMO.md](docs/DEMO.md) (5-minute judges demo script),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (module map + the push → scan →
review → guard → fix → draft PR loop), and
[docs/SECURITY_ENGINE_INTEGRATION.md](docs/SECURITY_ENGINE_INTEGRATION.md) for the engine design.

## Quickstart

The fastest way to deploy AegisCode for evaluation is via the bundled Docker Compose stack:

```bash
# 1. Clone the repository
git clone https://github.com/Prateek2007-cmd/aegiscode.git aegiscode
cd aegiscode

# 2. Configure the environment
cp example/docker-compose/.env.example example/docker-compose/.env
# Edit example/docker-compose/.env with your JWT secret and database credentials

# 3. Launch the platform
cd example/docker-compose
docker compose up --build
```

Access the AegisCode Security Console at **`https://localhost:5443`**.

## Running it locally — screen / brain / memory

Think of the three parts like this: the **Vue frontend is the screen**, the
**.NET API is the brain**, the **PostgreSQL database is the memory**. The
screen shows what the brain computed from what the memory stored — nothing
else.

- **Screen (frontend):** `cd frontend && npm install && npm run dev` → `:5173`
  (or `:5174`). `npm run build` for production. The access token lives in
  memory only (a Vue ref), never in localStorage.
- **Brain (API):** `dotnet run --project src/MeisterDev.ProPR.Api` → port
  **8081** (not 8080 — that belongs to another local service). **Run it in
  Development mode** (`ASPNETCORE_ENVIRONMENT=Development`): the local AI
  proxy (Antseed, `http://127.0.0.1:8377/v1`) is a loopback address, and the
  SSRF guard blocks loopback unless Development mode is on.
- **Memory (database):** PostgreSQL 18. Local Postgres works, but it has no
  pgvector — point the local API at a Neon database for the full feature set.
  Set `AEGIS_SECURITY_FIX_PROPOSALS_ROOT` to a persistent path on durable
  installs (fix proposals are JSON files, one per client).

**Honest status (2026-11-06):** AI PR review + security scanning are proven
live on a laptop (ACM PR #1, PRIVAGENT scan). All six features above build
clean (full solution 0 errors, frontend builds, targeted specs pass). Still
owed: `dotnet test` + live end-to-end checks on the laptop, and the pushes
(each needs explicit approval). Merge guard, radar, and the autopilot need
the API public + always-on to work live — GitHub cannot reach
`http://localhost:8081`. The chosen hosting path is a Hugging Face Space
(Docker, port 7860, `Dockerfile.deploy`) + Neon; the local Antseed proxy
cannot be reached from the cloud, so the AI provider switches to Groq
(free tier) there.

## Development Setup

### Backend (.NET 10)
```powershell
# Restore dependencies and build solution
dotnet restore MeisterDev.ProPR.slnx
dotnet build MeisterDev.ProPR.slnx --no-restore

# Run local API host
dotnet run --project src/MeisterDev.ProPR.Api/MeisterDev.ProPR.Api.csproj
```

### Frontend (Vue 3 + Vuetify)
```bash
cd frontend
npm install

# Run in mock mode (MSW simulated security fixtures)
npm run mock

# Run against local live backend
npm run dev

# Run full test suite & type verification
npm test
npm run type-check
npm run build
```

## Architecture & Configuration

AegisCode supports flexible configuration through both modern `AEGIS_*` variables and backwards-compatible `MEISTER_*` environment bindings:

| Variable | Description |
|---|---|
| `AEGIS_JWT_SECRET` | 256-bit cryptographic key used for HS256 authentication tokens |
| `AEGIS_BOOTSTRAP_ADMIN_USER` | Initial administrative user seeded on empty database initialization |
| `AEGIS_BOOTSTRAP_ADMIN_PASSWORD` | Initial password for the administrator account |
| `AEGIS_PUBLIC_BASE_URL` | Public base endpoint advertised to SCM webhook listeners |
| `DB_CONNECTION_STRING` | PostgreSQL connection string (with pgvector support enabled) |

## Documentation

Full architectural and operational guides are organized under `docs/`:
- [Quickstart Guide](docs/quickstart.md)
- [Architecture & Boundaries](docs/architecture/scm-provider-boundaries.md)
- [Code Insights & Hotspots](docs/architecture/code-insights.md)
- [Security & Ingress Reference](docs/reference/security.md)
- [Product Transformation Report](docs/PRODUCT_TRANSFORMATION_REPORT.md)

## License & Attribution

AegisCode contains and builds upon technology originally authored by **Andreas Rain** under the **Elastic License 2.0 (ELv2)**.

- Original Copyright (c) Andreas Rain. All rights reserved.
- Distributed under the terms of the [Elastic License 2.0](LICENSE).
- See [NOTICE](NOTICE) and [LICENSING.md](LICENSING.md) for complete license policies and attribution.
