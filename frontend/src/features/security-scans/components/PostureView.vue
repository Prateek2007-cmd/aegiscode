<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="posture" aria-label="Findings posture">
    <div class="posture-grid">
      <!-- Severity dial + stacked bar -->
      <div class="posture-block">
        <h4>Severity posture</h4>
        <div class="dial-row">
          <svg class="dial" viewBox="0 0 200 112" role="img" :aria-label="dialAriaLabel">
            <path d="M 12 100 A 88 88 0 0 1 188 100" fill="none" stroke="rgba(148,163,184,0.18)" stroke-width="14" stroke-linecap="round" />
            <path
              v-for="seg in dialSegments"
              :key="seg.severity"
              :d="seg.path"
              fill="none"
              :stroke="severityColor(seg.severity)"
              stroke-width="14"
              stroke-linecap="round"
            />
            <text x="100" y="88" text-anchor="middle" class="dial-total">{{ totalFindings }}</text>
            <text x="100" y="106" text-anchor="middle" class="dial-total-label">findings</text>
          </svg>
          <ul class="dial-legend">
            <li v-for="sev in SEVERITY_ORDER" :key="sev">
              <span class="legend-dot" :style="{ background: severityColor(sev) }"></span>
              {{ sev }} <strong>{{ summary[sev.toLowerCase() as keyof SecurityScanSummary] }}</strong>
            </li>
          </ul>
        </div>
        <div v-if="totalFindings > 0" class="stacked-bar" role="img" :aria-label="stackedAriaLabel">
          <span
            v-for="sev in SEVERITY_ORDER"
            :key="sev"
            class="stacked-seg"
            :style="{ width: `${(summaryCount(sev) / totalFindings) * 100}%`, background: severityColor(sev) }"
            :title="`${sev}: ${summaryCount(sev)}`"
          ></span>
        </div>
        <p v-else class="posture-empty">No findings reported for this scan yet.</p>
      </div>

      <!-- Timeline across scans -->
      <div class="posture-block">
        <h4>Findings over time</h4>
        <div v-if="timelinePoints.length === 0" class="posture-empty">
          <p>No scans to compare yet — run a scan to start the timeline.</p>
        </div>
        <template v-else>
          <svg class="timeline" viewBox="0 0 600 140" role="img" :aria-label="timelineAriaLabel">
            <line x1="30" y1="118" x2="590" y2="118" stroke="rgba(148,163,184,0.25)" stroke-width="1" />
            <polyline :points="timelinePolyline" fill="none" stroke="#38bdf8" stroke-width="2" />
            <g v-for="p in timelinePoints" :key="p.scanId">
              <circle :cx="p.x" :cy="p.y" r="5" :fill="p.color" stroke="#0a1628" stroke-width="1.5">
                <title>{{ p.title }}</title>
              </circle>
              <text :x="p.x" :y="134" text-anchor="middle" class="timeline-label">{{ p.dateLabel }}</text>
            </g>
          </svg>
          <p class="timeline-note">
            One point per scan ({{ timelinePoints.length }}). Point height is total findings; colour is the worst severity in that scan.
            <span v-if="timelinePoints.length === 1">Run another scan to compare.</span>
          </p>
        </template>
      </div>
    </div>

    <!-- File heatmap -->
    <div class="posture-block heatmap-block">
      <h4>Hottest files</h4>
      <div v-if="heatEntries.length === 0" class="posture-empty">
        <p>No findings loaded for this scan yet — nothing to map to files.</p>
      </div>
      <ul v-else class="heatmap">
        <li v-for="entry in heatEntries" :key="entry.filePath" class="heat-row">
          <span class="heat-file" :title="entry.filePath">{{ entry.filePath }}</span>
          <span class="heat-track">
            <span
              class="heat-bar"
              :style="{ width: `${(entry.count / heatMax) * 100}%`, background: severityColor(entry.maxSeverity) }"
            ></span>
          </span>
          <span class="heat-count">{{ entry.count }}</span>
          <span class="heat-sev" :style="{ color: severityColor(entry.maxSeverity) }">{{ entry.maxSeverity }}</span>
        </li>
      </ul>
      <p v-if="heatEntries.length" class="heatmap-note">
        From the {{ loadedFindingsCount }} findings loaded on this page ({{ totalFindings }} total in this scan).
      </p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { SecurityFinding, SecurityScan, SecurityScanSummary } from '../types'
import { SEVERITY_ORDER, severityColor } from '../types'
import { aggregateFindingsByFile, summaryTotal } from '../scanInsights'

const props = defineProps<{
  scan: SecurityScan
  /** All scans for this client, for the timeline. */
  scans: SecurityScan[]
  /** Findings currently loaded on the findings page. */
  findings: SecurityFinding[]
  totalFindings: number
}>()

const summary = computed(() => props.scan.summary)
const totalFindings = computed(() => summaryTotal(props.scan.summary))
const loadedFindingsCount = computed(() => props.findings.length)

function summaryCount(sev: (typeof SEVERITY_ORDER)[number]): number {
  return summary.value[sev.toLowerCase() as keyof SecurityScanSummary]
}

