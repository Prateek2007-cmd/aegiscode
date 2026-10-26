<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="scan-detail-panel">
    <header class="detail-header">
      <div>
        <h3>{{ shortRepo(scan.repositoryUrl) }}</h3>
        <p class="detail-sub">
          <span class="status-dot" :style="{ background: scanStatusColor(scan.status) }"></span>
          {{ scanStatusHint(scan) }}
          <span v-if="scan.ref" class="ref-chip">{{ scan.ref }}</span>
        </p>
      </div>
      <div class="detail-actions">
        <button v-if="scan.status === 'Queued' || scan.status === 'Running'" class="btn-ghost" :disabled="!canManage || cancelling" @click="cancel">
          {{ cancelling ? 'Cancelling…' : 'Cancel' }}
        </button>
        <button class="btn-ghost" :disabled="downloadingSbom" @click="download('sbom')" title="Download CycloneDX SBOM">
          <i class="fi fi-rr-document"></i> SBOM
        </button>
        <button class="btn-ghost" :disabled="downloadingReport" @click="download('report')" title="Download JSON security report">
          <i class="fi fi-rr-download"></i> Report
        </button>
      </div>
    </header>

    <div v-if="actionError" class="banner banner-error" role="alert">{{ actionError }}</div>

    <!-- Mission control: live while running, the report once finished -->
    <MissionControl
      :scan="scan"
      :findings-so-far="totalFindings"
      :per-scanner-findings="perScannerFindings"
      :sbom-component-count="sbomComponentCount"
      :sbom-missing="sbomMissing"
      :snapshots="snapshots"
    />

    <!-- Repo scale: files, manifests, SBOM components, languages from manifests -->
    <ScaleStrip :scan="scan" :sbom-component-count="sbomComponentCount" :sbom-missing="sbomMissing" />

    <!-- Posture: severity dial, timeline across scans, hottest files -->
    <PostureView :scan="scan" :scans="scans" :findings="findings" :total-findings="totalFindings" />

    <!-- SBOM constellation -->
    <SbomConstellation :sbom="sbom" :sbom-missing="sbomMissing" :dep-scan-findings="depScanFindings" />

    <!-- Coverage: honest account of what ran -->
    <details v-if="scan.coverage" class="coverage">
      <summary>Scan coverage</summary>
      <dl>
        <div><dt>Files scanned</dt><dd>{{ scan.coverage.filesScanned }}</dd></div>
        <div><dt>Manifests</dt><dd>{{ scan.coverage.manifestsFound.join(', ') || 'none found' }}</dd></div>
        <div><dt>Scanners ran</dt><dd>{{ scan.coverage.scannersRun.join(', ') || '—' }}</dd></div>
        <div v-if="scan.coverage.scannersFailed.length"><dt class="warn">Failed</dt><dd class="warn">{{ scan.coverage.scannersFailed.join(', ') }}</dd></div>
        <div v-if="scan.coverage.scannersSkipped.length"><dt class="warn">Skipped</dt><dd class="warn">{{ scan.coverage.scannersSkipped.join(', ') }}</dd></div>
      </dl>
    </details>

    <!-- Filters -->
    <div class="filters" role="search" aria-label="Filter findings">
      <input v-model="search" type="search" placeholder="Search title, file, rule…" class="filter-input" />
      <select v-model="severityFilter" class="filter-select" aria-label="Severity">
        <option value="">All severities</option>
        <option v-for="s in SEVERITY_ORDER" :key="s" :value="s">{{ s }}</option>
      </select>
      <select v-model="scannerFilter" class="filter-select" aria-label="Scanner">
        <option value="">All scanners</option>
        <option v-for="s in SECURITY_SCANNERS" :key="s" :value="s">{{ s }}</option>
      </select>
      <select v-model="statusFilter" class="filter-select" aria-label="Status">
        <option value="">All statuses</option>
        <option>Open</option><option>Acknowledged</option><option>Suppressed</option><option>Resolved</option>
      </select>
    </div>

    <div v-if="loadingFindings" class="loading-state"><div class="spinner"></div><p>Loading findings…</p></div>
    <div v-else-if="findingsError" class="banner banner-error" role="alert">
      {{ findingsError }} <button class="btn-link" @click="loadFindings">Retry</button>
    </div>
    <div v-else-if="filteredFindings.length === 0" class="empty-state empty-inline">
      <p v-if="totalFindings === 0">No findings — the scanners reported nothing for this repository.</p>
      <p v-else>No findings match the current filters.</p>
    </div>

    <ul v-else class="findings-list">
      <li v-for="f in filteredFindings" :key="f.id" class="finding-card" :class="{ expanded: expandedId === f.id }">
        <button class="finding-head" @click="toggleExpand(f.id)" :aria-expanded="expandedId === f.id">
          <span class="sev-dot" :style="{ background: severityColor(f.severity) }"></span>
          <span class="finding-title">{{ f.title }}</span>
          <span class="finding-meta">{{ f.scanner }} · {{ f.severity }} · prio {{ f.priorityScore }}</span>
          <i class="fi" :class="expandedId === f.id ? 'fi-rr-angle-up' : 'fi-rr-angle-down'"></i>
        </button>
        <div v-if="expandedId === f.id" class="finding-body">
          <dl>
            <div v-if="f.ruleId">
              <dt>Rule</dt>
              <dd>
                <a v-if="osvUrl(f.ruleId)" :href="osvUrl(f.ruleId)!" target="_blank" rel="noopener" class="rule-link">
                  {{ f.ruleId }} <span aria-hidden="true">↗</span>
                </a>
                <code v-else>{{ f.ruleId }}</code>
              </dd>
            </div>
            <div v-if="f.filePath"><dt>Location</dt><dd><code>{{ f.filePath }}{{ f.line ? ':' + f.line : '' }}</code></dd></div>
            <div>
              <dt>Priority score</dt>
              <dd><span class="prio-num">{{ f.priorityScore }}</span> <span class="prio-note">priority — not a CVSS score</span></dd>
            </div>
            <div><dt>Confidence</dt><dd><span class="confidence-badge">{{ f.confidence }}</span></dd></div>
            <div><dt>Status</dt><dd>{{ f.status }}</dd></div>
            <div v-if="f.evidence"><dt>Evidence</dt><dd class="evidence">{{ f.evidence }}</dd></div>
            <div v-if="f.remediation">
              <dt>AI suggested fix</dt>
              <dd>
                <span class="confidence-badge">{{ f.confidence }}</span>
                <p class="remediation">{{ f.remediation }}</p>
              </dd>
            </div>
            <div v-if="f.scoreGaps.length"><dt>Score gaps</dt><dd class="gaps">{{ f.scoreGaps.join('; ') }}</dd></div>
            <div><dt>First seen</dt><dd>{{ formatTime(f.firstSeenAt) }}</dd></div>
          </dl>
          <div class="finding-actions" v-if="canManage && (f.status === 'Open' || f.status === 'Acknowledged')">
            <button class="btn-ghost btn-sm" @click="acknowledge(f)" :disabled="actingId === f.id">Acknowledge</button>
            <button class="btn-ghost btn-sm" @click="suppressPrompt(f)" :disabled="actingId === f.id">Suppress…</button>
            <FixProposalButton
              :client-id="clientId"
              source="scan"
              :finding-ref="`scan:${f.id}`"
              :title="f.title"
              :file-path="f.filePath"
              :line="f.line"
              :remediation="f.remediation"
              :confidence="f.confidence"
            />
          </div>
          <div v-if="suppressingId === f.id" class="suppress-box">
            <input v-model="suppressReason" placeholder="Reason (required, audited)…" class="filter-input" />
            <div class="suppress-actions">
              <button class="btn-ghost btn-sm" @click="suppressingId = null">Cancel</button>
              <button class="btn-primary btn-sm" :disabled="!suppressReason.trim() || actingId === f.id" @click="suppress(f)">Suppress</button>
            </div>
          </div>
        </div>
      </li>
    </ul>

    <div v-if="totalPages > 1" class="pagination">
      <button class="btn-ghost btn-sm" :disabled="page === 1" @click="page--">‹ Prev</button>
      <span>Page {{ page }} of {{ totalPages }} ({{ totalFindings }} findings)</span>
      <button class="btn-ghost btn-sm" :disabled="page === totalPages" @click="page++">Next ›</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import FixProposalButton from '@/features/reviews/components/FixProposalButton.vue'
