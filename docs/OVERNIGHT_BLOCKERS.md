<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# Overnight Blockers — 2026-10-24

Everything that could not be completed overnight, why, and the exact manual
action needed to unblock it tomorrow.

## 1. Live AI code review (not executed)

- **Blocker:** The Gemini provider connection was wiped when the VM restarted
  (database destroyed). Per explicit instruction it was not recreated, and the
  key was never persisted — so there is nothing to recover.
- **Also missing:** a GitHub token for the code-host provider connection and
  a repo coverage (crawl/webhook) config on the client — both required before
  any review job can run.
- **What was verified instead:** the complete code path end-to-end by source
  trace, plus the fail-closed behavior ("No active AI binding is configured
  for purpose 'ReviewDefault'"). A connection verify against Gemini returned
  HTTP 200 the previous evening, proving the provider stack works.
- **To unblock:** follow `docs/GEMINI_SETUP.md` (2 min in the UI + key from
  <https://aistudio.google.com/apikey>), add a GitHub connection with a
  personal access token on the same client, add repository coverage, open a
  PR on a covered repo, click "Review current state".

## 2. .NET unit tests (could not run)

- **Blocker:** `vstest` testhost cannot start in this VM — "failed to connect
  to testhost process" after 90s (default) and 300s
  (`VSTEST_CONNECTION_TIMEOUT=300`). Environment limitation, not a code
  failure; the solution itself builds with 0 errors.
- **What was verified instead:** module build (0 errors), plus live API-level
  integration tests covering the same ground (scan lifecycle, findings,
  suppression validation, redaction, SBOM).
- **To unblock:** run `dotnet test` on a machine where the testhost starts
  (the laptop or CI).

## 3. Browser / visual UI verification (not performed)

- **Blocker:** per mission rules, no browser-permission prompts were requested
  while the user was unavailable; the available browser tool was already known
  broken from the previous session.
- **What was verified instead:** frontend type-check PASS, production build
  PASS, and every API the UI calls was exercised live (scan start/status/
  findings/SBOM/report/suppress/acknowledge).
- **To unblock:** open `http://localhost:5173` on the laptop and walk the
  `/security-scans` pages manually.

## 4. VM instability (environmental)

- The VM restarted **twice** overnight, each time wiping `/tmp` (passwords,
  JWTs, logs), killing all processes, and destroying the apt-installed
  PostgreSQL + its data. Only `~/workspace` survives.
- **Consequence:** any future local stack on this VM must be treated as
  disposable — recreate DB, re-bootstrap admin, re-add provider connections
  after each restart. Nothing in the repo was lost.
- **To unblock:** none needed for the repo; for durable local work, prefer
  the laptop. If the VM stack is rebuilt, the recipe is in
  `docs/OVERNIGHT_ENGINEERING_REPORT.md` §7.

## 5. External services (not needed / not used)

- OSV.dev advisory API: reachable and used live by dep-scan (real GHSA/CVE
  IDs returned). No blocker.
- No other external service was required overnight.
