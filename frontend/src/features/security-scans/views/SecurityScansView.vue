<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <PageWithSidebar>
    <template #sidebar>
      <AppNavDrawer>
        <div class="sidebar-nav">
          <div class="sidebar-nav-group">
            <h4>Security Scans</h4>
            <button class="sidebar-nav-link active">
              <i class="fi fi-rr-shield-check"></i> Scan History
            </button>
          </div>
        </div>
      </AppNavDrawer>
    </template>

    <AppTopBar class="view-header">
      <div>
        <h2 class="view-title">Security Scans</h2>
        <p class="view-subtitle">
          Deterministic scans: dependency vulnerabilities (OSV.dev), secret detection,
          and SBOM generation. Every result below comes from a real scan run.
        </p>
      </div>
      <button class="btn-primary" :disabled="!canStartScan" @click="showStartDialog = true" title="Start a new security scan">
        <i class="fi fi-rr-play"></i> New Scan
      </button>
    </AppTopBar>

    <div v-if="clientError" class="banner banner-error" role="alert">{{ clientError }}</div>

    <div v-if="!clientId" class="empty-state">
      <i class="fi fi-rr-shield-check empty-icon"></i>
      <h3>No client selected</h3>
      <p>Select a client to view and run security scans.</p>
      <select v-model="selectedClientId" class="client-select" aria-label="Select client">
        <option v-for="id in availableClientIds" :key="id" :value="id">{{ clientLabel(id) }}</option>
      </select>
    </div>

    <template v-else>
      <section v-if="loadingScans" class="loading-state" aria-busy="true">
        <div class="spinner"></div><p>Loading scans…</p>
      </section>

      <section v-else-if="scansError" class="banner banner-error" role="alert">
        {{ scansError }}
        <button class="btn-link" @click="loadScans">Retry</button>
      </section>

      <section v-else-if="scans.length === 0" class="empty-state">
        <i class="fi fi-rr-shield-check empty-icon"></i>
        <h3>No scans yet</h3>
        <p>Start your first deterministic security scan to detect vulnerable dependencies,
          leaked secrets, and generate an SBOM.</p>
        <button class="btn-primary" :disabled="!canStartScan" @click="showStartDialog = true">
          <i class="fi fi-rr-play"></i> Start a scan
        </button>
      </section>

      <section v-else class="scans-layout">
        <div class="scan-list" role="list" aria-label="Scan history">
          <button
            v-for="scan in scans"
            :key="scan.scanId"
            role="listitem"
            class="scan-card"
            :class="{ 'scan-card-selected': scan.scanId === selectedScanId }"
            @click="selectScan(scan.scanId)"
          >
            <div class="scan-card-top">
              <span class="scan-status-dot" :style="{ background: scanStatusColor(scan.status) }"></span>
              <span class="scan-repo">{{ shortRepo(scan.repositoryUrl) }}</span>
              <span class="scan-time">{{ formatTime(scan.queuedAt) }}</span>
            </div>
            <div class="scan-card-bottom">
              <span class="scan-status-text">{{ scanStatusHint(scan) }}</span>
              <span class="scan-counts">
                <span v-if="scan.summary.critical" class="count critical">{{ scan.summary.critical }}C</span>
                <span v-if="scan.summary.high" class="count high">{{ scan.summary.high }}H</span>
                <span v-if="scan.summary.medium" class="count medium">{{ scan.summary.medium }}M</span>
                <span v-if="scan.summary.low" class="count low">{{ scan.summary.low }}L</span>
              </span>
            </div>
            <div v-if="scan.status === 'Failed' && scan.error" class="scan-error">{{ scan.error }}</div>
          </button>
        </div>

        <div class="scan-detail">
          <div v-if="!selectedScan" class="empty-state empty-inline">
            <p>Select a scan to see its findings.</p>
          </div>
          <ScanDetailPanel
            v-else
            :client-id="clientId"
            :scan="selectedScan"
            :scans="scans"
            :can-manage="canStartScan"
            @refresh="loadScans"
            @cancelled="onScanCancelled"
          />
        </div>
      </section>
    </template>

    <StartScanDialog
      v-if="showStartDialog && clientId"
      :client-id="clientId"
      @close="showStartDialog = false"
      @started="onScanStarted"
    />
  </PageWithSidebar>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { AppNavDrawer, AppTopBar, PageWithSidebar } from '@/components'