import {
  acknowledgeFinding,
  cancelSecurityScan,
  downloadScanArtifact,
  getScanFindings,
  getScanSbomJson,
  suppressFinding,
} from '../securityScansService'
import type { CycloneDxBom, PollSnapshot, SecurityFinding, SecurityScan } from '../types'
import { SECURITY_SCANNERS, SEVERITY_ORDER, osvUrl, scanStatusColor, scanStatusHint, severityColor } from '../types'
import MissionControl from './MissionControl.vue'
import ScaleStrip from './ScaleStrip.vue'
import PostureView from './PostureView.vue'
import SbomConstellation from './SbomConstellation.vue'

const props = withDefaults(
  defineProps<{ clientId: string; scan: SecurityScan; canManage: boolean; scans?: SecurityScan[] }>(),
  { scans: () => [] },
)
const emit = defineEmits<{ refresh: []; cancelled: [] }>()

const findings = ref<SecurityFinding[]>([])
const totalFindings = ref(0)
const loadingFindings = ref(false)
const findingsError = ref<string | null>(null)
const page = ref(1)
const pageSize = 25
const totalPages = computed(() => Math.max(1, Math.ceil(totalFindings.value / pageSize)))

const search = ref('')
const severityFilter = ref('')
const scannerFilter = ref('')
const statusFilter = ref('')
const expandedId = ref<string | null>(null)
const suppressingId = ref<string | null>(null)
const suppressReason = ref('')
const actingId = ref<string | null>(null)
const cancelling = ref(false)
const downloadingSbom = ref(false)
const downloadingReport = ref(false)
const actionError = ref<string | null>(null)

