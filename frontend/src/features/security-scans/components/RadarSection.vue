<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Dependency Radar section on the client dashboard. Shows the open alerts the
  radar recorded when a new Critical/High CVE appeared for a package this
  client actually uses, plus the watch settings and a manual "check now"
  trigger. Every line is backed by OSV data from the server — a pass that
  finds nothing new says so, and the section never invents a CVE.
-->
<template>
  <section class="radar-section" aria-label="Dependency Radar">
    <div class="radar-section-header">
      <div>
        <h3 class="radar-section-title">Dependency Radar</h3>
        <p class="radar-section-subtitle">
          Watches the packages your last scans recorded and alerts the day a new CVE drops for one
          of them.
        </p>
      </div>
      <button
        class="btn-secondary"
        type="button"
        :disabled="checking || unavailable"
        @click="handleCheckNow"
      >
        {{ checking ? 'Checking…' : 'Check now' }}
      </button>
    </div>

    <p v-if="lastPassAt" class="radar-section-meta">
      Last checked
      <time :datetime="lastPassAt">{{ formatLastChecked(lastPassAt) }}</time>
      <template v-if="settings && !settings.enabled"> — radar is paused for this client.</template>
    </p>

    <p v-if="unavailable" class="radar-section-note">
      Dependency Radar needs the API update — not available yet.
    </p>

    <p v-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <div v-if="passSummary" class="radar-pass-summary" :class="passSummaryTone" aria-live="polite">
      <p class="radar-pass-summary-title">{{ passSummaryTitle }}</p>
      <p class="radar-pass-summary-detail">{{ passSummary }}</p>
      <p v-if="passErrors.length" class="radar-pass-summary-errors">
        <span v-for="repoError in passErrors" :key="repoError.repositoryUrl">
          {{ repoError.repositoryUrl }}: {{ repoError.error }}
        </span>
      </p>
    </div>

    <div v-if="loading" class="radar-section-loading">Loading radar alerts…</div>

    <template v-else-if="!unavailable && !loadError">
      <p v-if="alerts.length === 0" class="radar-section-empty">
        No new CVEs since the last scan. The radar will alert here the moment one appears.
      </p>

      <ul v-else class="radar-alert-list">
        <li v-for="alert in alerts" :key="alert.id" class="radar-alert">
          <span
            class="radar-alert-severity"
            :style="{ backgroundColor: severityColor(alert.severity as SecurityFindingSeverity) }"
          >
            {{ alert.severity }}
          </span>
          <div class="radar-alert-body">
            <p class="radar-alert-title">
              <strong>{{ alert.vulnId }}</strong>
              <span v-if="alert.cveId" class="radar-alert-cve">({{ alert.cveId }})</span>
              — {{ alert.packageName }}@{{ alert.packageVersion }}
              <span class="radar-alert-ecosystem">{{ alert.ecosystem }}</span>
            </p>
            <p v-if="alert.summary" class="radar-alert-summary">{{ alert.summary }}</p>
            <p class="radar-alert-meta">
              First seen {{ formatLastChecked(alert.firstSeenAt) }}
              <template v-if="alert.fixedVersion"> · fixed in {{ alert.fixedVersion }}</template>
              · from
              <a class="radar-alert-link" :href="alert.osvUrl" target="_blank" rel="noopener noreferrer">
                OSV
              </a>
              <span class="radar-alert-status" :class="`radar-alert-status-${alert.status.toLowerCase()}`">
                {{ alert.status }}
              </span>
            </p>
          </div>
          <button
            v-if="alert.status === 'New'"
            class="btn-ghost radar-alert-acknowledge"
            type="button"
            :disabled="acknowledgingId === alert.id"
            @click="handleAcknowledge(alert)"
          >
            {{ acknowledgingId === alert.id ? 'Acknowledging…' : 'Acknowledge' }}
          </button>
        </li>
      </ul>

      <div v-if="settings" class="radar-settings">
        <label class="radar-settings-toggle">
          <input
            type="checkbox"
            :checked="settings.enabled"
            :disabled="savingSettings"
            @change="handleEnabledChange"
          />
          Watch this client's packages
        </label>
        <label class="radar-settings-interval">
          Check every
          <select :value="settings.intervalMinutes" :disabled="savingSettings" @change="handleIntervalChange">
            <option :value="60">1 hour</option>
            <option :value="180">3 hours</option>
            <option :value="360">6 hours</option>
            <option :value="720">12 hours</option>
            <option :value="1440">24 hours</option>
          </select>
        </label>
        <p v-if="settingsError" class="error" role="alert">{{ settingsError }}</p>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  acknowledgeRadarAlert,
  checkRadarNow,
  getRadarSettings,
  isRadarUnavailable,
  listRadarAlerts,
  radarPassSummary,
  updateRadarSettings,
} from '@/services/radarService'
import type { RadarAlert, RadarPassResult, RadarSettings } from '@/services/radarService'
import type { SecurityFindingSeverity } from '../types'
import { severityColor } from '../types'

const props = defineProps<{
  clientId: string
}>()

