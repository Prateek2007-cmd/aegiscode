<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Posture trends section on the client dashboard. Shows the before/after story
  — findings by severity per completed scan, mean time to resolve, and
  new-vs-fixed per scan — aggregated by the server from stored scans and
  findings. With fewer than 2 completed scans it says there is not enough
  history yet; it never draws a trend line from a single point or invents a
  "0 criticals" posture.
-->
<template>
  <section class="trends-section" aria-label="Posture trends">
    <div class="trends-section-header">
      <div>
        <h3 class="trends-section-title">Posture trends</h3>
        <p class="trends-section-subtitle">
          How the client's security posture moved across scans — the before/after story.
        </p>
      </div>
    </div>

    <p v-if="unavailable" class="trends-section-note">
      Posture trends need the API update — not available yet.
    </p>

    <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <div v-if="loading" class="trends-section-loading">Loading posture trends…</div>

    <template v-else-if="!unavailable && !loadError && trends">
      <template v-if="!trends.hasEnoughHistory">
        <p class="trends-section-empty" role="status">
          {{ trends.message ?? 'Not enough history yet — run at least 2 completed scans to see trends.' }}
        </p>
        <p class="trends-section-meta">
          <template v-if="trends.scanCount === 0">No completed scans yet.</template>
          <template v-else-if="trends.scanCount === 1">1 completed scan so far — one more and the trend appears.</template>
          <template v-else>{{ trends.scanCount }} completed scans so far.</template>
        </p>
      </template>

      <template v-else>
        <p v-if="headline" class="trends-headline" role="status">{{ headline }}</p>

        <div class="trends-stats">
          <div class="trends-stat">
            <span class="trends-stat-value">{{ trends.scanCount }}</span>
            <span class="trends-stat-label">{{ trends.scanCount === 1 ? 'completed scan' : 'completed scans' }}</span>
          </div>
          <div class="trends-stat">
            <span class="trends-stat-value">{{ latestTotal }}</span>
            <span class="trends-stat-label">findings in the latest scan</span>
          </div>
          <div class="trends-stat">
            <span class="trends-stat-value">{{ mttrLabel }}</span>
            <span class="trends-stat-label">
              mean time to resolve
              <template v-if="trends.resolvedFindingsCount > 0">
                ({{ trends.resolvedFindingsCount }}
                {{ trends.resolvedFindingsCount === 1 ? 'finding' : 'findings' }} fixed)
              </template>
            </span>
          </div>
        </div>

        <p v-if="trends.meanTimeToResolveDays === null" class="trends-section-meta">
          No findings fixed yet — mean time to resolve appears once a finding disappears from a scan.
        </p>

        <div class="trends-block">
          <h4>Criticals over time</h4>
          <svg class="trends-line" viewBox="0 0 600 140" role="img" :aria-label="criticalsAriaLabel">
            <line x1="30" y1="118" x2="590" y2="118" stroke="var(--color-muted-soft)" stroke-width="1" />
            <polyline :points="criticalsPolyline" fill="none" stroke="var(--color-danger)" stroke-width="2" />
            <g v-for="p in criticalPoints" :key="p.scanId">
              <circle :cx="p.x" :cy="p.y" r="5" fill="var(--color-danger)" stroke="var(--color-surface)" stroke-width="1.5">
                <title>{{ p.title }}</title>
              </circle>
              <text :x="p.x" :y="134" text-anchor="middle" class="trends-line-label">{{ p.dateLabel }}</text>
            </g>
          </svg>
          <p class="trends-note">One point per completed scan ({{ trends.points.length }}). Point height is critical findings in that scan.</p>
        </div>

        <div class="trends-block">
          <h4>New vs fixed per scan</h4>
          <ul class="trends-scan-list">
            <li v-for="point in trends.points" :key="point.scanId" class="trends-scan">
              <div class="trends-scan-head">
                <span class="trends-scan-date">{{ formatScanDate(point.at) }}</span>
                <span class="trends-scan-repo" :title="point.repositoryUrl">{{ shortRepo(point.repositoryUrl) }}</span>
                <span class="trends-scan-total">{{ point.total }} {{ point.total === 1 ? 'finding' : 'findings' }}</span>
              </div>
              <div
                v-if="point.total > 0"
                class="trends-stacked-bar"
                role="img"
                :aria-label="stackedAriaLabel(point)"
              >
                <span
                  v-for="sev in SEVERITY_ORDER"
                  :key="sev"
                  class="trends-stacked-seg"
                  :style="{ width: `${(severityCount(point, sev) / point.total) * 100}%`, background: severityColor(sev) }"
                  :title="`${sev}: ${severityCount(point, sev)}`"
                ></span>
              </div>
              <p v-else class="trends-scan-clean">Clean scan — 0 findings.</p>
              <p class="trends-scan-flow">
                <span class="trends-new">+{{ point.newFindings }} new</span>
                ·
                <span class="trends-fixed">−{{ point.fixedFindings }} fixed</span>
                <span class="trends-severity-breakdown">
                  Critical {{ point.critical }} · High {{ point.high }} · Medium {{ point.medium }} · Low {{ point.low }} · Info {{ point.info }}
                </span>
              </p>
            </li>
          </ul>
        </div>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  formatMeanTimeToResolve,
  getSecurityTrends,
  isSecurityTrendsUnavailable,
  trendsHeadline,
} from '@/services/securityTrendsService'
import type { SecurityTrendPoint, SecurityTrends } from '@/services/securityTrendsService'
import { SEVERITY_ORDER, severityColor } from '../types'
import type { SecurityFindingSeverity } from '../types'

