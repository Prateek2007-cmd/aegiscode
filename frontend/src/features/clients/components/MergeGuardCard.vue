<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Merge Guard card. Shows the guard's current state for one client — whether it
  is on, its severity threshold, and the latest check run it reported — and lets
  a client administrator change the settings. The card never invents a state:
  no checks yet renders as "no checks yet", a failed post renders with its
  error, and a missing API renders as "API update not deployed yet".
-->
<template>
  <section class="merge-guard-card" aria-label="Merge guard">
    <h3 class="merge-guard-title">Merge guard</h3>
    <p class="merge-guard-subtitle">
      Reports a GitHub check run on every reviewed pull request: failure when the latest scan or the
      review has blocking findings, success when the data is clean and fresh, neutral when the data
      is missing or stale.
    </p>

    <p v-if="loading" class="merge-guard-note">Loading merge guard…</p>
    <p v-else-if="unavailable" class="empty-state" role="status">
      Merge guard needs the API update — not available yet.
    </p>
    <p v-else-if="loadError" class="error" role="alert">{{ loadError }}</p>

    <template v-else-if="status">
      <form @submit.prevent="handleSave" novalidate>
        <div class="merge-guard-field">
          <label class="merge-guard-toggle">
            <input v-model="enabledDraft" type="checkbox" :disabled="saving" />
            Guard enabled — report a check run when a review is published
          </label>
        </div>

        <div class="merge-guard-field">
          <label for="merge-guard-threshold">Block merging at severity</label>
          <select
            id="merge-guard-threshold"
            v-model="thresholdDraft"
            :disabled="saving || !enabledDraft"
          >
            <option
              v-for="option in guardThresholdOptions"
              :key="option.value"
              :value="option.value"
            >
              {{ option.label }}
            </option>
          </select>
          <p class="merge-guard-note">{{ selectedThresholdDetail }}</p>
        </div>

        <button class="btn-primary" type="submit" :disabled="saving || !settingsChanged">
          {{ saving ? 'Saving…' : 'Save guard settings' }}
        </button>
      </form>

      <p v-if="saveError" class="error" role="alert">{{ saveError }}</p>
      <p v-if="savedNote" class="merge-guard-saved" aria-live="polite">{{ savedNote }}</p>

      <div class="merge-guard-latest" aria-live="polite">
        <h4 class="merge-guard-latest-title">Latest check</h4>
        <template v-if="latestCheck">
          <p>
            <span
              class="merge-guard-chip"
              :class="`merge-guard-chip-${latestDecision.tone}`"
            >{{ latestDecision.label }}</span>
          </p>
          <p class="merge-guard-check-title">{{ latestCheck.title }}</p>
          <p class="merge-guard-check-reason">{{ latestCheck.reason }}</p>
          <p class="merge-guard-note">
            {{ latestCheck.repositoryUrl }} — PR #{{ latestCheck.pullRequestNumber }}
            (head <code>{{ shortSha(latestCheck.headSha) }}</code>) ·
            {{ describeBlockingFindings(latestCheck.blockingScanFindings, latestCheck.blockingReviewFindings) }}
            · {{ formatDashboardDate(latestCheck.updatedAt) }}
          </p>
          <p v-if="!latestCheck.posted" class="error" role="alert">
            This decision never reached GitHub{{ latestCheck.postError ? `: ${latestCheck.postError}` : '.' }}
          </p>
          <a
            v-if="latestCheck.posted && latestCheck.checkRunUrl"
            class="merge-guard-link"
            :href="latestCheck.checkRunUrl"
            target="_blank"
            rel="noopener noreferrer"
          >Open the check run on GitHub ↗</a>
        </template>
        <p v-else class="empty-state">
          No checks reported yet — the guard reports when a review is published.
        </p>
      </div>

      <p class="merge-guard-note">
        A failed check only blocks merging when “AegisCode Merge Guard” is added to the branch's
        required status checks. The check reports the decision; branch protection enforces it.
      </p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  getMergeGuardStatus,
  isMergeGuardUnavailable,
  updateMergeGuardConfig,
  type MergeGuardStatus,
  type MergeGuardThreshold,
} from '@/services/mergeGuardService'
import {
  describeBlockingFindings,
  describeGuardDecision,
  guardThresholdOptions,
} from '@/features/clients/components/mergeGuardFormatters'
import { formatDashboardDate } from '@/features/clients/components/clientDashboardFormatters'

