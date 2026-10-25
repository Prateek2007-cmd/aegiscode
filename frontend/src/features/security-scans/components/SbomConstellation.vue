<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="constellation" aria-label="SBOM constellation">
    <header class="constellation-head">
      <h4>SBOM constellation</h4>
      <p class="constellation-sub">
        Every dot is a component from the CycloneDX SBOM. Size and colour show the worst
        dep-scan finding matched to that component — neutral dots have no recorded findings.
      </p>
    </header>

    <div v-if="sbomMissing" class="constellation-empty">
      <p>No SBOM produced for this scan — nothing to map.</p>
    </div>
    <div v-else-if="matches.length === 0" class="constellation-empty">
      <p>The SBOM lists no components — nothing to map.</p>
    </div>
    <template v-else>
      <div class="constellation-body">
        <svg
          class="constellation-svg"
          viewBox="0 0 640 420"
          role="group"
          aria-label="SBOM components arranged around the repository"
        >
          <circle cx="320" cy="210" r="150" class="orbit-ring" />
          <circle cx="320" cy="210" r="95" class="orbit-ring" />
          <circle cx="320" cy="210" r="46" class="orbit-ring" />
          <g class="repo-node">
            <circle cx="320" cy="210" r="26" class="repo-core" />
            <text x="320" y="214" text-anchor="middle" class="repo-label">repo</text>
          </g>
          <g v-for="dot in dots" :key="dot.key">
            <circle
              :cx="dot.x"
              :cy="dot.y"
              :r="dot.r"
              :fill="dot.color"
              :class="{ 'dot-neutral': dot.maxSeverity === null }"
              tabindex="0"
              role="button"
              :aria-label="dot.ariaLabel"
              @mouseenter="hovered = dot"
              @mouseleave="hovered = null"
              @focus="hovered = dot"
              @blur="hovered = null"
            >
              <title>{{ dot.ariaLabel }}</title>
            </circle>
          </g>
        </svg>
        <div v-if="hovered" class="dot-tip" role="status">
          <p class="dot-tip-name">{{ hovered.displayName }} <span v-if="hovered.version">{{ hovered.version }}</span></p>
          <p v-if="hovered.maxSeverity" class="dot-tip-vuln">
            {{ hovered.vulnCount }} matched {{ hovered.vulnCount === 1 ? 'vulnerability' : 'vulnerabilities' }} ·
            worst: <span :style="{ color: severityColor(hovered.maxSeverity) }">{{ hovered.maxSeverity }}</span>
            <span v-if="hovered.topRuleId"> · {{ hovered.topRuleId }}</span>
          </p>
          <p v-else class="dot-tip-vuln">No recorded findings for this component.</p>
        </div>
        <p v-else class="dot-tip dot-tip-idle">Hover or focus a dot to inspect a component.</p>
      </div>
      <p v-if="sampled" class="constellation-note">
        Showing 120 of {{ matches.length }} components (deterministic sample).
      </p>
      <p class="constellation-note">
        Matches use dep-scan findings loaded for this scan ({{ depScanFindingCount }} loaded).
      </p>
      <ul class="constellation-legend">
        <li v-for="sev in SEVERITY_ORDER" :key="sev">
          <span class="legend-dot" :style="{ background: severityColor(sev) }"></span>{{ sev }}
        </li>
        <li><span class="legend-dot legend-neutral"></span>No recorded findings</li>
      </ul>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import type { CycloneDxBom, SecurityFinding, SecurityFindingSeverity } from '../types'
import { SEVERITY_ORDER, severityColor } from '../types'
import { matchSbomComponents, type SbomComponentMatch } from '../scanInsights'

const MAX_DOTS = 120

const props = defineProps<{
  sbom: CycloneDxBom | null
  sbomMissing: boolean
  depScanFindings: SecurityFinding[]
}>()

const matches = computed(() =>
  matchSbomComponents(props.sbom?.components ?? [], props.depScanFindings),
)
const depScanFindingCount = computed(() => props.depScanFindings.length)

/** Deterministic sample: sort by name, take an evenly-spaced stride. */
const sampled = computed(() => matches.value.length > MAX_DOTS)
const shown = computed(() => {
  const all = [...matches.value].sort((a, b) => a.displayName.localeCompare(b.displayName))
  if (all.length <= MAX_DOTS) return all
  const stride = all.length / MAX_DOTS
  return Array.from({ length: MAX_DOTS }, (_, i) => all[Math.floor(i * stride)])
})

