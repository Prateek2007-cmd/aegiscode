<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="mission-control" aria-label="Live scan mission control">
    <div class="mc-top">
      <!-- Overall progress ring: the one bold element -->
      <div class="ring-wrap">
        <svg class="ring" viewBox="0 0 140 140" role="img" :aria-label="`Scan ${scan.progressPct}% complete`">
          <circle class="ring-track" cx="70" cy="70" r="58" />
          <circle
            class="ring-fill"
            cx="70"
            cy="70"
            r="58"
            :stroke-dasharray="circumference"
            :stroke-dashoffset="dashOffset"
          />
        </svg>
        <div class="ring-center">
          <span class="ring-pct"><CountUp :value="scan.progressPct" />%</span>
          <span class="ring-label">{{ scan.status === 'Queued' ? 'queued' : 'scanning' }}</span>
        </div>
      </div>

      <div class="mc-counters">
        <div class="mc-counter">
          <span class="mc-num"><CountUp :value="elapsedSeconds" :duration-ms="0" /></span>
          <span class="mc-cap">seconds elapsed</span>
        </div>
        <div class="mc-counter">
          <span class="mc-num"><CountUp :value="scan.coverage?.filesScanned ?? 0" /></span>
          <span class="mc-cap">files scanned</span>
        </div>
        <div class="mc-counter">
          <span class="mc-num"><CountUp :value="scan.coverage?.manifestsFound.length ?? 0" /></span>
          <span class="mc-cap">manifests found</span>
        </div>
        <div class="mc-counter">
          <span class="mc-num"><CountUp :value="findingsSoFar" /></span>
          <span class="mc-cap">findings so far</span>
        </div>
      </div>
    </div>

    <!-- Per-scanner cards: state derived ONLY from coverage + overall state -->
    <div class="scanner-grid">
      <article
        v-for="(meta, i) in scannerMetas"
        :key="meta.id"
        class="scanner-card"
        :class="`state-${scannerState(meta.id)}`"
        :style="{ '--enter-delay': `${i * 70}ms` }"
      >
        <header>
          <span class="scanner-name">{{ meta.label }}</span>
          <span class="scanner-state" :class="`state-${scannerState(meta.id)}`">
            <span v-if="scannerState(meta.id) === 'running'" class="shimmer-dot" aria-hidden="true"></span>
            {{ stateLabel(scannerState(meta.id)) }}
          </span>
        </header>
        <p class="scanner-blurb">{{ meta.blurb }}</p>
        <p class="scanner-result">{{ scannerResult(meta.id) }}</p>
      </article>
    </div>
    <p v-if="isActive" class="engine-note">
      Engine reports overall progress only — per-scanner state is confirmed when each scanner reports back.
    </p>

    <!-- Poll log: snapshots recorded in this browser session, NOT engine logs -->
    <details class="poll-log" open>
      <summary>Scan activity <span class="poll-log-hint">poll log — snapshots from this browser session</span></summary>
      <ul v-if="snapshots.length" class="poll-lines">
        <li v-for="(s, i) in snapshots" :key="i" class="poll-line">
          <span class="poll-time">{{ formatClock(s.at) }}</span>
          <span class="poll-status" :style="{ color: scanStatusColor(s.status) }">{{ s.status }}</span>
          <span class="poll-pct">{{ s.progressPct }}%</span>
        </li>
      </ul>
      <p v-else class="poll-empty">No poll snapshots yet in this session.</p>
    </details>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import type { PollSnapshot, SecurityScan } from '../types'
import { SCANNER_META, SECURITY_SCANNERS, scanStatusColor } from '../types'
import CountUp from './CountUp.vue'

const props = defineProps<{
  scan: SecurityScan
  findingsSoFar: number
  /** Findings count per scanner id (from the findings API), when known. */
  perScannerFindings: Record<string, number>
  /** SBOM component count, when an SBOM was produced. */
  sbomComponentCount: number | null
  /** True when the SBOM fetch returned 404 (no SBOM produced). */
  sbomMissing: boolean
  snapshots: PollSnapshot[]
}>()

