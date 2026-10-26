<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<!--
  Trigger Review card. Asks for a review of one pull request by coordinates
  (repository + PR number) through submitReviewByCoordinates. The provider
  host comes from the client's first active GitHub connection, falling back
  to https://github.com with a visible note when there is none.
-->
<template>
  <section class="trigger-review-card" aria-label="Trigger review">
    <h3 class="trigger-review-title">Trigger review</h3>
    <p class="trigger-review-subtitle">
      Ask AegisCode to review a pull request as it stands now.
    </p>

    <p class="trigger-review-host">
      <span class="trigger-review-host-label">Provider host</span>
      <code class="trigger-review-host-value">{{ providerScopePath }}</code>
    </p>
    <p v-if="showNoConnectionNote" class="trigger-review-note">
      No active GitHub connection was found for this client, so the default host is used.
    </p>
    <p v-if="connectionsLoading" class="trigger-review-note">Loading provider connections…</p>

    <form @submit.prevent="handleSubmit" novalidate>
      <div class="trigger-review-field">
        <label for="trigger-review-repository">Repository</label>
        <input
          id="trigger-review-repository"
          v-model="repository"
          type="text"
          placeholder="owner/repo"
          autocomplete="off"
          :disabled="submitting"
          @blur="validateRepository"
        />
        <p v-if="repositoryError" class="error" role="alert">{{ repositoryError }}</p>
      </div>

      <div class="trigger-review-field">
        <label for="trigger-review-pr-number">Pull request number</label>
        <input
          id="trigger-review-pr-number"
          v-model="prNumber"
          type="text"
          inputmode="numeric"
          placeholder="1"
          autocomplete="off"
          :disabled="submitting"
          @blur="validatePrNumber"
        />
        <p v-if="prNumberError" class="error" role="alert">{{ prNumberError }}</p>
      </div>

      <button class="btn-primary" type="submit" :disabled="submitDisabled">
        {{ submitting ? 'Requesting review…' : 'Request review' }}
      </button>
    </form>

    <div
      v-if="outcome || transportError"
      class="trigger-review-result"
      :class="outcome ? `trigger-review-result-${outcome.tone}` : 'trigger-review-result-error'"
      aria-live="polite"
    >
      <template v-if="outcome">
        <p class="trigger-review-result-title">{{ outcome.title }}</p>
        <p class="trigger-review-result-detail">{{ outcome.detail }}</p>
        <RouterLink
          v-if="submittedJobId"
          class="trigger-review-job-link"
          :to="{ name: 'job-protocol', params: { id: submittedJobId } }"
        >
          Watch this job ↗
        </RouterLink>
        <button class="btn-ghost trigger-review-again" type="button" @click="resetResult">
          Request another review
        </button>
      </template>
      <template v-else>
        <p class="trigger-review-result-title">The review could not be requested.</p>
        <p class="trigger-review-result-detail">{{ transportError }}</p>
        <button class="btn-ghost trigger-review-again" type="button" @click="resetResult">
          Request another review
        </button>
      </template>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { submitReviewByCoordinates } from '@/services/jobsService'
import type { ReviewByCoordinatesResult } from '@/services/jobsService'
import { listProviderConnections } from '@/services/providerConnectionsService'
import {
  buildPullRequestIdentity,
  DEFAULT_PROVIDER_HOST,
  describeReviewOutcome,
  validatePullRequestNumber,
  validateRepositoryInput,
} from '@/features/clients/components/triggerReviewFormatters'
import type { ReviewOutcomeDescription } from '@/features/clients/components/triggerReviewFormatters'

const props = defineProps<{
  clientId: string
}>()

const repository = ref('')
const prNumber = ref('')
const repositoryError = ref<string | null>(null)
const prNumberError = ref<string | null>(null)

const providerScopePath = ref(DEFAULT_PROVIDER_HOST)
const hasActiveGitHubConnection = ref(false)
const connectionsLoading = ref(true)
const connectionsLoaded = ref(false)

const submitting = ref(false)
const result = ref<ReviewByCoordinatesResult | null>(null)
const transportError = ref<string | null>(null)

