<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<template>
  <div class="review-rules-panel">
    <p class="rules-intro">
      Your team's explicit policy for this repo — separate from reviewer memory, which learns from your
      decisions. A rule matches a file path or file content literally, upgrades a matching finding to its
      severity, and cites itself by name. It never invents a finding.
    </p>

    <p v-if="loading" class="empty-state">Loading review rules…</p>
    <p v-else-if="unavailable" class="empty-state">Review rules unavailable — the API update is not deployed yet.</p>
    <p v-else-if="loadError" class="error" role="alert">{{ loadError }}</p>
    <template v-else>
      <p v-if="entries.length === 0" class="empty-state">
        No custom rules yet — reviews use the default rules.
      </p>
      <template v-else>
        <p class="rules-summary">{{ describeRules(entries) }}</p>
        <ul class="rules-list">
          <li v-for="entry in entries" :key="entry.id" class="rules-row">
            <div class="rules-main">
              <span class="rules-name">{{ entry.name }}</span>
              <span class="rules-pattern">{{ entry.pattern }}</span>
              <span class="rules-meta">{{ ruleSummary(entry) }}</span>
              <span class="rules-message">{{ entry.message }}</span>
            </div>
            <div class="rules-actions">
              <span :class="['rules-status', entry.enabled ? 'rules-status-enabled' : 'rules-status-disabled']">
                {{ statusLabel(entry) }}
              </span>
              <button
                class="btn-ghost btn-sm"
                :disabled="actingId === entry.id"
                @click="toggle(entry)"
              >
                {{ entry.enabled ? 'Disable' : 'Enable' }}
              </button>
              <button
                class="btn-ghost btn-sm"
                :disabled="actingId === entry.id"
                @click="startEdit(entry)"
              >
                Edit
              </button>
              <button
                class="btn-ghost btn-sm"
                :disabled="actingId === entry.id"
                @click="remove(entry)"
              >
                Delete
              </button>
            </div>
          </li>
        </ul>
      </template>

      <div class="rules-form">
        <h4 class="rules-form-title">{{ editing ? 'Edit rule' : 'New rule' }}</h4>
        <label class="rules-field">
          <span>Name</span>
          <input v-model="form.name" type="text" placeholder="e.g. No Console.WriteLine" maxlength="120" />
        </label>
        <label class="rules-field">
          <span>Pattern — matched against file paths and file content</span>
          <input v-model="form.pattern" type="text" placeholder="e.g. Console.WriteLine" maxlength="500" />
        </label>
        <label class="rules-field rules-field-check">
          <input v-model="form.isRegex" type="checkbox" />
          <span>Pattern is a regular expression</span>
        </label>
        <label class="rules-field">
          <span>Severity</span>
          <select v-model="form.severity">
            <option v-for="severity in reviewRuleSeverities" :key="severity" :value="severity">
              {{ severity }}
            </option>
          </select>
        </label>
        <label class="rules-field">
          <span>Message — shown on matching findings</span>
          <input v-model="form.message" type="text" placeholder="e.g. Do not write to the console in production code." maxlength="500" />
        </label>
        <div class="rules-form-actions">
          <button class="btn-secondary btn-sm" :disabled="saving" @click="save">
            {{ editing ? 'Save changes' : 'Add rule' }}
          </button>
          <button v-if="editing" class="btn-ghost btn-sm" :disabled="saving" @click="cancelEdit">
            Cancel
          </button>
        </div>
        <p v-if="formError" class="error" role="alert">{{ formError }}</p>
      </div>
      <p v-if="actionError" class="error" role="alert">{{ actionError }}</p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import {
  createReviewRule,
  deleteReviewRule,
  isReviewRulesUnavailable,
  listReviewRules,
  setReviewRuleEnabled,
  updateReviewRule,
  type ReviewRuleEntry,
  type ReviewRuleSeverity,
} from '@/services/reviewRulesService'
import {
  describeRules,
  reviewRuleSeverities,
  ruleSummary,
  statusLabel,
  validateRuleInput,
} from '@/features/clients/components/reviewRulesFormatters'

const props = defineProps<{
    clientId: string
}>()

