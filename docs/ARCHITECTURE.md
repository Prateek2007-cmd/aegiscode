<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# AegisCode — Architecture (short version)

The screen / brain / memory analogy: the **Vue frontend is the screen**,
the **.NET API is the brain**, the **PostgreSQL database is the memory**.
The API never serves a page the frontend cannot reach, and the frontend never
stores a secret — the access token lives in memory only (a Vue ref), never in
localStorage.

## Module map

```
frontend/                        Vue 3 + Vuetify, Vite. No /api prefix on routes.
  src/features/clients/          Per-client dashboard (/clients/:id/dashboard):
                                 Trigger review card, Merge Guard card,
                                 Ask-your-codebase panel, Radar section,
                                 Reviewer Memory panel, Posture view.
  src/features/security-scans/   /security-scans — Mission Control, SBOM
                                 constellation, ScanDetailPanel (with Blast
                                 Radius section), Fix Queue.
  src/features/reviews/          Review depth panel, findings list, Diff tab.
  src/features/job-protocol/     Job page (/jobs/:id/protocol) + Diff tab.

src/MeisterDev.ProPR.Api/        API host. Login is POST /auth/login
                                 ({Username, Password} -> {accessToken}).
src/MeisterDev.ProPR.Application/ Review orchestration, SuggestedCodeGate,
                                 ReviewerMemoryFilter/Rules (learning
                                 reviewer), PrOverviewSanitizer,
                                 ReviewDiffProcessor, ChangeDiagramGate.
src/MeisterDev.ProPR.Infrastructure/
                                 GitHub provider (workspace auth via Basic
                                 x-access-token, Checks API poster, draft-PR
                                 gateway), webhook intake + signature
                                 verification, EF Core + Postgres.
src/MeisterDev.ProPR.SecurityScanning/
                                 SecurityScanService/Worker (vulnveto engine
                                 at tools/vulnveto/, resolved by default),
                                 FixProposals (approve/reject + draft PR),
                                 MergeGuard (gate + check runs), Radar
                                 (OSV re-checks + worker), CodeQa
                                 (ask-your-codebase), BlastRadius (impact
                                 analysis), ReviewerMemory (store + service).
                                 All six "too good" features live here.
src/MeisterDev.ProPR.Reviewing/  Review execution strategies (PR-wide
                                 agentic, file-by-file), AI cores.
src/MeisterDev.ProPR.CodeAnalysis/
                                 Structural analyzers (Roslyn / Tree-sitter)
                                 used by Blast Radius.
tools/vulnveto/                  Vendored vulnveto engine (byte-identical to
                                 github.com/Prateek2007-cmd/vulnveto).
                                 Do not modify destructively.
```

## Data flow — the loop

```
GitHub push / PR created
  -> webhook intake (signature verified, event classified)
  -> review intake (PR created)            OR  scan intake (push — Phase 3)
  -> AI review (ReviewDefault model)        -> security scan (vulnveto engine)
       |                                            |
       v                                            v
  review published to GitHub              findings persisted (fingerprint dedup)
  (summary + counted stats +                |
   Mermaid diagram + inline                 v
   findings + Diff tab)              Merge Guard evaluates latest scan
       |                              + review (FAIL / NEUTRAL / SUCCESS)
       |                              -> GitHub Check Run "AegisCode Merge Guard"
       v
  Fix Queue: human approves a fix proposal
       |
       v
  Create draft PR (fail-closed gate: byte-for-byte original match,
  valid range, non-no-op; a changed file refuses with 422)
       |
       v
  Real draft PR on GitHub (branch aegiscode/fix-..., base = PR head or
  default branch). Second click returns the existing PR.
       |
       +-- Dependency Radar (scheduled): re-checks stored SBOM vs OSV,
       |   alerts on NEW Critical/High CVEs only.
       +-- Learning Reviewer: dismissals/accepts recorded per rule;
       |   5+ dismissals + 0 accepts mutes the rule (visibly).
       +-- Blast Radius: impact map for a finding (inferred vs verified).
       +-- Ask your codebase: grounded Q&A with citations, or "I don't know".
```

## Honesty contracts (the rules the code enforces)

- **No fake scans.** A scan is only "Succeeded" when the engine actually
  ran. Missing tools never produce a successful scan.