// --- Severity dial: semicircle gauge, one arc per severity with findings ---
const dialSegments = computed(() => {
  const total = totalFindings.value
  if (total === 0) return []
  const r = 88
  const cx = 100
  const cy = 100
  const segs: Array<{ severity: (typeof SEVERITY_ORDER)[number]; path: string }> = []
  let acc = 0
  for (const sev of SEVERITY_ORDER) {
    const n = summaryCount(sev)
    if (n === 0) continue
    const startAngle = Math.PI * (1 - acc / total)
    const endAngle = Math.PI * (1 - (acc + n) / total)
    const x1 = cx + r * Math.cos(startAngle)
    const y1 = cy - r * Math.sin(startAngle)
    const x2 = cx + r * Math.cos(endAngle)
    const y2 = cy - r * Math.sin(endAngle)
    const largeArc = (endAngle - startAngle) > Math.PI ? 1 : 0
    // Sweep flag 1: clockwise from left end of the semicircle to the right.
    segs.push({
      severity: sev,
      path: `M ${x1.toFixed(1)} ${y1.toFixed(1)} A ${r} ${r} 0 ${largeArc} 1 ${x2.toFixed(1)} ${y2.toFixed(1)}`,
    })
    acc += n
  }
  return segs
})
const dialAriaLabel = computed(() =>
  totalFindings.value === 0
    ? 'No findings reported'
    : SEVERITY_ORDER.map((s) => `${s}: ${summaryCount(s)}`).join(', '),
)
const stackedAriaLabel = computed(() => dialAriaLabel.value)

// --- Timeline: one point per scan ---
interface TimelinePoint {
  scanId: string
  x: number
  y: number
  color: string
  title: string
  dateLabel: string
}

const timelinePoints = computed<TimelinePoint[]>(() => {
  const ordered = [...props.scans].sort(
    (a, b) => new Date(a.queuedAt).getTime() - new Date(b.queuedAt).getTime(),
  )
  if (ordered.length === 0) return []
  const totals = ordered.map((s) => summaryTotal(s.summary))
  const maxTotal = Math.max(...totals, 1)
  const times = ordered.map((s) => new Date(s.queuedAt).getTime())
  const minT = Math.min(...times)
  const maxT = Math.max(...times)
  const span = Math.max(maxT - minT, 1)
  return ordered.map((s, i) => {
    const x = ordered.length === 1 ? 310 : 40 + ((times[i] - minT) / span) * 540
    const y = 112 - (totals[i] / maxTotal) * 96
    const worst = SEVERITY_ORDER.find((sev) => summaryCountFor(s, sev) > 0)
    return {
      scanId: s.scanId,
      x,
      y,
      color: worst ? severityColor(worst) : '#94a3b8',
      title: `${new Date(s.queuedAt).toLocaleString()} — ${totals[i]} findings (${s.status})`,
      dateLabel: new Date(s.queuedAt).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
    }
  })
})

function summaryCountFor(s: SecurityScan, sev: (typeof SEVERITY_ORDER)[number]): number {
  return s.summary[sev.toLowerCase() as keyof SecurityScanSummary]
}

const timelinePolyline = computed(() =>
  timelinePoints.value.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' '),
)
const timelineAriaLabel = computed(() =>
  timelinePoints.value.map((p) => p.title).join('; '),
)

// --- File heatmap ---
const heatEntries = computed(() => aggregateFindingsByFile(props.findings, 8))
const heatMax = computed(() => Math.max(...heatEntries.value.map((e) => e.count), 1))
</script>

<style scoped>
.posture {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.1rem 1.4rem;
  margin-bottom: 1rem;
  color: #e2e8f0;
}
.posture-grid { display: grid; grid-template-columns: 1fr 1.4fr; gap: 1.5rem; }
@media (max-width: 900px) { .posture-grid { grid-template-columns: 1fr; } }
.posture-block h4 { margin: 0 0 0.7rem; font-size: 0.95rem; }
.dial-row { display: flex; align-items: center; gap: 1rem; flex-wrap: wrap; }
.dial { width: 200px; height: 112px; flex-shrink: 0; }
.dial-total {
  fill: #fbbf24;
  font-size: 26px; font-weight: 700;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
}
.dial-total-label { fill: #94a3b8; font-size: 10px; }
.dial-legend { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.82rem; }
.dial-legend strong { font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace; }
.legend-dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 0.35rem; }
.stacked-bar { display: flex; height: 10px; border-radius: 9999px; overflow: hidden; background: rgba(148, 163, 184, 0.15); margin-top: 0.8rem; }
.stacked-seg { height: 100%; }
.posture-empty { color: #94a3b8; font-size: 0.85rem; }
.posture-empty p { margin: 0; }
.timeline { width: 100%; height: auto; display: block; }
.timeline-label { fill: #94a3b8; font-size: 10px; }
.timeline-note { margin: 0.4rem 0 0; font-size: 0.76rem; color: #94a3b8; }
.heatmap-block { margin-top: 1.4rem; }
.heatmap { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.45rem; }
.heat-row { display: grid; grid-template-columns: minmax(140px, 1.2fr) 2fr 2.2rem 5rem; gap: 0.7rem; align-items: center; font-size: 0.8rem; }
.heat-file {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #cbd5e1;
}
.heat-track { height: 8px; background: rgba(148, 163, 184, 0.15); border-radius: 9999px; overflow: hidden; }
.heat-bar { display: block; height: 100%; border-radius: 9999px; }
.heat-count { font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace; font-weight: 700; text-align: right; color: #fbbf24; }
.heat-sev { font-size: 0.74rem; font-weight: 700; }
.heatmap-note { margin: 0.6rem 0 0; font-size: 0.76rem; color: #94a3b8; }
</style>
