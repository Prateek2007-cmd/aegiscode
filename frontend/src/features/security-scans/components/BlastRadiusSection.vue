<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="blast-radius">
    <div class="blast-head">
      <h4>Blast radius</h4>
      <span class="blast-hint">What breaks if this is exploited — traced from the actual code, not guessed.</span>
    </div>

    <div v-if="loading" class="loading-state"><div class="spinner"></div><p>Analyzing impact…</p></div>
    <div v-else-if="error" class="banner banner-error" role="alert">
      {{ error }} <button class="btn-link" @click="load">Retry</button>
    </div>
    <div v-else-if="result && result.status === 'Unavailable'" class="unavailable" role="status">
      <p><strong>Blast radius unavailable.</strong></p>
      <p>{{ result.reason ?? 'The analysis could not be computed for this finding.' }}</p>
      <p v-if="result.seed" class="seed-line">Traced from: {{ seedLabel(result.seed) }}</p>
    </div>
    <div v-else-if="result" class="blast-body">
      <p class="blast-summary">
        <strong>{{ result.affectedModules.length }}</strong> affected {{ result.affectedModules.length === 1 ? 'module' : 'modules' }}
        · <strong>{{ result.entryPoints.length }}</strong> {{ result.entryPoints.length === 1 ? 'entry point' : 'entry points' }}
        · {{ result.filesScanned }} files scanned
        <span v-if="result.truncated" class="warn">· results capped — the map may be incomplete</span>
      </p>
      <p v-if="result.seed" class="seed-line">Traced from: {{ seedLabel(result.seed) }}</p>

      <!-- Simple impact graph: entry points → affected modules → seed. -->
      <div v-if="result.edges.length > 0" class="blast-graph" aria-label="Impact graph">
        <div class="graph-layer">
          <span class="graph-label">Entry points</span>
          <span v-for="ep in result.entryPoints" :key="ep.filePath + (ep.definition ?? '')" class="graph-node entry">
            {{ ep.definition ?? ep.filePath }}<span class="graph-file">{{ ep.filePath }}:{{ ep.line }}</span>
          </span>
          <span v-if="result.entryPoints.length === 0" class="graph-node muted">—</span>
        </div>
        <div class="graph-arrow" aria-hidden="true">↓ calls into ↓</div>
        <div class="graph-layer">
          <span class="graph-label">Affected modules</span>
          <span v-for="m in result.affectedModules" :key="m.filePath" class="graph-node module">
            {{ shortPath(m.filePath) }}<span class="graph-file">{{ m.callers.length }} {{ m.callers.length === 1 ? 'caller' : 'callers' }}</span>
          </span>
        </div>
        <div class="graph-arrow" aria-hidden="true">↓ reaches ↓</div>
        <div class="graph-layer">
          <span class="graph-label">Vulnerable code</span>
          <span class="graph-node seed">{{ seedLabel(result.seed) }}</span>
        </div>
      </div>

      <!-- Affected modules: every link is one the analyzer actually found. -->
      <ul v-if="result.affectedModules.length > 0" class="modules-list">
        <li v-for="m in result.affectedModules" :key="m.filePath" class="module-card">
          <code class="module-path">{{ m.filePath }}</code>
          <ul class="callers-list">
            <li v-for="c in m.callers" :key="c.name ?? '' + c.startLine">
              <span class="caller-name">{{ c.name ?? '(file scope)' }}</span>
              <span class="caller-lines">lines {{ c.referenceLines.join(', ') }}</span>
              <span class="verify-badge confirmed">confirmed call</span>
            </li>
            <li v-for="line in m.importLines" :key="'import-' + line">
              <span class="caller-name">imports the vulnerable code</span>
              <span class="caller-lines">line {{ line }}</span>
              <span class="verify-badge import">import — name match</span>
            </li>
          </ul>
        </li>
      </ul>
      <p v-else class="empty-note">No callers found in the analyzed files — nothing in this repository references the vulnerable code.</p>

      <!-- Data-flow notes: template-generated from verified edges only. -->
      <ul v-if="result.notes.length > 0" class="notes-list">
        <li v-for="(note, i) in result.notes" :key="i">{{ note }}</li>
      </ul>

      <details v-if="result.totalSkipped > 0" class="skipped">
        <summary>{{ result.totalSkipped }} {{ result.totalSkipped === 1 ? 'file' : 'files' }} skipped</summary>
        <ul>
          <li v-for="s in result.skippedFiles" :key="s.path"><code>{{ s.path }}</code> — {{ s.reason }}</li>
          <li v-if="result.totalSkipped > result.skippedFiles.length">…and {{ result.totalSkipped - result.skippedFiles.length }} more.</li>
        </ul>
      </details>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getBlastRadius } from '../securityScansService'
import type { BlastRadiusResponse, BlastRadiusSeed } from '../types'

const props = defineProps<{ clientId: string; scanId: string; findingId: string }>()

const result = ref<BlastRadiusResponse | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

