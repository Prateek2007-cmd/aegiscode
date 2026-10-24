<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

# Wiring Gemini (free tier) into AegisCode's AI code review

AegisCode's AI code review needs a per-client AI provider connection — there is
no environment-variable shortcut and no mock mode. If no connection is bound,
review jobs fail closed with:

> `No active AI binding is configured for purpose 'ReviewDefault'.`

This guide uses a **free Google AI Studio key** through AegisCode's built-in
OpenAI-compatible provider. Verified working against the live API
(2026-10-24): the connection verify call returned HTTP 200.

**Never paste your API key into source code, config files, or git.** It goes
into AegisCode's encrypted provider-connection store via the UI (or the API),
never into the repo.

## 1. Get a free Gemini API key (2 min)

1. Go to <https://aistudio.google.com/apikey>
2. Sign in with a Google account → **Create API key**
3. Copy the key. Keep it private — treat it like a password.

The free tier is enough for demo reviews of small pull requests.

## 2. Add the provider connection in AegisCode (2 min)

1. Sign in to AegisCode as an admin.
2. Go to **Clients** → open your client → **Providers** tab.
3. Click **Add connection** and fill in:
   - **Provider:** `OpenAI-compatible (custom base URL)`
   - **Base URL:** `https://generativelanguage.googleapis.com/v1beta/openai/`
   - **Auth:** API key → paste your Gemini key
   - **Model:** add `gemini-2.0-flash`
     (alternative: `gemini-2.5-flash` — newer, slightly slower free-tier quota)
4. Bind the model to the **Review** purpose (`ReviewDefault`).
5. Save, then click **Verify** on the connection — it should succeed.

## 3. What also has to exist before a review runs

The AI connection is necessary but not sufficient. A review job also needs:

1. **Repository coverage** — the client needs a crawl or webhook configuration
   covering the target repository, with the PR's target branch allowed by the
   destination-branch policy. Without this, the intake endpoint refuses with
   "not covered".
2. **A supported provider connection for the code host** — e.g. a GitHub
   connection (token) on the same client, so AegisCode can fetch the PR diff.
3. **An open pull request** on a covered repo — reviews only run on
   Open/Draft PRs.

## 4. Triggering a review

1. Open the PR in AegisCode (`/pr-review` via the client's PR list).
2. If the PR has new commits since its last review, a banner appears —
   click **Review current state**.
3. The job queues (202 Accepted); the background worker picks it up.
4. Findings appear in the PR view and under **Reviews** (`/reviews` history).

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `No active AI binding is configured for purpose 'ReviewDefault'` | Connection exists but isn't bound to the Review purpose | Edit the connection → add the Review purpose binding |
| Verify fails with 401/403 | Key is wrong, revoked, or quota exhausted | Regenerate the key in AI Studio |
| Review refused: "not covered" | No crawl/webhook config for the repo | Add repository coverage on the client |
| Review stuck in Queued | Background worker not running | Start the API (the worker runs in-process) |

## Notes for the cloud deploy (Hugging Face / Render)

The same UI steps apply after deploy — no extra environment variables are
needed for Gemini. (The `MEISTER_ALLOW_PRIVATE_EGRESS` flag seen in some
dev notes is specific to the sandbox VM's egress proxy and is **not** needed
on a laptop, Render, or Hugging Face.)
