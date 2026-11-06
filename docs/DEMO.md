<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# AegisCode — 5-Minute Judges Demo Script

The whole product, live, in five minutes. Every number below is real and
verifiable; nothing is mocked, and nothing is claimed that the demo does not
actually do. If a live step fails on stage, fall back to the seeded path at the
end — never fake a live result.

**Setup (before the demo):** API running in Development mode
(`ASPNETCORE_ENVIRONMENT=Development`, port 8081 — the Antseed loopback proxy
only works in Development mode) + frontend on `:5173` (or `:5174`). Signed in
as the bootstrap admin. Client "Chintawar Prateek" (or your own client).
GitHub connection "githubb" Active + Verified. AI provider bound to Review
default (Antseed proxy, GLM 5.3). See `README.md` for the screen / brain /
memory setup analogy.

## 0:00–0:30 — The problem (30s)

"Every pull request gets a review, but nobody reads the security scan, the
dependency alerts arrive a week late, and fixing a finding means copy-pasting
a suggestion into a new branch by hand. AegisCode closes that loop: review,
scan, guard, fix — in one place, with a human gate at the end."

## 0:30–1:30 — Trigger a review on a real PR (60s)

1. Open the client dashboard → **Trigger review** card. Type `Prateek2007-cmd`
   + `PRIVAGENT` (or `ACM`) + PR number, one click.
2. The job page opens. Show:
   - **What this PR does** (AI summary, capped, never invented).
   - **Changes:** counted from the actual diff (added/removed lines — never
     model-sourced).
   - The **Mermaid change diagram** (gated: only shown when the AI produced
     valid Mermaid; otherwise omitted, never faked).
   - An inline finding with an **Apply suggestion** button — the suggestion
     only publishes as a GitHub `suggestion` block when the original lines
     match the PR head byte-for-byte; otherwise it stays plain text.
   - The **Diff tab**: red/green diff with the finding pinned under the exact
     NEW-side line.
