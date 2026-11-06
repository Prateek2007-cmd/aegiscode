<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Push Scans card. Shows the client's push-scan state — whether pushes start a
  security scan automatically, which branches are covered, and the most recent
  push-triggered scans with their commit SHAs — and lets a client administrator
  change the settings. The card never invents a state: no scans yet renders as
  "no push scans yet", a failed start renders with its reason, and a missing API
  renders as "API update not deployed yet".
-->
<template>
  <section class="push-scan-card" aria-label="Push scans">
    <h3 class="push-scan-title">Push scans</h3>
    <p class="push-scan-subtitle">
      Every push to a covered branch starts a security scan of the pushed commit automatically —
      one scan per commit, even if the same commit is pushed twice.
    </p>

    <p v-if="loading" class="push-scan-note">Loading push scans…</p>
    <p v-else-if="unavailable" class="empty-state" role="status">
      Push scans need the API update — not available yet.
    </p>
    <p v-else-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <template v-else-if="status">
      <form @submit.prevent="handleSave" novalidate>
        <div class="push-scan-field">
          <label class="push-scan-toggle">
            <input v-model="enabledDraft" type="checkbox" :disabled="saving" />
            Push scans enabled — scan the pushed commit on every push
          </label>
        </div>

        <div class="push-scan-field">
          <label for="push-scan-branches">Branches to scan (comma-separated, * matches any suffix)</label>
          <input
            id="push-scan-branches"
            v-model="branchesDraft"
            type="text"
            :disabled="saving || !enabledDraft"
            placeholder="main"
            autocomplete="off"
          />
          <p class="push-scan-note">Default is main only. Pushes to other branches are ignored, never scanned.</p>
        </div>

        <button class="btn-primary" type="submit" :disabled="saving || !settingsChanged">
          {{ saving ? 'Saving…' : 'Save push scan settings' }}
        </button>
      </form>

      <p v-if="saveError" class="error" role="alert">{{ saveError }}</p>
      <p v-if="savedNote" class="push-scan-saved" aria-live="polite">{{ savedNote }}</p>

      <div class="push-scan-recent" aria-live="polite">
        <h4 class="push-scan-recent-title">Recent push-triggered scans</h4>
        <template v-if="status.recentScans.length > 0">
          <ul class="push-scan-list">
            <li v-for="entry in status.recentScans" :key="`${entry.repositoryUrl}:${entry.headSha}`" class="push-scan-item">
              <p>
                <span
                  class="push-scan-chip"
                  :class="`push-scan-chip-${describePushScanOutcome(entry.outcome).tone}`"
                >{{ describePushScanOutcome(entry.outcome).label }}</span>
                <span v-if="entry.scanStatus" class="push-scan-note"> · scan {{ entry.scanStatus }}</span>
              </p>
              <p class="push-scan-entry">{{ describePushScanEntry(entry) }}</p>
              <p class="push-scan-note">{{ entry.reason }} · {{ formatDashboardDate(entry.updatedAt) }}</p>
            </li>
          </ul>
        </template>
        <p v-else class="empty-state">
          No push scans yet — the first push to a covered branch starts one.
        </p>
      </div>

      <p class="push-scan-note">
        Pushes only scan repositories this client covers, and only when the webhook is subscribed to
        push events. A push that did not start a scan is recorded in the webhook delivery history with its reason.
      </p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  getPushScanStatus,
  isPushScanUnavailable,
  updatePushScanConfig,
  type PushScanStatus,
} from '@/services/pushScanService'
import {
  describePushScanEntry,
  describePushScanOutcome,
  formatBranchFilter,
  parseBranchFilter,
} from '@/features/clients/components/pushScanFormatters'
import { formatDashboardDate } from '@/features/clients/components/clientDashboardFormatters'

const props = defineProps<{
  clientId: string
}>()

const status = ref<PushScanStatus | null>(null)
const loading = ref(true)
const loadError = ref<string | null>(null)
const unavailable = ref(false)

const enabledDraft = ref(true)
const branchesDraft = ref('main')
const saving = ref(false)
const saveError = ref<string | null>(null)
const savedNote = ref<string | null>(null)

const settingsChanged = computed(
  () =>
    status.value != null &&
    (enabledDraft.value !== status.value.config.enabled ||
      formatBranchFilter(parseBranchFilter(branchesDraft.value)) !==
        formatBranchFilter(status.value.config.branches)),
)

function applyStatus(loaded: PushScanStatus): void {
  status.value = loaded
  enabledDraft.value = loaded.config.enabled
  branchesDraft.value = formatBranchFilter(loaded.config.branches)
}

async function loadStatus(): Promise<void> {
  loading.value = true
  loadError.value = null
  unavailable.value = false
  try {
    applyStatus(await getPushScanStatus(props.clientId))
  } catch (error) {
    if (isPushScanUnavailable(error)) {
      unavailable.value = true
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to load push scans.'
    }
  } finally {
    loading.value = false
  }
}

async function handleSave(): Promise<void> {
  if (status.value == null) return
  saving.value = true
  saveError.value = null
  savedNote.value = null
  try {
    const saved = await updatePushScanConfig(props.clientId, {
      enabled: enabledDraft.value,
      branches: parseBranchFilter(branchesDraft.value),
    })
    status.value = { ...status.value, config: saved }
    savedNote.value = saved.enabled
      ? `Push scans on — pushes to ${formatBranchFilter(saved.branches)} start a scan.`
      : 'Push scans off — pushes will not start a scan.'
  } catch (error) {
    if (isPushScanUnavailable(error)) {
      unavailable.value = true
    } else {
      saveError.value = error instanceof Error ? error.message : 'Failed to save push scan settings.'
    }
  } finally {
    saving.value = false
  }
}

watch(
  () => props.clientId,
  () => {
    void loadStatus()
  },
)

onMounted(() => {
  void loadStatus()
})
</script>

<style scoped>
.push-scan-card {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.push-scan-title {
  margin: 0;
  font-size: 1rem;
}

.push-scan-subtitle {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.push-scan-note {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.push-scan-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.push-scan-toggle {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  font-size: 0.95rem;
}

.push-scan-saved {
  margin: 0;
  color: var(--color-success, var(--color-success));
}

.push-scan-recent {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding-top: 0.5rem;
  border-top: 1px solid var(--color-muted-soft);
}

.push-scan-recent-title {
  margin: 0;
  font-size: 0.95rem;
}

.push-scan-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.push-scan-item {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.push-scan-item p {
  margin: 0;
}

.push-scan-chip {
  display: inline-block;
  padding: 0.15rem 0.6rem;
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 600;
}

.push-scan-chip-success {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.push-scan-chip-failure {
  background: var(--color-danger-soft);
  color: var(--color-critical);
}

.push-scan-chip-neutral {
  background: var(--color-muted-soft);
  color: var(--color-text-muted);
}

.push-scan-entry {
  margin: 0;
  font-weight: 600;
}

.empty-state {
  color: var(--color-text-muted);
  font-style: italic;
}

.error {
  color: var(--color-danger);
}
</style>
