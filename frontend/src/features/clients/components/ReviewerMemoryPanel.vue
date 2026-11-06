<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<template>
  <div class="reviewer-memory-panel">
    <p class="memory-intro">
      What the reviewer learned from your decisions. A rule you dismiss repeatedly mutes itself —
      muted rules never reach a pull request — and you can un-mute or reset them here.
    </p>

    <p v-if="loading" class="empty-state">Loading reviewer memory…</p>
    <p v-else-if="unavailable" class="empty-state">Reviewer memory unavailable — the API update is not deployed yet.</p>
    <p v-else-if="loadError" class="error" role="alert">{{ loadError }}</p>
    <p v-else-if="entries.length === 0" class="empty-state">
      Nothing learned yet. Dismiss a finding or decide a fix proposal and the reviewer will start learning.
    </p>

    <template v-else>
      <p class="memory-summary">{{ describeMemory(entries) }}</p>
      <ul class="memory-list">
        <li v-for="entry in entries" :key="entry.ruleKey" class="memory-row">
          <div class="memory-main">
            <span class="memory-label">{{ entry.label }}</span>
            <span class="memory-meta">
              {{ sourceLabel(entry.source) }} · {{ countsText(entry) }}
            </span>
            <span v-if="mutedDetail(entry)" class="memory-muted" role="note">
              {{ mutedDetail(entry) }}
            </span>
          </div>
          <div class="memory-actions">
            <span :class="['memory-status', entry.muted ? 'memory-status-muted' : 'memory-status-active']">
              {{ statusLabel(entry) }}
            </span>
            <button
              v-if="entry.muted"
              class="btn-ghost btn-sm"
              :disabled="actingKey === entry.ruleKey"
              @click="unmute(entry)"
            >
              Un-mute
            </button>
            <button
              class="btn-ghost btn-sm"
              :disabled="actingKey === entry.ruleKey"
              @click="reset(entry)"
            >
              Reset
            </button>
          </div>
        </li>
      </ul>
      <p v-if="actionError" class="error" role="alert">{{ actionError }}</p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import {
  isReviewerMemoryUnavailable,
  listReviewerMemory,
  resetReviewerMemoryRule,
  unmuteReviewerMemoryRule,
  type ReviewerMemoryEntry,
} from '@/services/reviewerMemoryService'
import {
  countsText,
  describeMemory,
  mutedDetail,
  sourceLabel,
  statusLabel,
} from '@/features/clients/components/reviewerMemoryFormatters'

const props = defineProps<{
    clientId: string
}>()

const entries = ref<ReviewerMemoryEntry[]>([])
const loading = ref(true)
const unavailable = ref(false)
const loadError = ref('')
const actionError = ref('')
const actingKey = ref<string | null>(null)

async function load(): Promise<void> {
    loading.value = true
    unavailable.value = false
    loadError.value = ''
    try {
        entries.value = await listReviewerMemory(props.clientId)
    } catch (err) {
        if (isReviewerMemoryUnavailable(err)) {
            unavailable.value = true
        } else {
            loadError.value = err instanceof Error ? err.message : 'Failed to load reviewer memory.'
        }
    } finally {
        loading.value = false
    }
}

async function unmute(entry: ReviewerMemoryEntry): Promise<void> {
    actingKey.value = entry.ruleKey
    actionError.value = ''
    try {
        await unmuteReviewerMemoryRule(props.clientId, entry.ruleKey)
        await load()
    } catch (err) {
        actionError.value = err instanceof Error ? err.message : 'Failed to un-mute the rule.'
    } finally {
        actingKey.value = null
    }
}

async function reset(entry: ReviewerMemoryEntry): Promise<void> {
    actingKey.value = entry.ruleKey
    actionError.value = ''
    try {
        await resetReviewerMemoryRule(props.clientId, entry.ruleKey)
        await load()
    } catch (err) {
        actionError.value = err instanceof Error ? err.message : 'Failed to reset the rule.'
    } finally {
        actingKey.value = null
    }
}

onMounted(load)
</script>

<style scoped>
.reviewer-memory-panel {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.memory-intro {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.memory-summary {
  margin: 0;
  font-weight: 600;
}

.memory-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.memory-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
}

.memory-main {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

.memory-label {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.memory-meta {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.memory-muted {
  color: var(--color-warning, var(--color-warning));
  font-size: 0.85rem;
}

.memory-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-shrink: 0;
}

.memory-status {
  font-size: 0.8rem;
  font-weight: 600;
  padding: 0.1rem 0.5rem;
  border-radius: 999px;
}

.memory-status-active {
  background: var(--color-success-bg, var(--color-success-soft));
  color: var(--color-success, var(--color-success));
}

.memory-status-muted {
  background: var(--color-warning-bg, var(--color-warning-soft));
  color: var(--color-warning, var(--color-warning));
}
</style>