function seedLabel(seed: BlastRadiusSeed | null | undefined): string {
  if (!seed) return 'this finding'
  if (seed.kind === 'package') return `package ${seed.packageName ?? 'unknown'}`
  const file = seed.filePath ?? 'unknown file'
  return seed.symbol ? `${file}:${seed.symbol}` : file
}

function shortPath(path: string): string {
  const parts = path.split('/')
  return parts.length > 3 ? `…/${parts.slice(-3).join('/')}` : path
}

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    result.value = await getBlastRadius(props.clientId, props.scanId, props.findingId)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load blast radius.'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
})
</script>

<style scoped>
.blast-radius { margin-top: 0.9rem; border-top: 1px solid rgba(255,255,255,0.06); padding-top: 0.8rem; }
.blast-head { display: flex; align-items: baseline; gap: 0.6rem; flex-wrap: wrap; margin-bottom: 0.5rem; }
.blast-head h4 { margin: 0; font-size: 0.9rem; }
.blast-hint { font-size: 0.78rem; color: var(--color-text-muted); }
.loading-state { text-align: center; padding: 1.2rem; color: var(--color-text-muted); }
.spinner { width: 24px; height: 24px; margin: 0 auto 0.6rem; border: 3px solid rgba(56,189,248,0.2); border-top-color: #38bdf8; border-radius: 50%; animation: spin 0.8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
.banner-error { background: rgba(244,63,94,0.1); border: 1px solid rgba(244,63,94,0.3); color: #fda4af; padding: 0.6rem 0.8rem; border-radius: 8px; font-size: 0.84rem; }
.btn-link { background: none; border: none; color: #38bdf8; cursor: pointer; font-weight: 600; }
.unavailable { font-size: 0.84rem; color: var(--color-text-muted); }
.unavailable p { margin: 0.25rem 0; }
.blast-summary { font-size: 0.84rem; margin: 0 0 0.3rem; }
.seed-line { font-size: 0.8rem; color: var(--color-text-muted); margin: 0 0 0.7rem; }
.warn { color: #fbbf24; }
.blast-graph { display: flex; flex-direction: column; gap: 0.25rem; margin-bottom: 0.9rem; }
.graph-layer { display: flex; align-items: center; gap: 0.45rem; flex-wrap: wrap; }
.graph-label { font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: var(--color-text-muted); min-width: 110px; }
.graph-node { display: inline-flex; flex-direction: column; font-size: 0.78rem; font-weight: 600; background: rgba(255,255,255,0.05); border: 1px solid rgba(255,255,255,0.12); border-radius: 8px; padding: 0.35rem 0.6rem; }
.graph-node.entry { border-color: rgba(244,63,94,0.45); color: #fda4af; }
.graph-node.module { border-color: rgba(251,191,36,0.45); color: #fcd34d; }
.graph-node.seed { border-color: rgba(244,63,94,0.65); background: rgba(244,63,94,0.12); color: #fda4af; }
.graph-node.muted { color: var(--color-text-muted); }
.graph-file { font-weight: 400; font-size: 0.7rem; color: var(--color-text-muted); }
.graph-arrow { font-size: 0.72rem; color: var(--color-text-muted); padding-left: 110px; }
.modules-list { list-style: none; margin: 0 0 0.7rem; padding: 0; display: flex; flex-direction: column; gap: 0.45rem; }
.module-card { background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.08); border-radius: 8px; padding: 0.55rem 0.7rem; }
.module-path { font-size: 0.8rem; background: rgba(255,255,255,0.06); padding: 0.1rem 0.35rem; border-radius: 4px; }
.callers-list { list-style: none; margin: 0.45rem 0 0; padding: 0; display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.8rem; }
.callers-list li { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
.caller-name { font-weight: 600; }
.caller-lines { color: var(--color-text-muted); font-size: 0.76rem; }
.verify-badge { font-size: 0.68rem; font-weight: 700; padding: 0.1rem 0.45rem; border-radius: 9999px; }
.verify-badge.confirmed { background: rgba(16,185,129,0.12); color: #6ee7b7; }
.verify-badge.import { background: rgba(251,191,36,0.12); color: #fcd34d; }
.empty-note { font-size: 0.84rem; color: var(--color-text-muted); }
.notes-list { margin: 0 0 0.7rem; padding-left: 1.1rem; font-size: 0.82rem; color: var(--color-text-muted); display: flex; flex-direction: column; gap: 0.3rem; }
.skipped { font-size: 0.8rem; color: var(--color-text-muted); }
.skipped summary { cursor: pointer; color: #7dd3fc; font-weight: 600; }
.skipped ul { margin: 0.4rem 0 0; padding-left: 1.1rem; }
.skipped code { font-size: 0.76rem; background: rgba(255,255,255,0.06); padding: 0.1rem 0.3rem; border-radius: 4px; }
@media (prefers-reduced-motion: reduce) { .spinner { animation: none; } }
</style>