const isActive = computed(() => props.scan.status === 'Queued' || props.scan.status === 'Running')

const scannerMetas = SECURITY_SCANNERS.map((id) => ({ id, ...SCANNER_META[id] }))

type ScannerState = 'done' | 'failed' | 'skipped' | 'running' | 'not-reported'

function scannerState(id: string): ScannerState {
  const cov = props.scan.coverage
  if (cov) {
    if (cov.scannersRun.includes(id)) return 'done'
    if (cov.scannersFailed.includes(id)) return 'failed'
    if (cov.scannersSkipped.includes(id)) return 'skipped'
  }
  if (isActive.value) return 'running'
  return 'not-reported'
}

function stateLabel(state: ScannerState): string {
  switch (state) {
    case 'done': return 'Done'
    case 'failed': return 'Failed'
    case 'skipped': return 'Skipped'
    case 'running': return 'Running'
    case 'not-reported': return 'Not reported'
  }
}

function scannerResult(id: string): string {
  const state = scannerState(id)
  if (state === 'failed') return 'Scanner failed — see scan error.'
  if (state === 'skipped') return 'Scanner skipped by the engine.'
  if (state === 'not-reported') return 'No result reported for this scanner.'
  if (id === 'sbom') {
    if (props.sbomMissing) return 'No SBOM produced.'
    if (props.sbomComponentCount === null) return 'SBOM pending…'
    return `${props.sbomComponentCount} component${props.sbomComponentCount === 1 ? '' : 's'} inventoried`
  }
  if (state === 'done') {
    const n = props.perScannerFindings[id]
    if (n === undefined) return 'Completed — findings loading…'
    return n === 0 ? 'No findings reported.' : `${n} finding${n === 1 ? '' : 's'}`
  }
  // running: only what the engine has confirmed so far
  const n = props.perScannerFindings[id]
  if (n !== undefined && n > 0) return `${n} finding${n === 1 ? '' : 's'} so far`
  return 'Waiting for the scanner to report back…'
}

// Progress ring
const circumference = 2 * Math.PI * 58
const dashOffset = computed(() => circumference * (1 - Math.min(100, Math.max(0, props.scan.progressPct)) / 100))

// Elapsed time, ticking client-side from startedAt (or queuedAt before start).
const nowMs = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | null = null
const elapsedSeconds = computed(() => {
  const start = props.scan.startedAt ?? props.scan.queuedAt
  return Math.max(0, Math.floor((nowMs.value - new Date(start).getTime()) / 1000))
})

function formatClock(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString()
  } catch {
    return iso
  }
}

onMounted(() => {
  tickTimer = setInterval(() => {
    nowMs.value = Date.now()
  }, 1000)
})
onUnmounted(() => {
  if (tickTimer) clearInterval(tickTimer)
})
</script>

