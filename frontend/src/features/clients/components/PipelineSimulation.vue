<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Pipeline simulation. A clearly-labelled SIMULATED walkthrough of one pull
  request flowing through AegisCode — PR opened, AI review, findings, fix
  approval, draft PR, merge guard, merge. Static mock data only: it calls no
  endpoint and never presents simulated data as real. The only real numbers
  on this card are the "Proven live" caption (ACM PR #1); every other number
  is simulated fiction and is marked as such.
-->
<template>
  <section class="pipeline-simulation" aria-label="Pipeline simulation">
    <div class="pipeline-simulation-head">
      <div>
        <h3 class="pipeline-simulation-title">Pipeline simulation</h3>
        <p class="pipeline-simulation-subtitle">
          Watch one pull request flow through AegisCode, step by step.
        </p>
      </div>
      <span class="simulated-chip">Simulated</span>
    </div>

    <p class="pipeline-simulation-banner">
      This is a demonstration with fictional data — nothing here is a real
      review, scan, or pull request.
    </p>

    <!-- Controls -->
    <div class="pipeline-controls" role="group" aria-label="Simulation controls">
      <button class="btn-ghost pipeline-control" type="button" :disabled="isFirst" @click="goTo(currentStep - 1)">
        ← Back
      </button>
      <button class="btn-primary pipeline-control" type="button" @click="togglePlay">
        {{ playing ? '❚❚ Pause' : '▶ Play' }}
      </button>
      <button class="btn-ghost pipeline-control" type="button" :disabled="isLast" @click="goTo(currentStep + 1)">
        Next →
      </button>
      <span class="pipeline-step-label" aria-live="polite">
        Step {{ currentStep + 1 }} of {{ steps.length }}
      </span>
    </div>

    <!-- Step dots -->
    <div class="pipeline-dots" role="tablist" aria-label="Simulation steps">
      <button
        v-for="(step, index) in steps"
        :key="step.id"
        type="button"
        role="tab"
        :aria-selected="index === currentStep"
        :aria-label="`Step ${index + 1}: ${step.title}`"
        class="pipeline-dot"
        :class="{ 'pipeline-dot-active': index === currentStep, 'pipeline-dot-done': index < currentStep }"
        @click="goTo(index)"
      >
        {{ index + 1 }}
      </button>
    </div>

    <!-- Progress -->
    <div
      class="pipeline-progress"
      role="progressbar"
      :aria-valuenow="currentStep + 1"
      aria-valuemin="1"
      :aria-valuemax="steps.length"
      :aria-label="`Simulation progress, step ${currentStep + 1} of ${steps.length}`"
    >
      <div class="pipeline-progress-fill" :style="{ width: `${progressPercent}%` }"></div>
    </div>

    <!-- Step panels -->
    <div class="pipeline-stage">
      <!-- Step 1: PR opened -->
      <article v-show="currentStep === 0" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 1 · PR opened
        </p>
        <h4 class="pipeline-panel-title">Add rate limiting to the login endpoint</h4>
        <dl class="pipeline-facts">
          <div><dt>Repository</dt><dd><code>acme/api</code> (fictional)</dd></div>
          <div><dt>Branch</dt><dd><code>rate-limit-login → main</code></dd></div>
          <div><dt>Changed</dt><dd>3 files · +142 −38 (simulated)</dd></div>
          <div><dt>Trigger</dt><dd><span class="pipeline-chip">webhook: pull_request opened</span></dd></div>
        </dl>
        <p class="pipeline-panel-note">
          The moment the PR opens, GitHub fires the webhook and AegisCode
          takes in the diff. Nothing here is a real repository.
        </p>
      </article>

      <!-- Step 2: AI review runs -->
      <article v-show="currentStep === 1" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 2 · AI review runs
        </p>
        <h4 class="pipeline-panel-title">Review summary, written from the diff</h4>
        <p class="pipeline-panel-note">
          What this PR does: adds a per-account rate limit to the login
          handler so brute-force attempts are slowed down.
        </p>
        <dl class="pipeline-facts">
          <div><dt>Counted from the diff</dt><dd>3 files · +142 −38 (simulated)</dd></div>
          <div><dt>Walkthrough</dt><dd>login handler → rate-limit middleware → tests</dd></div>
          <div><dt>Findings</dt><dd>2 pinned to exact lines (simulated)</dd></div>
        </dl>
        <p class="pipeline-panel-note">
          Stats are counted from the actual diff, never invented by the model —
          the same rule the real reviewer follows.
        </p>
      </article>

      <!-- Step 3: findings -->
      <article v-show="currentStep === 2" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 3 · Findings on the PR
        </p>
        <h4 class="pipeline-panel-title">Two findings, pinned to the lines</h4>
        <div class="pipeline-finding pipeline-finding-security">
          <p class="pipeline-finding-kind">Security · Critical (simulated)</p>
          <p class="pipeline-finding-title">Hardcoded secret in the login handler</p>
          <p class="pipeline-finding-location"><code>src/auth/login.ts:87</code></p>
          <pre class="pipeline-code">const API_KEY = "sk-live-…"</pre>
          <div class="pipeline-suggestion">
            <p class="pipeline-suggestion-label">Suggested fix</p>
            <pre class="pipeline-code">const API_KEY = process.env.LOGIN_API_KEY</pre>
            <button
              class="btn-primary pipeline-apply"
              type="button"
              :disabled="suggestionApplied"
              @click="suggestionApplied = true"
            >
              {{ suggestionApplied ? '✓ Suggestion applied (simulated)' : 'Apply suggestion' }}
            </button>
          </div>
        </div>
        <div class="pipeline-finding pipeline-finding-quality">
          <p class="pipeline-finding-kind">Quality (simulated)</p>
          <p class="pipeline-finding-title">Unbounded retry loop</p>
          <p class="pipeline-finding-location"><code>src/auth/rateLimit.ts:41</code></p>
          <p class="pipeline-panel-note">
            The login retry loop has no backoff and no cap — a slow upstream
            turns it into a hammer. (simulated)
          </p>
        </div>
      </article>

      <!-- Step 4: Fix Queue approval → draft PR -->
      <article v-show="currentStep === 3" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 4 · Fix approved → draft PR
        </p>
        <h4 class="pipeline-panel-title">A human approves, then a draft PR opens</h4>
        <div class="pipeline-flow">
          <div class="pipeline-flow-card">
            <p class="pipeline-flow-label">Fix Queue</p>
            <p class="pipeline-flow-text">Fix proposal approved by a human — nothing opens without approval.</p>
          </div>
          <span class="pipeline-flow-arrow" aria-hidden="true">→</span>
          <div class="pipeline-flow-card">
            <p class="pipeline-flow-label">Draft pull request</p>
            <p class="pipeline-flow-text">fix: rotate login API key</p>
            <p class="pipeline-flow-text"><code>aegiscode/fix-login-secret-42</code></p>
            <span class="pipeline-chip">Draft</span>
          </div>
        </div>
        <p class="pipeline-panel-note">
          The draft PR carries the suggested fix on a branch from the PR head —
          a draft, never an auto-merge. (simulated)
        </p>
      </article>

      <!-- Step 5: Merge guard -->
      <article v-show="currentStep === 4" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 5 · Merge guard
        </p>
        <h4 class="pipeline-panel-title">AegisCode Merge Guard</h4>
        <div class="pipeline-check">
          <span class="pipeline-check-mark" aria-hidden="true">✓</span>
          <div>
            <p class="pipeline-check-status">Success</p>
            <p class="pipeline-check-note">
              Green only because the latest scan is fresh and clean — no
              critical or unresolved high findings. (simulated)
            </p>
          </div>
        </div>
        <p class="pipeline-panel-note">
          A missing, failed, or stale scan never gets a fake pass — the guard
          reports neutral instead.
        </p>
      </article>

      <!-- Step 6: Merge -->
      <article v-show="currentStep === 5" class="pipeline-panel">
        <p class="pipeline-panel-kicker">
          <span class="simulated-chip simulated-chip-small">Simulated</span>
          Step 6 · Merged
        </p>
        <h4 class="pipeline-panel-title">Merged — posture improves by one</h4>
        <div class="pipeline-trend" aria-label="Open findings trend (simulated)">
          <div class="pipeline-trend-bar">
            <span class="pipeline-trend-label">Before</span>
            <span class="pipeline-trend-track"><span class="pipeline-trend-fill pipeline-trend-fill-before"></span></span>
            <span class="pipeline-trend-value">5 open (simulated)</span>
          </div>
          <div class="pipeline-trend-bar">
            <span class="pipeline-trend-label">After</span>
            <span class="pipeline-trend-track"><span class="pipeline-trend-fill pipeline-trend-fill-after"></span></span>
            <span class="pipeline-trend-value">4 open · 1 fixed (simulated)</span>
          </div>
        </div>
        <p class="pipeline-panel-note">
          The fix lands, the trend ticks down, and the next scan starts from
          the new baseline.
        </p>
      </article>
    </div>

    <p class="pipeline-proven">
      Proven live: ACM PR #1 — reviewed in 34s, 22,830 tokens in / 954 out,
      1 finding published, 1 withheld.
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'

const PLAY_INTERVAL_MS = 3800
const PLAY_INTERVAL_REDUCED_MOTION_MS = 6000

const steps = [
  { id: 'pr-opened', title: 'PR opened' },
  { id: 'ai-review', title: 'AI review runs' },
  { id: 'findings', title: 'Findings on the PR' },
  { id: 'draft-pr', title: 'Fix approved, draft PR' },
  { id: 'merge-guard', title: 'Merge guard' },
  { id: 'merged', title: 'Merged' },
] as const

const currentStep = ref(0)
const playing = ref(false)
const suggestionApplied = ref(false)

const isFirst = computed(() => currentStep.value === 0)
const isLast = computed(() => currentStep.value === steps.length - 1)
const progressPercent = computed(() => ((currentStep.value + 1) / steps.length) * 100)

let timer: ReturnType<typeof setInterval> | null = null

function playIntervalMs(): number {
  if (typeof window === 'undefined') return PLAY_INTERVAL_MS
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
    ? PLAY_INTERVAL_REDUCED_MOTION_MS
    : PLAY_INTERVAL_MS
}

function stopAutoAdvance(): void {
  if (timer !== null) {
    clearInterval(timer)
    timer = null
  }
  playing.value = false
}

function startAutoAdvance(): void {
  stopAutoAdvance()
  playing.value = true
  timer = setInterval(() => {
    if (currentStep.value >= steps.length - 1) {
      stopAutoAdvance()
      return
    }
    currentStep.value += 1
  }, playIntervalMs())
}

function togglePlay(): void {
  if (playing.value) {
    stopAutoAdvance()
    return
  }
  if (isLast.value) currentStep.value = 0
  startAutoAdvance()
}

function goTo(index: number): void {
  stopAutoAdvance()
  currentStep.value = Math.max(0, Math.min(index, steps.length - 1))
}

onBeforeUnmount(() => stopAutoAdvance())
</script>

<style scoped>
.pipeline-simulation {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-xl);
  padding: 1.25rem 1.4rem;
  color: var(--color-text);
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.pipeline-simulation-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.pipeline-simulation-title {
  margin: 0;
  font-family: var(--font-display);
  font-size: 1.25rem;
  font-weight: 600;
}

.pipeline-simulation-subtitle {
  margin: 0.25rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.simulated-chip {
  display: inline-block;
  padding: 0.2rem 0.65rem;
  border-radius: var(--radius-pill);
  background: var(--color-accent-soft);
  border: 1px solid var(--color-accent);
  color: var(--color-accent);
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  white-space: nowrap;
}

.simulated-chip-small {
  padding: 0.1rem 0.5rem;
  font-size: 0.68rem;
}

.pipeline-simulation-banner {
  margin: 0;
  padding: 0.7rem 0.9rem;
  border-radius: var(--radius-md);
  background: var(--color-muted-soft);
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.pipeline-controls {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.6rem;
}

.pipeline-control {
  min-width: 5.5rem;
}

.pipeline-step-label {
  margin-left: auto;
  font-family: var(--font-mono);
  font-size: 0.85rem;
  color: var(--color-text-muted);
}

.pipeline-dots {
  display: flex;
  gap: 0.5rem;
}

.pipeline-dot {
  width: 2rem;
  height: 2rem;
  border-radius: var(--radius-pill);
  border: 1px solid var(--color-border-hover);
  background: var(--color-surface-raised);
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  font-size: 0.8rem;
  cursor: pointer;
  padding: 0;
}

.pipeline-dot:hover {
  border-color: var(--color-accent);
  color: var(--color-text);
}

.pipeline-dot-active {
  background: var(--color-accent);
  border-color: var(--color-accent);
  color: var(--color-primary-text);
  font-weight: 700;
}

.pipeline-dot-done {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.pipeline-progress {
  height: 0.35rem;
  border-radius: var(--radius-pill);
  background: var(--color-surface-raised);
  overflow: hidden;
}

.pipeline-progress-fill {
  height: 100%;
  background: var(--color-accent);
  border-radius: var(--radius-pill);
  transition: width 0.3s ease;
}

.pipeline-stage {
  min-height: 16rem;
}

.pipeline-panel {
  display: flex;
  flex-direction: column;
  gap: 0.7rem;
}

.pipeline-panel-kicker {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 0.6rem;
  font-size: 0.85rem;
  color: var(--color-text-muted);
  font-weight: 600;
}

.pipeline-panel-title {
  margin: 0;
  font-size: 1.05rem;
}

.pipeline-panel-note {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.pipeline-facts {
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
}

.pipeline-facts > div {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  font-size: 0.9rem;
}

.pipeline-facts dt {
  color: var(--color-text-muted);
  min-width: 11rem;
}

.pipeline-facts dd {
  margin: 0;
}

.pipeline-facts code,
.pipeline-flow-text code,
.pipeline-finding-location code {
  font-family: var(--font-mono);
  font-size: 0.88rem;
  background: var(--color-muted-soft);
  border-radius: var(--radius-xs);
  padding: 0.05rem 0.35rem;
}

.pipeline-chip {
  display: inline-block;
  padding: 0.15rem 0.6rem;
  border-radius: var(--radius-pill);
  background: var(--color-muted-soft);
  border: 1px solid var(--color-border-hover);
  font-size: 0.78rem;
  font-weight: 600;
}

.pipeline-finding {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  padding: 0.9rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.pipeline-finding-security {
  border-left: 3px solid var(--color-danger);
}

.pipeline-finding-quality {
  border-left: 3px solid var(--color-warning);
}

.pipeline-finding-kind {
  margin: 0;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.pipeline-finding-title {
  margin: 0;
  font-weight: 700;
  font-size: 0.95rem;
}

.pipeline-finding-location {
  margin: 0;
  font-size: 0.85rem;
}

.pipeline-code {
  margin: 0.2rem 0 0;
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-sm);
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  font-family: var(--font-mono);
  font-size: 0.85rem;
  color: var(--color-text);
  overflow-x: auto;
}

.pipeline-suggestion {
  margin-top: 0.4rem;
  padding: 0.7rem 0.8rem;
  border-radius: var(--radius-sm);
  background: var(--color-suggestion-soft);
  border: 1px solid var(--color-suggestion);
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.5rem;
}

.pipeline-suggestion-label {
  margin: 0;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-suggestion);
}

.pipeline-apply {
  margin-top: 0.2rem;
}

.pipeline-flow {
  display: flex;
  align-items: stretch;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.pipeline-flow-card {
  flex: 1;
  min-width: 12rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  padding: 0.9rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.pipeline-flow-label {
  margin: 0;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.pipeline-flow-text {
  margin: 0;
  font-size: 0.9rem;
}

.pipeline-flow-arrow {
  align-self: center;
  color: var(--color-accent);
  font-size: 1.4rem;
  font-weight: 700;
}

.pipeline-check {
  display: flex;
  align-items: flex-start;
  gap: 0.8rem;
  border: 1px solid var(--color-success);
  border-radius: var(--radius-md);
  background: var(--color-success-soft);
  padding: 0.9rem 1rem;
}

.pipeline-check-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  border-radius: var(--radius-pill);
  background: var(--color-success);
  color: var(--color-bg);
  font-weight: 700;
  flex-shrink: 0;
}

.pipeline-check-status {
  margin: 0;
  font-weight: 700;
  font-size: 0.95rem;
}

.pipeline-check-note {
  margin: 0.2rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.pipeline-trend {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.pipeline-trend-bar {
  display: grid;
  grid-template-columns: 4rem 1fr auto;
  align-items: center;
  gap: 0.75rem;
  font-size: 0.85rem;
}

.pipeline-trend-label {
  color: var(--color-text-muted);
}

.pipeline-trend-track {
  height: 0.6rem;
  border-radius: var(--radius-pill);
  background: var(--color-surface-raised);
  overflow: hidden;
}

.pipeline-trend-fill {
  display: block;
  height: 100%;
  border-radius: var(--radius-pill);
}

.pipeline-trend-fill-before {
  width: 100%;
  background: var(--color-text-faint);
}

.pipeline-trend-fill-after {
  width: 80%;
  background: var(--color-accent);
}

.pipeline-trend-value {
  font-family: var(--font-mono);
  white-space: nowrap;
}

.pipeline-proven {
  margin: 0;
  padding-top: 0.7rem;
  border-top: 1px solid var(--color-border);
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

@media (prefers-reduced-motion: reduce) {
  .pipeline-progress-fill {
    transition: none;
  }
}
</style>