const props = defineProps<{
  clientId: string
}>()

const status = ref<MergeGuardStatus | null>(null)
const loading = ref(true)
const loadError = ref<string | null>(null)
const unavailable = ref(false)

const enabledDraft = ref(false)
const thresholdDraft = ref<MergeGuardThreshold>('High')
const saving = ref(false)
const saveError = ref<string | null>(null)
const savedNote = ref<string | null>(null)

const latestCheck = computed(() => status.value?.checks?.[0] ?? null)
const latestDecision = computed(() => describeGuardDecision(latestCheck.value?.conclusion))
const selectedThresholdDetail = computed(
  () => guardThresholdOptions.find((option) => option.value === thresholdDraft.value)?.detail ?? '',
)
const settingsChanged = computed(
  () =>
    status.value != null &&
    (enabledDraft.value !== status.value.config.enabled ||
      thresholdDraft.value !== status.value.config.threshold),
)

function shortSha(sha: string): string {
  return sha.length > 7 ? sha.slice(0, 7) : sha
}

function applyStatus(loaded: MergeGuardStatus): void {
  status.value = loaded
  enabledDraft.value = loaded.config.enabled
  thresholdDraft.value = loaded.config.threshold
}

async function loadStatus(): Promise<void> {
  loading.value = true
  loadError.value = null
  unavailable.value = false
  try {
    applyStatus(await getMergeGuardStatus(props.clientId))
  } catch (error) {
    if (isMergeGuardUnavailable(error)) {
      unavailable.value = true
    } else {
      loadError.value = error instanceof Error ? error.message : 'Failed to load merge guard status.'
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
    const saved = await updateMergeGuardConfig(props.clientId, {
      enabled: enabledDraft.value,
      threshold: thresholdDraft.value,
    })
    status.value = { ...status.value, config: saved }
    savedNote.value = saved.enabled
      ? `Guard on — findings at ${saved.threshold} and above will fail the check.`
      : 'Guard off — no check runs will be posted.'
  } catch (error) {
    if (isMergeGuardUnavailable(error)) {
      unavailable.value = true
    } else {
      saveError.value = error instanceof Error ? error.message : 'Failed to save merge guard settings.'
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
.merge-guard-card {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.merge-guard-title {
  margin: 0;
  font-size: 1rem;
}

.merge-guard-subtitle {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.merge-guard-note {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.merge-guard-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.merge-guard-toggle {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  font-size: 0.95rem;
}

.merge-guard-saved {
  margin: 0;
  color: var(--color-success, #15803d);
}

.merge-guard-latest {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding-top: 0.5rem;
  border-top: 1px solid rgba(148, 163, 184, 0.25);
}

.merge-guard-latest-title {
  margin: 0;
  font-size: 0.95rem;
}

.merge-guard-chip {
  display: inline-block;
  padding: 0.15rem 0.6rem;
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 600;
}

.merge-guard-chip-success {
  background: rgba(22, 163, 74, 0.15);
  color: #15803d;
}

.merge-guard-chip-failure {
  background: rgba(220, 38, 38, 0.15);
  color: #b91c1c;
}

.merge-guard-chip-neutral {
  background: rgba(148, 163, 184, 0.2);
  color: var(--color-text-muted);
}

.merge-guard-check-title {
  margin: 0;
  font-weight: 600;
}

.merge-guard-check-reason {
  margin: 0;
}

.merge-guard-link {
  color: var(--color-accent);
  text-decoration: none;
  font-size: 0.9rem;
}

.empty-state {
  color: var(--color-text-muted);
  font-style: italic;
}

.error {
  color: var(--color-danger);
}
</style>
