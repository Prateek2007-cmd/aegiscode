<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<!--
  Compact scan list for the per-client dashboard. One row per scan: repo, status,
  date, severity counts, and progress while a scan is active. Each row links to
  the Security Scans page (which keeps the selected scan in local state); this
  section never embeds the per-scan Mission Control — it is too heavy for a list.
-->
<template>
  <div class="dashboard-scans">
    <p v-if="loading" class="loading">Loading scans…</p>
    <p v-else-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="scans.length === 0" class="empty-state">No scans yet for this client</p>
    <ul v-else class="scan-rows">
      <li v-for="scan in scans" :key="scan.scanId" class="scan-row-wrap">
        <RouterLink :to="{ name: 'security-scans' }" class="scan-row" :title="scanStatusHint(scan)">
          <span class="scan-repo">{{ repoShortName(scan.repositoryUrl) }}</span>
          <span class="scan-status" :style="{ color: scanStatusColor(scan.status) }">{{ scan.status }}</span>
          <span class="scan-date">{{ formatDashboardDate(scan.finishedAt ?? scan.startedAt ?? scan.queuedAt) }}</span>
          <span class="scan-counts" aria-label="Findings by severity">
            <span
              v-for="chip in scanSummaryChips(scan.summary)"
              :key="chip.severity"
              class="sev-chip"
              :style="{ color: severityColor(chip.severity) }"
              :title="`${chip.severity}: ${chip.count}`"
            >{{ chip.label }}{{ chip.count }}</span>
          </span>
          <span v-if="isScanActive(scan.status)" class="scan-progress">{{ scan.progressPct }}%</span>
        </RouterLink>
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import type { SecurityScan } from '@/features/security-scans/types'
import { scanStatusColor, scanStatusHint, severityColor } from '@/features/security-scans/types'
import {
  formatDashboardDate,
  isScanActive,
  repoShortName,
  scanSummaryChips,
} from './clientDashboardFormatters'

defineProps<{
  scans: SecurityScan[]
  loading: boolean
  error: string
}>()
</script>

<style scoped>
.dashboard-scans {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.1rem 1.4rem;
  color: #e2e8f0;
}

.loading,
.empty-state {
  color: #94a3b8;
  font-style: italic;
  margin: 0;
}

.error {
  color: #f43f5e;
  margin: 0;
}

.scan-rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.scan-row-wrap {
  border-bottom: 1px solid rgba(148, 163, 184, 0.14);
}

.scan-row-wrap:last-child {
  border-bottom: none;
}

.scan-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.75rem 1rem;
  padding: 0.7rem 0.25rem;
  color: #e2e8f0;
  text-decoration: none;
  transition: background 0.15s ease;
}

.scan-row:hover {
  background: rgba(56, 189, 248, 0.06);
}

.scan-repo {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-weight: 600;
  font-size: 0.9rem;
  color: #7dd3fc;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1 1 12rem;
}

.scan-status {
  font-size: 0.8rem;
  font-weight: 700;
}

.scan-date {
  font-size: 0.82rem;
  color: #94a3b8;
  white-space: nowrap;
}

.scan-counts {
  display: inline-flex;
  gap: 0.5rem;
  margin-left: auto;
}

.sev-chip {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 0.8rem;
  font-weight: 700;
}

.scan-progress {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 0.85rem;
  font-weight: 700;
  color: #fbbf24;
}
</style>