<style scoped>
.mission-control {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.25rem 1.4rem;
  margin-bottom: 1rem;
  color: #e2e8f0;
}
.mc-top { display: flex; gap: 1.75rem; align-items: center; flex-wrap: wrap; }
.ring-wrap { position: relative; width: 140px; height: 140px; flex-shrink: 0; }
.ring { width: 140px; height: 140px; transform: rotate(-90deg); }
.ring-track { fill: none; stroke: rgba(148, 163, 184, 0.18); stroke-width: 9; }
.ring-fill {
  fill: none;
  stroke: #f59e0b;
  stroke-width: 9;
  stroke-linecap: round;
  transition: stroke-dashoffset 0.6s ease;
}
.ring-center {
  position: absolute; inset: 0;
  display: flex; flex-direction: column; align-items: center; justify-content: center;
}
.ring-pct {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 1.7rem; font-weight: 700; color: #fbbf24; line-height: 1;
}
.ring-label { font-size: 0.75rem; color: #94a3b8; margin-top: 0.3rem; letter-spacing: 0.04em; }
.mc-counters { display: grid; grid-template-columns: repeat(4, minmax(90px, 1fr)); gap: 1rem; flex: 1; min-width: 260px; }
@media (max-width: 640px) { .mc-counters { grid-template-columns: repeat(2, 1fr); } }
.mc-counter { display: flex; flex-direction: column; gap: 0.15rem; border-left: 2px solid rgba(245, 158, 11, 0.45); padding-left: 0.7rem; }
.mc-num {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 1.55rem; font-weight: 700; color: #7dd3fc; line-height: 1.1;
}
.mc-cap { font-size: 0.74rem; color: #94a3b8; }

.scanner-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 0.7rem; margin-top: 1.25rem; }
@media (max-width: 900px) { .scanner-grid { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 520px) { .scanner-grid { grid-template-columns: 1fr; } }
.scanner-card {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(148, 163, 184, 0.22);
  border-radius: 10px;
  padding: 0.8rem 0.9rem;
  animation: mc-enter 0.45s ease both;
  animation-delay: var(--enter-delay, 0ms);
}
@keyframes mc-enter { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
.scanner-card header { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; }
.scanner-name { font-weight: 700; font-size: 0.88rem; }
.scanner-state { font-size: 0.72rem; font-weight: 700; padding: 0.15rem 0.5rem; border-radius: 9999px; display: inline-flex; align-items: center; gap: 0.35rem; white-space: nowrap; }
.scanner-state.state-done { background: rgba(16, 185, 129, 0.15); color: #34d399; }
.scanner-state.state-failed { background: rgba(244, 63, 94, 0.15); color: #fb7185; }
.scanner-state.state-skipped { background: rgba(245, 158, 11, 0.15); color: #fbbf24; }
.scanner-state.state-running { background: rgba(56, 189, 248, 0.15); color: #7dd3fc; }
.scanner-state.state-not-reported { background: rgba(148, 163, 184, 0.15); color: #94a3b8; }
.shimmer-dot { width: 7px; height: 7px; border-radius: 50%; background: #38bdf8; animation: shimmer 1.1s ease-in-out infinite; }
@keyframes shimmer { 0%, 100% { opacity: 0.35; } 50% { opacity: 1; } }
.scanner-card.state-running { border-color: rgba(56, 189, 248, 0.4); }
.scanner-card.state-failed { border-color: rgba(244, 63, 94, 0.45); }
.scanner-blurb { margin: 0.45rem 0 0.3rem; font-size: 0.76rem; color: #94a3b8; }
.scanner-result {
  margin: 0;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 0.8rem; color: #e2e8f0;
}
.engine-note { margin: 0.8rem 0 0; font-size: 0.78rem; color: #fbbf24; }

.poll-log { margin-top: 1.1rem; border-top: 1px solid rgba(148, 163, 184, 0.18); padding-top: 0.8rem; }
.poll-log summary { cursor: pointer; font-weight: 600; font-size: 0.85rem; color: #7dd3fc; }
.poll-log-hint { font-weight: 400; font-size: 0.76rem; color: #94a3b8; margin-left: 0.4rem; }
.poll-lines { list-style: none; margin: 0.6rem 0 0; padding: 0; max-height: 160px; overflow-y: auto; display: flex; flex-direction: column; gap: 0.2rem; }
.poll-line {
  display: flex; gap: 0.9rem;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 0.78rem; color: #cbd5e1;
}
.poll-time { color: #64748b; }
.poll-pct { margin-left: auto; color: #fbbf24; }
.poll-empty { margin: 0.6rem 0 0; font-size: 0.8rem; color: #94a3b8; }
@media (prefers-reduced-motion: reduce) {
  .scanner-card, .shimmer-dot, .ring-fill { animation: none; transition: none; }
}
</style>