const entries = ref<ReviewRuleEntry[]>([])
const loading = ref(true)
const unavailable = ref(false)
const loadError = ref('')
const actionError = ref('')
const formError = ref('')
const actingId = ref<string | null>(null)
const saving = ref(false)
const editing = ref<ReviewRuleEntry | null>(null)

const form = ref<{
    name: string
    pattern: string
    isRegex: boolean
    severity: ReviewRuleSeverity
    message: string
}>({
    name: '',
    pattern: '',
    isRegex: false,
    severity: 'Warning',
    message: '',
})

function resetForm(): void {
    form.value = { name: '', pattern: '', isRegex: false, severity: 'Warning', message: '' }
    editing.value = null
    formError.value = ''
}

async function load(): Promise<void> {
    loading.value = true
    unavailable.value = false
    loadError.value = ''
    try {
        entries.value = await listReviewRules(props.clientId)
    } catch (err) {
        if (isReviewRulesUnavailable(err)) {
            unavailable.value = true
        } else {
            loadError.value = err instanceof Error ? err.message : 'Failed to load review rules.'
        }
    } finally {
        loading.value = false
    }
}

async function save(): Promise<void> {
    formError.value = ''
    const invalid = validateRuleInput(form.value)
    if (invalid) {
        formError.value = invalid
        return
    }
    saving.value = true
    try {
        if (editing.value) {
            await updateReviewRule(props.clientId, editing.value.id, {
                ...form.value,
                enabled: editing.value.enabled,
            })
        } else {
            await createReviewRule(props.clientId, form.value)
        }
        resetForm()
        await load()
    } catch (err) {
        // The API rejects an invalid regex with the real regex error; show it as-is.
        formError.value = err instanceof Error ? err.message : 'Failed to save the rule.'
    } finally {
        saving.value = false
    }
}

function startEdit(entry: ReviewRuleEntry): void {
    editing.value = entry
    form.value = {
        name: entry.name,
        pattern: entry.pattern,
        isRegex: entry.isRegex,
        severity: entry.severity,
        message: entry.message,
    }
    formError.value = ''
}

function cancelEdit(): void {
    resetForm()
}

async function toggle(entry: ReviewRuleEntry): Promise<void> {
    actingId.value = entry.id
    actionError.value = ''
    try {
        await setReviewRuleEnabled(props.clientId, entry.id, !entry.enabled)
        await load()
    } catch (err) {
        actionError.value = err instanceof Error ? err.message : 'Failed to change the rule.'
    } finally {
        actingId.value = null
    }
}

async function remove(entry: ReviewRuleEntry): Promise<void> {
    actingId.value = entry.id
    actionError.value = ''
    try {
        await deleteReviewRule(props.clientId, entry.id)
        if (editing.value?.id === entry.id) resetForm()
        await load()
    } catch (err) {
        actionError.value = err instanceof Error ? err.message : 'Failed to delete the rule.'
    } finally {
        actingId.value = null
    }
}

onMounted(load)
</script>

<style scoped>
.review-rules-panel {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.rules-intro {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.rules-summary {
  margin: 0;
  font-weight: 600;
}

.rules-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.rules-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
}

.rules-main {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

.rules-name {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.rules-pattern {
  font-family: monospace;
  font-size: 0.85rem;
  overflow-wrap: anywhere;
}

.rules-meta {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.rules-message {
  font-size: 0.9rem;
  overflow-wrap: anywhere;
}

.rules-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-shrink: 0;
  flex-wrap: wrap;
}

.rules-status {
  font-size: 0.8rem;
  font-weight: 600;
  padding: 0.1rem 0.5rem;
  border-radius: 999px;
}

.rules-status-enabled {
  background: var(--color-success-bg, #e6f4ea);
  color: var(--color-success, #137333);
}

.rules-status-disabled {
  background: var(--color-warning-bg, #fef7e0);
  color: var(--color-warning, #9a6a00);
}

.rules-form {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
}

.rules-form-title {
  margin: 0;
}

.rules-field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.9rem;
}

.rules-field-check {
  flex-direction: row;
  align-items: center;
  gap: 0.5rem;
}

.rules-form-actions {
  display: flex;
  gap: 0.5rem;
}
</style>