// Dashboard data: per-scanner finding counts, SBOM, dep-scan findings for the constellation.
const perScannerFindings = ref<Record<string, number>>({})
const sbom = ref<CycloneDxBom | null>(null)
const sbomMissing = ref(false)
const sbomComponentCount = computed(() => sbom.value?.components?.length ?? null)
const depScanFindings = ref<SecurityFinding[]>([])

// Poll log: snapshots recorded in this browser session (not engine logs).
const snapshots = ref<PollSnapshot[]>([])
function recordSnapshot(): void {
  const last = snapshots.value[snapshots.value.length - 1]
  if (last && last.status === props.scan.status && last.progressPct === props.scan.progressPct) return
  snapshots.value.push({
    at: new Date().toISOString(),
    status: props.scan.status,
    progressPct: props.scan.progressPct,
  })
  if (snapshots.value.length > 50) snapshots.value.splice(0, snapshots.value.length - 50)
}

const filteredFindings = computed(() => {
  const q = search.value.trim().toLowerCase()
  return findings.value.filter((f) => {
    if (severityFilter.value && f.severity !== severityFilter.value) return false
    if (scannerFilter.value && f.scanner !== scannerFilter.value) return false
    if (statusFilter.value && f.status !== statusFilter.value) return false
    if (q) {
      const hay = `${f.title} ${f.filePath ?? ''} ${f.ruleId ?? ''}`.toLowerCase()
      if (!hay.includes(q)) return false
    }
    return true
  })
})