- **No fake passes.** The merge guard is NEUTRAL (not SUCCESS) when there
  is no scan, the scan failed, or it is older than 7 days.
- **No fake fixes.** A draft PR is only claimed when GitHub returned its
  URL. A changed file refuses (422) instead of force-applying.
- **No invented findings.** Every finding carries real evidence
  (file:line, package@version, OSV advisory, or diff context). Reviewer
  memory keys come only from findings and decisions the user actually made.
- **No invented numbers.** Counts come from the actual diff / scan;
  the Mermaid diagram is gated (invalid → omitted, never faked).
- **Fail-closed everywhere.** When in doubt, the feature reports
  "unavailable" / "neutral" / "I don't know" with the real reason.

## What needs hosting (and why)

- **Webhook auto-review, merge guard check runs, radar sweeps, and the
  autopilot** need the API public + always-on. GitHub cannot reach
  `http://localhost:8081`, and a laptop that sleeps cannot run a
  scheduled worker. The chosen path is a Hugging Face Space (Docker,
  port 7860, `Dockerfile.deploy`) + Neon Postgres. The local Antseed
  proxy (`http://127.0.0.1:8377/v1`) cannot be reached from the cloud —
  the AI provider must switch to a cloud one (Groq free tier is the pick;
  Gemini is the backup).
- **Local run:** API in Development mode (required for the Antseed
  loopback proxy — the SSRF guard blocks loopback otherwise) on port
  **8081** (not 8080), frontend on `:5173`/`:5174`, Postgres 18
  (local, or Neon for pgvector). Set `AEGIS_SECURITY_FIX_PROPOSALS_ROOT`
  to a persistent path on durable installs.

## Key files (where to look)

- Review trigger: `frontend/src/services/jobsService.ts`
  (`submitReviewByCoordinates`), `POST /clients/{id}/reviewing/jobs/by-coordinates`.
- Diff: `GET /reviewing/jobs/{jobId}/diff` (+ `/jobs/{jobId}/diff` alias).
- Draft PR: `POST /clients/{id}/fix-proposals/{proposalId}/create-draft-pr`,
  `DraftPullRequestService` + `GitHubDraftPullRequestGateway`.
- Merge guard: `MergeGuardGate.Evaluate` (pure function, unit-testable),
  `MergeGuardService.EvaluateAndReportAsync`, `GitHubCheckRunPoster`.
- Radar: `RadarDetector.DetectNew` (pure), `RadarService`, `RadarWorker`,
  `OsvClient`.
- Ask: `CodeQaService`, `CodeQaCitationGate` (fail-closed citations).
- Blast: `BlastRadiusService`, `BlastRadiusImpactAnalyzer`.
- Learn: `ReviewerMemoryRules` (mute after 5 dismissals / 0 accepts),
  `ReviewerMemoryService` / `ReviewerMemoryStore`, `ReviewerMemoryFilter`.
- Scan engine: `SecurityScanEngineRunner`, `SecurityScanWorker`,
  `SecurityScanFingerprint.Compute`.

## Status (2026-11-06, overnight build)

- All six "too good" features are ported to the main copy, format-clean
  (`cleanupcode 2026.2.0`), and the full solution builds with 0 errors;
  the frontend builds and the targeted specs pass.
- All three Phase 3 features are also in main and verified the same way:
  webhook-driven scans on every push (`PushScanService` + `IPushScanTrigger`,
  wired into `HandleProviderWebhookDeliveryHandler`), posture trends over
  time (`SecurityTrendsService`, read-only over stored scans), and custom
  review rules per repo (`ReviewRuleService` + `CustomReviewRules`, enforced
  in `ReviewOrchestrationService` and `SecurityScanWorker`). Main targeted
  specs: 83/83 (six features + push-scans + trends + rules).
- Still owed on the user's laptop: `dotnet test` (the VM cannot run the
  test host), live end-to-end checks (real PR review, real scan, real
  draft PR, real guard check run), and the pushes (each needs his explicit
  approval — nothing is pushed overnight).
- Still owed for hosting: HF Space API deploy, Vercel Deployment Protection
  off + `VITE_API_BASE_URL` set, OpenAPI regen (diff + draft-PR + code-qa
  endpoints).