const showNoConnectionNote = computed(
  () => connectionsLoaded.value && !hasActiveGitHubConnection.value,
)
const submitDisabled = computed(() => connectionsLoading.value || submitting.value)
const outcome = computed<ReviewOutcomeDescription | null>(() =>
  result.value ? describeReviewOutcome(result.value) : null,
)
const submittedJobId = computed(() => result.value?.jobId ?? null)

onMounted(async () => {
  try {
    const connections = await listProviderConnections(props.clientId)
    const github = connections.find(
      (connection) => connection.providerFamily === 'github' && connection.isActive,
    )
    if (github) {
      providerScopePath.value = github.hostBaseUrl
      hasActiveGitHubConnection.value = true
    }
  } catch {
    // No active GitHub connection could be resolved; the default host stays
    // in place and the note below says so.
    hasActiveGitHubConnection.value = false
  } finally {
    connectionsLoading.value = false
    connectionsLoaded.value = true
  }
})

function validateRepository(): void {
  repositoryError.value = validateRepositoryInput(repository.value)
}

function validatePrNumber(): void {
  prNumberError.value = validatePullRequestNumber(prNumber.value)
}

function resetResult(): void {
  result.value = null
  transportError.value = null
}

async function handleSubmit(): Promise<void> {
  validateRepository()
  validatePrNumber()
  if (repositoryError.value || prNumberError.value) return

  const identity = buildPullRequestIdentity(
    providerScopePath.value,
    repository.value,
    Number.parseInt(prNumber.value.trim(), 10),
  )

  submitting.value = true
  result.value = null
  transportError.value = null
  try {
    result.value = await submitReviewByCoordinates(props.clientId, identity)
  } catch (error) {
    transportError.value =
      error instanceof Error ? error.message : 'The review could not be requested.'
  } finally {
    submitting.value = false
  }
}
</script>

<style scoped>
.trigger-review-card {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.25rem 1.4rem;
  color: #e2e8f0;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.trigger-review-title {
  margin: 0;
  font-size: 1.05rem;
}

.trigger-review-subtitle {
  margin: 0;
  color: #94a3b8;
  font-size: 0.9rem;
}

.trigger-review-host {
  margin: 0;
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 0.5rem;
  font-size: 0.85rem;
}

.trigger-review-host-label {
  color: #94a3b8;
}

.trigger-review-host-value {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  color: #7dd3fc;
  font-size: 0.9rem;
  word-break: break-all;
}

.trigger-review-note {
  margin: 0;
  color: #fbbf24;
  font-size: 0.85rem;
}

.trigger-review-card form {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin-top: 0.25rem;
}

.trigger-review-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.trigger-review-field label {
  font-size: 0.85rem;
  font-weight: 600;
}

.trigger-review-field input {
  background: rgba(148, 163, 184, 0.08);
  border: 1px solid rgba(56, 189, 248, 0.25);
  border-radius: 8px;
  color: #e2e8f0;
  padding: 0.55rem 0.75rem;
  font-size: 0.95rem;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
}

.trigger-review-field input:disabled {
  opacity: 0.6;
}

.trigger-review-field input::placeholder {
  color: #64748b;
}

.error {
  color: var(--color-danger);
  margin: 0;
  font-size: 0.85rem;
}

.trigger-review-result {
  border-radius: 10px;
  padding: 0.9rem 1rem;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.4rem;
}

.trigger-review-result-success {
  background: rgba(16, 185, 129, 0.12);
  border: 1px solid rgba(52, 211, 153, 0.4);
}

.trigger-review-result-warning {
  background: rgba(251, 191, 36, 0.1);
  border: 1px solid rgba(251, 191, 36, 0.45);
}

.trigger-review-result-error {
  background: rgba(239, 68, 68, 0.1);
  border: 1px solid rgba(248, 113, 113, 0.45);
}

.trigger-review-result-title {
  margin: 0;
  font-weight: 700;
  font-size: 0.95rem;
}

.trigger-review-result-detail {
  margin: 0;
  color: #cbd5e1;
  font-size: 0.9rem;
}

.trigger-review-job-link {
  color: #7dd3fc;
  font-weight: 600;
  font-size: 0.9rem;
  text-decoration: none;
}

.trigger-review-job-link:hover {
  text-decoration: underline;
}

.trigger-review-again {
  margin-top: 0.35rem;
  padding: 0;
}
</style>