const props = defineProps<{
  clientId: string
}>()

const trends = ref<SecurityTrends | null>(null)
const loading = ref(true)
const unavailable = ref(false)
const loadError = ref<string | null>(null)

const headline = computed(() => (trends.value ? trendsHeadline(trends.value) : null))
const latestTotal = computed(() => {
  const points = trends.value?.points ?? []
  return points.length === 0 ? 0 : points[points.length - 1].total
})
const mttrLabel = computed(() => {
  const label = trends.value ? formatMeanTimeToResolve(trends.value.meanTimeToResolveDays) : null
  return label ?? '—'
})

function formatScanDate(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}

function shortRepo(url: string): string {
  const withoutScheme = url.replace(/^https?:\/\//, '')
  const parts = withoutScheme.split('/').filter(Boolean)
  return parts.length >= 3 ? parts.slice(1).join('/') : withoutScheme
}

function severityCount(point: SecurityTrendPoint, sev: SecurityFindingSeverity): number {
  return point[sev.toLowerCase() as 'critical' | 'high' | 'medium' | 'low' | 'info']
}

function stackedAriaLabel(point: SecurityTrendPoint): string {
  return SEVERITY_ORDER.map((s) => `${s}: ${severityCount(point, s)}`).join(', ')
}

// --- Criticals line: one point per scan, evenly spaced, like PostureView's timeline ---
interface CriticalPoint {
  scanId: string
  x: number
  y: number
  title: string
  dateLabel: string
}

const criticalPoints = computed<CriticalPoint[]>(() => {
  const points = trends.value?.points ?? []
  if (points.length === 0) return []
  const maxCritical = Math.max(...points.map((p) => p.critical), 1)
  return points.map((p, i) => {
    const x = points.length === 1 ? 310 : 40 + (i / (points.length - 1)) * 540
    const y = 112 - (p.critical / maxCritical) * 96
    return {
      scanId: p.scanId,
      x,
      y,
      title: `${formatScanDate(p.at)} — ${p.critical} criticals, ${p.total} findings`,
      dateLabel: new Date(p.at).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
    }
  })
})

const criticalsPolyline = computed(() =>
  criticalPoints.value.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' '),
)
const criticalsAriaLabel = computed(() =>
  criticalPoints.value.map((p) => p.title).join('; '),
)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  unavailable.value = false
  try {
    trends.value = await getSecurityTrends(props.clientId)
  } catch (error) {
    if (isSecurityTrendsUnavailable(error)) {
      unavailable.value = true
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to load posture trends.'
    }
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.trends-section {
  background: var(--color-surface);
  border-radius: 12px;
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.trends-section-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.trends-section-title {
  font-size: 1.1rem;
  margin: 0 0 4px;
}

.trends-section-subtitle {
  color: var(--color-text-muted);
  margin: 0;
}

.trends-section-meta,
.trends-section-note,
.trends-section-loading,
.trends-section-empty {
  color: var(--color-text-muted);
  margin: 0;
}

.trends-headline {
  font-size: 1.25rem;
  font-weight: 700;
  margin: 0;
  color: var(--color-warning);
}

.trends-stats {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
}

.trends-stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
  background: var(--surface-subtle);
  border: 1px solid var(--color-muted-soft);
  border-radius: 8px;
  padding: 10px 14px;
  min-width: 140px;
}

.trends-stat-value {
  font-size: 1.25rem;
  font-weight: 700;
  font-family: var(--font-mono);
}

.trends-stat-label {
  font-size: 0.8rem;
  color: var(--color-text-muted);
}

.trends-block h4 {
  margin: 0 0 0.7rem;
  font-size: 0.95rem;
}

.trends-line {
  width: 100%;
  height: auto;
  display: block;
}

.trends-line-label {
  fill: var(--color-text-muted);
  font-size: 10px;
}

.trends-note {
  margin: 0.4rem 0 0;
  font-size: 0.76rem;
  color: var(--color-text-muted);
}

.trends-scan-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.trends-scan {
  background: var(--surface-subtle);
  border: 1px solid var(--color-muted-soft);
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.trends-scan-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
  font-size: 0.85rem;
}

.trends-scan-date {
  font-weight: 700;
}

.trends-scan-repo {
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.trends-scan-total {
  margin-left: auto;
  font-weight: 700;
  color: var(--color-warning);
}

.trends-stacked-bar {
  display: flex;
  height: 10px;
  border-radius: 9999px;
  overflow: hidden;
  background: var(--color-muted-soft);
}

.trends-stacked-seg {
  height: 100%;
}

.trends-scan-clean {
  margin: 0;
  color: var(--color-success);
  font-size: 0.85rem;
}

.trends-scan-flow {
  margin: 0;
  font-size: 0.85rem;
  color: var(--color-text);
}

.trends-new {
  color: var(--color-danger);
  font-weight: 700;
}

.trends-fixed {
  color: var(--color-success);
  font-weight: 700;
}

.trends-severity-breakdown {
  display: block;
  margin-top: 4px;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}
</style>