import { useSession } from '@/composables/useSession'
import {
  getSecurityScan,
  listSecurityScans,
} from '../securityScansService'
import type { SecurityScan } from '../types'
import { scanStatusColor, scanStatusHint } from '../types'
import StartScanDialog from '../components/StartScanDialog.vue'
import ScanDetailPanel from '../components/ScanDetailPanel.vue'

const props = withDefaults(defineProps<{ clientId?: string }>(), { clientId: undefined })

const { clientRoles } = useSession()
const availableClientIds = computed(() => Object.keys(clientRoles.value ?? {}))
const selectedClientId = ref<string | null>(props.clientId ?? null)
const clientId = computed(() => props.clientId ?? selectedClientId.value)

const scans = ref<SecurityScan[]>([])
const selectedScanId = ref<string | null>(null)
const loadingScans = ref(false)
const scansError = ref<string | null>(null)
const clientError = ref<string | null>(null)
const showStartDialog = ref(false)
// Starting a scan is a privileged, resource-expensive action. The server enforces
// client-administrator; the button mirrors that so it never promises what it can't do.
const canStartScan = computed(() => {
  const id = clientId.value
  if (!id) return false
  const role = (clientRoles.value ?? {})[id]
  return role !== undefined && role >= 1
})

const selectedScan = computed(() => scans.value.find((s) => s.scanId === selectedScanId.value) ?? null)

let pollTimer: ReturnType<typeof setInterval> | null = null

function clientLabel(id: string): string {
  return `Client ${id.slice(0, 8)}…`
}

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

async function loadScans(): Promise<void> {
  const id = clientId.value
  if (!id) return
  loadingScans.value = true
  scansError.value = null
  try {
    scans.value = await listSecurityScans(id, 50, 0)
    if (scans.value.length > 0 && !selectedScanId.value) {
      selectedScanId.value = scans.value[0].scanId
    }
  } catch (e) {
    scansError.value = e instanceof Error ? e.message : 'Failed to load scans.'
  } finally {
    loadingScans.value = false
  }
}

function selectScan(scanId: string): void {
  selectedScanId.value = scanId
}

function onScanStarted(scanId: string): void {
  showStartDialog.value = false
  selectedScanId.value = scanId
  void loadScans()
}

function onScanCancelled(): void {
  void loadScans()
}

function startPolling(): void {
  stopPolling()
  pollTimer = setInterval(async () => {
    const id = clientId.value
    const active = scans.value.some((s) => s.status === 'Queued' || s.status === 'Running')
    if (!id || !active) return
    try {
      const fresh = await listSecurityScans(id, 50, 0)
      scans.value = fresh
    } catch {
      /* polling is best-effort; the manual refresh surfaces errors */
    }
  }, 5000)
}