const alerts = ref<RadarAlert[]>([])
const settings = ref<RadarSettings | null>(null)
const loading = ref(true)
const unavailable = ref(false)
const loadError = ref<string | null>(null)

const checking = ref(false)
const passSummary = ref<string | null>(null)
const passResult = ref<RadarPassResult | null>(null)

const savingSettings = ref(false)
const settingsError = ref<string | null>(null)
const acknowledgingId = ref<string | null>(null)

const lastPassAt = computed(() => settings.value?.lastPassAt ?? null)
const passSummaryTone = computed(() =>
  passResult.value?.nothingNew ? 'radar-pass-summary-clean' : 'radar-pass-summary-alert',
)
const passSummaryTitle = computed(() =>
  passResult.value?.nothingNew ? 'Nothing new' : 'New vulnerabilities found',
)
const passErrors = computed(() => (passResult.value?.repos ?? []).filter((r) => r.error))

function formatLastChecked(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}

async function loadAlerts(): Promise<void> {
  alerts.value = await listRadarAlerts(props.clientId)
}

async function loadSettings(): Promise<void> {
  settings.value = await getRadarSettings(props.clientId)
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  unavailable.value = false
  try {
    await Promise.all([loadAlerts(), loadSettings()])
  } catch (error) {
    if (isRadarUnavailable(error)) {
      unavailable.value = true
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to load the radar.'
    }
  } finally {
    loading.value = false
  }
}

async function handleCheckNow(): Promise<void> {
  checking.value = true
  passSummary.value = null
  passResult.value = null
  loadError.value = null
  try {
    const result = await checkRadarNow(props.clientId)
    passResult.value = result
    passSummary.value = radarPassSummary(result)
    await Promise.all([loadAlerts(), loadSettings()])
  } catch (error) {
    if (isRadarUnavailable(error)) {
      unavailable.value = true
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to run the radar check.'
    }
  } finally {
    checking.value = false
  }
}

async function handleAcknowledge(alert: RadarAlert): Promise<void> {
  acknowledgingId.value = alert.id
  try {
    const updated = await acknowledgeRadarAlert(props.clientId, alert.id)
    alerts.value = alerts.value.map((a) => (a.id === alert.id ? updated : a))
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : 'Failed to acknowledge the alert.'
  } finally {
    acknowledgingId.value = null
  }
}

async function handleEnabledChange(event: Event): Promise<void> {
  const enabled = (event.target as HTMLInputElement).checked
  await saveSettings({ enabled })
}

async function handleIntervalChange(event: Event): Promise<void> {
  const intervalMinutes = Number((event.target as HTMLSelectElement).value)
  await saveSettings({ intervalMinutes })
}

async function saveSettings(request: { enabled?: boolean; intervalMinutes?: number }): Promise<void> {
  savingSettings.value = true
  settingsError.value = null
  try {
    settings.value = await updateRadarSettings(props.clientId, request)
  } catch (error) {
    settingsError.value =
      error instanceof Error ? error.message : 'Failed to update the radar settings.'
  } finally {
    savingSettings.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.radar-section {
  background: var(--color-surface);
  border-radius: 12px;
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.radar-section-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.radar-section-title {
  font-size: 1.1rem;
  margin: 0 0 4px;
}

.radar-section-subtitle {
  color: var(--color-text-muted);
  margin: 0;
}

.radar-section-meta,
.radar-section-note,
.radar-section-loading,
.radar-section-empty {
  color: var(--color-text-muted);
  margin: 0;
}

.radar-pass-summary {
  border-radius: 8px;
  padding: 12px 16px;
  border: 1px solid transparent;
}

.radar-pass-summary-clean {
  border-color: var(--color-success);
}

.radar-pass-summary-alert {
  border-color: var(--color-danger);
}

.radar-pass-summary-title {
  font-weight: 700;
  margin: 0 0 4px;
}

.radar-pass-summary-detail,
.radar-pass-summary-errors {
  margin: 0;
  color: var(--color-text);
}

.radar-alert-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.radar-alert {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  background: var(--surface-subtle);
  border: 1px solid var(--color-muted-soft);
  border-radius: 8px;
  padding: 12px;
}

.radar-alert-severity {
  color: #fff;
  font-size: 0.75rem;
  font-weight: 700;
  border-radius: 4px;
  padding: 2px 6px;
  white-space: nowrap;
}

.radar-alert-body {
  flex: 1;
  min-width: 0;
}

.radar-alert-title,
.radar-alert-summary,
.radar-alert-meta {
  margin: 0 0 4px;
  overflow-wrap: anywhere;
}

.radar-alert-cve {
  color: var(--color-text-muted);
}

.radar-alert-ecosystem {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.radar-alert-meta {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.radar-alert-link {
  color: var(--color-text);
}

.radar-alert-status {
  font-weight: 700;
}

.radar-alert-status-new {
  color: var(--color-danger);
}

.radar-alert-status-acknowledged {
  color: var(--color-warning);
}

.radar-alert-status-resolved {
  color: var(--color-success);
}

.radar-settings {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 16px;
  border-top: 1px solid var(--color-muted-soft);
  padding-top: 12px;
}

.radar-settings-toggle,
.radar-settings-interval {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text);
}
</style>