function shortRepo(url: string): string {
  return url.replace(/^https?:\/\//, '')
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

async function loadFindings(): Promise<void> {
  loadingFindings.value = true
  findingsError.value = null
  try {
    const res = await getScanFindings(props.clientId, props.scan.scanId, {
      severity: (severityFilter.value || undefined) as SecurityFinding['severity'] | undefined,
      scanner: scannerFilter.value || undefined,
      status: (statusFilter.value || undefined) as SecurityFinding['status'] | undefined,
      limit: pageSize,
      offset: (page.value - 1) * pageSize,
    })
    findings.value = res.items
    totalFindings.value = res.totalCount
  } catch (e) {
    findingsError.value = e instanceof Error ? e.message : 'Failed to load findings.'
  } finally {
    loadingFindings.value = false
  }
  void loadScannerCounts()
}

/** Per-scanner finding totals via the findings API (limit 1, read totalCount). */
async function loadScannerCounts(): Promise<void> {
  try {
    const counts = await Promise.all(
      SECURITY_SCANNERS.map(async (scanner) => {
        const res = await getScanFindings(props.clientId, props.scan.scanId, { scanner, limit: 1 })
        return [scanner, res.totalCount] as const
      }),
    )
    perScannerFindings.value = Object.fromEntries(counts)
  } catch {
    /* best-effort: scanner cards fall back to "findings loading…" */
  }
}

async function loadSbom(): Promise<void> {
  try {
    const bom = await getScanSbomJson(props.clientId, props.scan.scanId)
    sbom.value = bom
    sbomMissing.value = bom === null
  } catch {
    // Leave the previous SBOM state; the download button surfaces real errors.
    sbomMissing.value = false
  }
}

/** Dep-scan findings used only to match SBOM components to vulnerabilities. */
async function loadDepScanFindings(): Promise<void> {
  try {
    const res = await getScanFindings(props.clientId, props.scan.scanId, {
      scanner: 'dep-scan',
      limit: 500,
    })
    depScanFindings.value = res.items
  } catch {
    depScanFindings.value = []
  }
}

function resetScanState(): void {
  expandedId.value = null
  perScannerFindings.value = {}
  sbom.value = null
  sbomMissing.value = false
  depScanFindings.value = []
  snapshots.value = []
  recordSnapshot()
}

function toggleExpand(id: string): void {
  expandedId.value = expandedId.value === id ? null : id
}

async function cancel(): Promise<void> {
  cancelling.value = true
  actionError.value = null
  try {
    await cancelSecurityScan(props.clientId, props.scan.scanId)
    emit('cancelled')
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : 'Failed to cancel scan.'
  } finally {
    cancelling.value = false
  }
}

async function download(kind: 'sbom' | 'report'): Promise<void> {
  if (kind === 'sbom') downloadingSbom.value = true
  else downloadingReport.value = true
  actionError.value = null
  try {
    await downloadScanArtifact(props.clientId, props.scan.scanId, kind)
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : `Failed to download ${kind}.`
  } finally {
    downloadingSbom.value = false
    downloadingReport.value = false
  }
}

async function acknowledge(f: SecurityFinding): Promise<void> {
  actingId.value = f.id
  actionError.value = null
  try {
    await acknowledgeFinding(props.clientId, props.scan.scanId, f.id)
    f.status = 'Acknowledged'
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : 'Failed to acknowledge.'
  } finally {
    actingId.value = null
  }
}

function suppressPrompt(f: SecurityFinding): void {
  suppressingId.value = f.id
  suppressReason.value = ''
}

async function suppress(f: SecurityFinding): Promise<void> {
  actingId.value = f.id
  actionError.value = null
  try {
    await suppressFinding(props.clientId, props.scan.scanId, f.id, suppressReason.value.trim())
    f.status = 'Suppressed'
    suppressingId.value = null
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : 'Failed to suppress.'
  } finally {
    actingId.value = null
  }
}

watch([() => props.scan.scanId, page], () => {
  expandedId.value = null
  void loadFindings()
})

// New scan selected: reset dashboard state and load its SBOM + dep-scan findings.
watch(
  () => props.scan.scanId,
  () => {
    resetScanState()
    void loadSbom()
    void loadDepScanFindings()
  },
)

// Poll updates arrive as a fresh scan object: record the snapshot, and re-fetch
// the SBOM / dep-scan findings once the scan reaches a terminal state.
watch(
  () => [props.scan.status, props.scan.progressPct] as const,
  () => {
    recordSnapshot()
    if (
      props.scan.status === 'Succeeded' ||
      props.scan.status === 'PartiallySucceeded' ||
      props.scan.status === 'Failed'
    ) {
      void loadSbom()
      void loadDepScanFindings()
    }
  },
)

onMounted(() => {
  recordSnapshot()
  void loadFindings()
  void loadSbom()
  void loadDepScanFindings()
})
</script>

<style scoped>
.scan-detail-panel { background: rgba(255,255,255,0.02); border: 1px solid rgba(255,255,255,0.08); border-radius: 12px; padding: 1.1rem 1.25rem; }
.detail-header { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; margin-bottom: 0.8rem; flex-wrap: wrap; }
.detail-header h3 { margin: 0 0 0.25rem; font-size: 1.05rem; }
.detail-sub { margin: 0; font-size: 0.82rem; color: var(--color-text-muted); display: flex; align-items: center; gap: 0.45rem; }
.status-dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }
.ref-chip { font-size: 0.72rem; background: rgba(56,189,248,0.12); color: #7dd3fc; padding: 0.1rem 0.5rem; border-radius: 9999px; }
.detail-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
.btn-ghost { background: transparent; border: 1px solid rgba(255,255,255,0.16); color: var(--color-text); border-radius: 8px; padding: 0.45rem 0.85rem; cursor: pointer; font-size: 0.84rem; display: inline-flex; align-items: center; gap: 0.4rem; }
.btn-ghost:hover:not(:disabled) { border-color: rgba(56,189,248,0.4); }
.btn-ghost:disabled { opacity: 0.45; cursor: not-allowed; }
.btn-sm { padding: 0.3rem 0.65rem; font-size: 0.78rem; }
.btn-link { background: none; border: none; color: #38bdf8; cursor: pointer; font-weight: 600; }
.banner-error { background: rgba(244,63,94,0.1); border: 1px solid rgba(244,63,94,0.3); color: #fda4af; padding: 0.6rem 0.8rem; border-radius: 8px; font-size: 0.84rem; margin-bottom: 0.8rem; }
.coverage { margin-bottom: 0.9rem; font-size: 0.84rem; }
.coverage summary { cursor: pointer; color: #7dd3fc; font-weight: 600; }
.coverage dl { margin: 0.5rem 0 0; display: grid; gap: 0.3rem; }
.coverage dl > div { display: flex; gap: 0.6rem; }
.coverage dt { color: var(--color-text-muted); min-width: 110px; }
.coverage dd { margin: 0; }
.warn { color: #fbbf24; }
.filters { display: flex; gap: 0.5rem; flex-wrap: wrap; margin-bottom: 0.9rem; }
.filter-input, .filter-select { padding: 0.45rem 0.65rem; border-radius: 8px; border: 1px solid rgba(255,255,255,0.14); background: rgba(255,255,255,0.04); color: var(--color-text); font-size: 0.84rem; }
.filter-input { flex: 1; min-width: 160px; }
.loading-state { text-align: center; padding: 2rem; color: var(--color-text-muted); }
.spinner { width: 28px; height: 28px; margin: 0 auto 0.8rem; border: 3px solid rgba(56,189,248,0.2); border-top-color: #38bdf8; border-radius: 50%; animation: spin 0.8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
.empty-inline { text-align: center; padding: 1.5rem; color: var(--color-text-muted); }
.findings-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.55rem; }
.finding-card { background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.08); border-radius: 10px; overflow: hidden; }
.finding-card.expanded { border-color: rgba(56,189,248,0.35); }
.finding-head { display: flex; align-items: center; gap: 0.65rem; width: 100%; background: transparent; border: none; color: var(--color-text); padding: 0.7rem 0.9rem; cursor: pointer; text-align: left; }
.sev-dot { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
.finding-title { font-weight: 600; font-size: 0.88rem; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.finding-meta { font-size: 0.75rem; color: var(--color-text-muted); white-space: nowrap; }
.finding-body { padding: 0 0.9rem 0.9rem; border-top: 1px solid rgba(255,255,255,0.06); }
.finding-body dl { margin: 0.7rem 0; display: grid; gap: 0.45rem; font-size: 0.84rem; }
.finding-body dl > div { display: grid; grid-template-columns: 110px 1fr; gap: 0.6rem; }
.finding-body dt { color: var(--color-text-muted); }
.finding-body dd { margin: 0; word-break: break-word; }
.finding-body code { background: rgba(255,255,255,0.06); padding: 0.1rem 0.35rem; border-radius: 4px; font-size: 0.8rem; }
.rule-link { color: #7dd3fc; font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace; font-size: 0.8rem; }
.rule-link:hover { color: #38bdf8; }
.prio-num {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-weight: 700; color: #fbbf24;
}
.prio-note { font-size: 0.74rem; color: var(--color-text-muted); margin-left: 0.3rem; }
.confidence-badge {
  display: inline-block; font-size: 0.72rem; font-weight: 700;
  background: rgba(56,189,248,0.12); color: #7dd3fc;
  padding: 0.1rem 0.5rem; border-radius: 9999px;
}
.evidence, .remediation { white-space: pre-wrap; font-size: 0.82rem; }
.remediation { margin: 0.4rem 0 0; }
.gaps { color: #fbbf24; font-size: 0.8rem; }
.finding-actions { display: flex; gap: 0.5rem; margin-top: 0.6rem; }
.suppress-box { margin-top: 0.6rem; display: flex; flex-direction: column; gap: 0.5rem; }
.suppress-actions { display: flex; gap: 0.5rem; justify-content: flex-end; }
.pagination { display: flex; align-items: center; justify-content: center; gap: 0.8rem; margin-top: 1rem; font-size: 0.84rem; color: var(--color-text-muted); }
@media (prefers-reduced-motion: reduce) { .spinner { animation: none; } }
</style>