function stopPolling(): void {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

watch(clientId, () => {
  selectedScanId.value = null
  void loadScans()
})

onMounted(() => {
  if (availableClientIds.value.length === 1 && !selectedClientId.value) {
    selectedClientId.value = availableClientIds.value[0]
  }
  void loadScans()
  startPolling()
})

onUnmounted(() => stopPolling())

// Keep the selected scan's status fresh without a full reload.
watch(selectedScanId, async (id) => {
  if (!id || !clientId.value) return
  try {
    const fresh = await getSecurityScan(clientId.value, id)
    const idx = scans.value.findIndex((s) => s.scanId === id)
    if (idx >= 0) scans.value[idx] = fresh
  } catch {
    /* best-effort */
  }
})
</script>

<style scoped>
.view-header { display: flex; align-items: center; justify-content: space-between; gap: 1rem; margin-bottom: 1.5rem; }
.view-title { margin: 0; font-size: 1.6rem; font-weight: 700; color: var(--color-text); }
.view-subtitle { margin: 0.35rem 0 0; font-size: 0.88rem; color: var(--color-text-muted); max-width: 75ch; }
.btn-primary { display: inline-flex; align-items: center; gap: 0.5rem; }
.btn-link { background: none; border: none; color: #38bdf8; cursor: pointer; font-weight: 600; }
.banner { padding: 0.75rem 1rem; border-radius: 10px; margin-bottom: 1rem; font-size: 0.88rem; }
.banner-error { background: rgba(244, 63, 94, 0.1); border: 1px solid rgba(244, 63, 94, 0.3); color: #fda4af; }
.empty-state { text-align: center; padding: 3rem 1.5rem; color: var(--color-text-muted); }
.empty-icon { font-size: 2.5rem; color: #38bdf8; opacity: 0.7; }
.empty-inline { padding: 2rem 1rem; }
.client-select { margin-top: 1rem; padding: 0.5rem 0.75rem; border-radius: 8px; background: var(--color-surface); color: var(--color-text); border: 1px solid rgba(255,255,255,0.12); }
.loading-state { text-align: center; padding: 3rem; color: var(--color-text-muted); }
.spinner { width: 32px; height: 32px; margin: 0 auto 1rem; border: 3px solid rgba(56,189,248,0.2); border-top-color: #38bdf8; border-radius: 50%; animation: spin 0.8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
.scans-layout { display: grid; grid-template-columns: 340px 1fr; gap: 1.25rem; align-items: start; }
@media (max-width: 900px) { .scans-layout { grid-template-columns: 1fr; } }
.scan-list { display: flex; flex-direction: column; gap: 0.6rem; max-height: 70vh; overflow-y: auto; }
.scan-card { text-align: left; background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.08); border-radius: 10px; padding: 0.75rem 0.9rem; cursor: pointer; color: var(--color-text); }
.scan-card:hover { border-color: rgba(56,189,248,0.35); }
.scan-card-selected { border-color: #38bdf8; background: rgba(56,189,248,0.07); }
.scan-card-top { display: flex; align-items: center; gap: 0.5rem; }
.scan-status-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
.scan-repo { font-weight: 600; font-size: 0.85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1; }
.scan-time { font-size: 0.72rem; color: var(--color-text-muted); white-space: nowrap; }
.scan-card-bottom { display: flex; align-items: center; justify-content: space-between; margin-top: 0.35rem; }
.scan-status-text { font-size: 0.78rem; color: var(--color-text-muted); }
.scan-counts { display: flex; gap: 0.3rem; }
.count { font-size: 0.7rem; font-weight: 700; padding: 0.1rem 0.4rem; border-radius: 9999px; }
.count.critical { background: rgba(244,63,94,0.15); color: #fb7185; }
.count.high { background: rgba(251,113,133,0.12); color: #fda4af; }
.count.medium { background: rgba(245,158,11,0.15); color: #fbbf24; }
.count.low { background: rgba(56,189,248,0.12); color: #7dd3fc; }
.scan-error { margin-top: 0.4rem; font-size: 0.75rem; color: #fda4af; }
.scan-detail { min-width: 0; }
.sidebar-nav-group h4 { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.06em; color: var(--color-text-muted); margin: 0 0 0.5rem; }
.sidebar-nav-link { display: flex; align-items: center; gap: 0.5rem; width: 100%; padding: 0.5rem 0.75rem; border: none; background: transparent; color: var(--color-text); border-radius: 8px; cursor: pointer; font-size: 0.88rem; }
.sidebar-nav-link.active { background: rgba(56,189,248,0.12); color: #7dd3fc; }
@media (prefers-reduced-motion: reduce) { .spinner { animation: none; } }
</style>