3. Real evidence to quote (verified 2026-10-25, ACM PR #1): the review ran in
   **34s**, **22,830 tokens in / 954 out**, **1 finding published, 1 withheld**
   for weak evidence. The withheld one is the point — the reviewer says
   "I don't know" instead of guessing.

## 1:30–2:30 — Security scan with Mission Control (60s)

1. Open `/security-scans` (direct URL — there is no sidebar link yet).
2. Start a scan of the same repo. Show Mission Control: live progress,
   the SBOM constellation (tappable — every component is a real dependency
   from the scan), and the findings list.
3. Open one **Critical** finding. Show: severity, package@version, evidence,
   and the fix guidance. Point out the fingerprint dedup — the same finding
   on a re-scan shows as **seen**, not as a new duplicate.
4. Quote the honest scale: the platform is **1,592,167 lines**
   (presented as 1.5M+, ~690,000 hand-written). That is the size of the
   platform, not a claim the AI read it all.

## 2:30–3:30 — The fix loop: approve → real draft PR (60s)

1. In the Fix Queue, open an approved fix proposal and click
   **Create draft PR**.
2. Show the real draft PR on GitHub: branch `aegiscode/fix-…`, the diff is
   exactly the replacement, nothing else. The proposal now records the PR
   URL — a second click returns the existing PR instead of opening a
   duplicate.
3. Show the fail-closed refusal path (if you have a second repo handy):
   change the file on GitHub, retry, and the app refuses with **422 "file
   has changed"** — no PR is created, nothing is force-applied. This is the
   feature: it would rather refuse than silently overwrite your code.

## 3:30–4:30 — The six features, one line each (60s)

Show each on the client dashboard (all four live there; Draft PRs live in
the Fix Queue, Blast Radius on the finding detail):

1. **Draft PRs** — approved fix → real draft PR on GitHub (just shown).
2. **Merge Guard** — the dashboard card shows the gate for the PR: FAIL on
   a critical/high finding, NEUTRAL when there is no scan or it is stale
   (never a fake pass), SUCCESS only with a fresh clean scan. It posts a
   GitHub Check Run named "AegisCode Merge Guard".
3. **Ask your codebase** — ask a question; the answer comes only from
   retrieved snippets with file:line citations, or an honest "I don't know
   from this codebase".
4. **Dependency Radar** — re-checks stored SBOM components against OSV for
   CVEs **new since the last scan**. "No new CVEs since your last scan" is
   a real empty state, not a fake clean bill.
5. **Blast Radius** — on a finding, the impact map: affected modules, entry
   points, data-flow notes, labelled inferred-vs-verified. Unsupported
   language → honest "unavailable".
6. **Learning Reviewer** — the Reviewer Memory panel: rules you dismissed
   5+ times (never accepted) are muted **visibly** ("muted because you
   dismissed it 5 times"), with un-mute/reset. Dismissed patterns feed the
   review prompt as per-client "do not flag X" guidance.

### The three Phase 3 features (one line each — same 60s block)

7. **Push-scans** — push to a covered repo and a security scan starts
   automatically (webhook-verified, deduped by commit SHA, per-client
   on/off + branch filter, default on for `main`). The scan links back to
   the push (branch + short SHA + pusher). An uncovered repo is logged and
   ignored — never a fake scan.
8. **Posture trends** — the dashboard shows findings by severity over time,
   mean time to resolve (from real first-seen → disappeared), and new-vs-fixed
   per scan. With fewer than 2 scans it says "not enough history yet" — it
   never invents a trend line.
9. **Custom review rules** — explicit team policy per repo: a rule is a name
   + a pattern (substring or regex on file path/content) + a severity + a
   message. A match adds a finding or upgrades its severity **with the rule
   name cited**. An invalid regex is rejected at save with the real error.
   A rule never invents a finding — the match must be a literal hit on real
   code. (Distinct from the Learning Reviewer, which adapts implicitly from
   your dismissals; rules are the explicit policy you wrote down.)

## 4:30–5:00 — Honest scorecard (30s)

Say this out loud — it is the differentiator:

- **Real:** AI PR review + security scanning proven live on this laptop
  (ACM PR #1, PRIVAGENT scan). Draft PRs, merge guard, ask-your-codebase,
  radar, blast radius, learning reviewer all build clean and are wired
  into the dashboard. The three Phase 3 features — push-scans, posture
  trends, custom review rules — also build clean and are wired in; their
  live behaviour (a real push triggering a scan, a real trend from 2+
  scans, a real rule firing on a review) still needs the laptop.
- **Needs hosting to work live:** merge guard, radar, and the autopilot
  need the API public + always-on (GitHub cannot reach localhost:8081).
  The HF Space deploy is the missing piece.
- **Needs the laptop:** `dotnet test` + live end-to-end checks (this VM
  cannot run the test host).
- **Next:** the self-healing autopilot — approved fixes become draft PRs
  on their own (drafts only, nothing merges without a human).

## If the live demo fails (fallback — use this, don't fake it)

- **Review:** show the ACM PR #1 comment already posted on GitHub
  (README.md:L35 finding, 34s, 1 published / 1 withheld). It is real
  evidence, not a screenshot of a mock.
- **Scan:** run the engine-level demo in `docs/demo/` — `VULNVETO_AI=none
  node run-demo-scan.mjs` on the fixture (`lodash@4.17.20`, real OSV
  CVEs + two synthetic credentials). Expected: `findings: 9 |
  summary: {"critical":0,"high":4,"medium":3,"low":1,"info":1}`.
  See `docs/demo/DEMO_SCRIPT.md`.
- **Fix loop:** show an already-created draft PR on GitHub from an
  earlier run, and narrate the refusal path instead of performing it.
- Never present a mock, a screenshot of a mock, or an invented number as
  a live result. Say "the live step failed, here is the real evidence
  from the last run" and move on.

## What NOT to claim

- No invented benchmark numbers, no "we block 99% of vulns", no fake
  customer quotes, no fake SIH scores.
- No coverage % or time-saved figure unless it was measured on a real
  run and the measurement is shown.
- The 1.5M+ lines is platform size, not "lines the AI read".
- The demo login / connector verification in PRIVAGENT-style flows are
  demonstrations, not real security — never enter a real password or API
  key on stage.