/** Tiny deterministic hash so dot positions are stable across renders. */
function hashString(s: string): number {
  let h = 2166136261
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return Math.abs(h)
}

interface Dot extends SbomComponentMatch {
  key: string
  x: number
  y: number
  r: number
  color: string
  ariaLabel: string
}

const radiusFor = (sev: SecurityFindingSeverity | null): number => {
  switch (sev) {
    case 'Critical': return 9
    case 'High': return 7.5
    case 'Medium': return 6
    case 'Low': return 5
    case 'Info': return 4.5
    default: return 3.5
  }
}

const dots = computed<Dot[]>(() =>
  shown.value.map((m, i) => {
    const h = hashString(`${m.displayName}@${m.version}`)
    // Golden-angle spiral, kept inside the 640x420 field and outside the repo core.
    const angle = (i * 2.399963 + (h % 100) / 100) * Math.PI
    const radius = 52 + ((h % 1000) / 1000) * 150
    const x = 320 + Math.cos(angle) * radius * 1.25
    const y = 210 + Math.sin(angle) * radius * 0.85
    const maxSeverity = m.maxSeverity
    return {
      ...m,
      key: `${m.displayName}@${m.version}`,
      x: Math.min(632, Math.max(8, x)),
      y: Math.min(412, Math.max(8, y)),
      r: radiusFor(maxSeverity),
      color: maxSeverity ? severityColor(maxSeverity) : '#64748b',
      ariaLabel: maxSeverity
        ? `${m.displayName} ${m.version} — ${m.vulnCount} matched ${m.vulnCount === 1 ? 'vulnerability' : 'vulnerabilities'}, worst ${maxSeverity}${m.topRuleId ? `, ${m.topRuleId}` : ''}`
        : `${m.displayName} ${m.version} — no recorded findings`,
    }
  }),
)

const hovered = ref<Dot | null>(null)
</script>

<style scoped>
.constellation {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.1rem 1.4rem;
  margin-bottom: 1rem;
  color: #e2e8f0;
}
.constellation-head h4 { margin: 0 0 0.3rem; font-size: 1rem; }
.constellation-sub { margin: 0; font-size: 0.8rem; color: #94a3b8; max-width: 75ch; }
.constellation-empty { padding: 1.5rem 0 0.5rem; color: #94a3b8; font-size: 0.88rem; }
.constellation-empty p { margin: 0; }
.constellation-body { position: relative; }
.constellation-svg { width: 100%; height: auto; display: block; }
.orbit-ring { fill: none; stroke: rgba(56, 189, 248, 0.12); stroke-width: 1; }
.repo-core { fill: #f59e0b; stroke: #fbbf24; stroke-width: 2; }
.repo-label {
  fill: #0a1628;
  font-size: 11px;
  font-weight: 700;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
}
.constellation-svg circle[tabindex] { cursor: pointer; outline: none; }
.constellation-svg circle[tabindex]:hover,
.constellation-svg circle[tabindex]:focus { stroke: #f8fafc; stroke-width: 1.5; }
.dot-neutral { opacity: 0.75; }
.dot-tip {
  min-height: 3.4rem;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(148, 163, 184, 0.25);
  border-radius: 10px;
  padding: 0.6rem 0.9rem;
  margin-top: 0.4rem;
}
.dot-tip-idle { color: #64748b; font-size: 0.8rem; }
.dot-tip-name {
  margin: 0 0 0.25rem;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 0.85rem; font-weight: 700;
}
.dot-tip-name span { color: #94a3b8; font-weight: 400; }
.dot-tip-vuln { margin: 0; font-size: 0.8rem; color: #cbd5e1; }
.constellation-note { margin: 0.6rem 0 0; font-size: 0.76rem; color: #94a3b8; }
.constellation-legend {
  list-style: none; margin: 0.7rem 0 0; padding: 0;
  display: flex; flex-wrap: wrap; gap: 0.4rem 1.1rem; font-size: 0.78rem; color: #cbd5e1;
}
.legend-dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 0.35rem; }
.legend-neutral { background: #64748b; }
</style>
