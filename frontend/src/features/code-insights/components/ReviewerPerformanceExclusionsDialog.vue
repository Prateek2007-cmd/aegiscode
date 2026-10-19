<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <dialog
    ref="dialog"
    class="performance-exclusions"
    aria-labelledby="performance-exclusions-title"
    tabindex="-1"
    @keydown="trapFocus"
    @close="$emit('close')"
  >
    <header class="performance-exclusions__header">
      <div>
        <h2 id="performance-exclusions-title">Excluded pull requests</h2>
        <p>{{ label }} · {{ windowFrom }} → {{ windowTo }}</p>
      </div>
      <button
        type="button"
        class="performance-action"
        autofocus
        aria-label="Close exclusions"
        @click="$emit('close')"
      >
        Close
      </button>
    </header>
    <p v-if="frozen" class="performance-exclusions__note">
      Captured details. Recovery commands are unavailable in saved reports.
    </p>
    <p v-else class="performance-exclusions__note">
      Use the recovery action supported by the retained evidence. Displayed measurements change
      only after a refresh. Historical execution proof cannot be reconstructed by retrying collection.
    </p>
    <p v-if="refreshError" role="alert">{{ refreshError }}</p>
    <p v-if="recoveryPending" role="status">
      Waiting for the recovery request to finish before refreshing evidence.
    </p>
    <p v-if="entries.length === 0" role="status">No excluded sources remain for this measurement.</p>
    <div
      class="performance-exclusions__scroll"
      tabindex="0"
      aria-label="Excluded pull requests table"
    >
      <table>
        <caption>
          {{ entries.length }} excluded source PR{{ entries.length === 1 ? '' : 's' }}
          · page {{ page + 1 }} of {{ pageCount }}
        </caption>
        <thead>
          <tr>
            <th scope="col">Pull request</th>
            <th scope="col">Client / repository</th>
            <th scope="col">Evidence limitation</th>
            <th scope="col">Evidence attempts</th>
            <th scope="col">Recovery</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="entry in pageEntries"
            :key="entry.id"
            :data-exclusion-id="entry.id"
          >
            <th scope="row" data-label="Pull request">
              <a
                v-if="sourceLink(entry.sourceUrl)"
                :href="sourceLink(entry.sourceUrl)"
                target="_blank"
                rel="noopener noreferrer"
              >
                PR #{{ entry.pullRequestId }}
                <span class="visually-hidden">(opens source in a new tab)</span>
              </a>
              <span v-else>
                PR #{{ entry.pullRequestId }}
                <small>Source link unavailable</small>
              </span>
            </th>
            <td data-label="Client / repository">
              <strong>{{ entry.clientName || entry.clientId }}</strong>
              <span>{{ entry.repositoryName || entry.repositoryId }}</span>
              <small>{{ entry.providerScope || 'Source identity unavailable' }}</small>
            </td>
            <td data-label="Evidence limitation">
              <ul>
                <li v-for="reason in entry.reasons ?? []" :key="reason">
                  {{ exclusionReasonLabel(reason) }}
                </li>
              </ul>
            </td>
            <td data-label="Evidence attempts">
              <template v-if="entry.recoveryAction === 'retry-model-evidence'">
                <span v-if="entry.lastModelRetryAt">Last classification attempt
                  <time :datetime="entry.lastModelRetryAt">{{ dateLabel(entry.lastModelRetryAt) }}</time>
                </span>
                <span v-if="entry.modelRetryNotBefore">Classification retry eligible after
                  <time :datetime="entry.modelRetryNotBefore">{{ dateLabel(entry.modelRetryNotBefore) }}</time>
                </span>
              </template>
              <span v-if="entry.lastAttemptAt">
                Last attempted
                <time :datetime="entry.lastAttemptAt" :title="entry.lastAttemptAt">
                  {{ dateLabel(entry.lastAttemptAt) }}
                </time>
              </span>
              <span v-else>No attempt recorded</span>
              <span v-if="entry.lastRefreshReason">{{ refreshReason(entry.lastRefreshReason) }}</span>
              <span v-if="entry.lastCompletedAt">
                Last completed
                <time :datetime="entry.lastCompletedAt">{{ dateLabel(entry.lastCompletedAt) }}</time>
              </span>
              <span v-if="entry.notBefore && retryTimeLabel(entry) && !localAction(entry)?.requestedAt">
                {{ retryTimeLabel(entry) }}
                <time :datetime="entry.notBefore" :title="entry.notBefore">
                  {{ dateLabel(entry.notBefore) }}
                </time>
              </span>
              <small v-else-if="!entry.notBefore">No retry time recorded</small>
            </td>
            <td data-label="Recovery">
              <strong>{{ dispositionLabel(currentDisposition(entry)) }}</strong>
              <span v-if="entry.collectionEnabled === false">
                Enable Code Insights collection for this client before retrying.
              </span>
              <span v-else-if="currentDisposition(entry) === 'in-progress'">
                Wait for the current observation to finish.
              </span>
              <span v-else-if="currentDisposition(entry) === 'queued'" role="status">
                Queued for a background sweep. Retained evidence changes after collection completes.
              </span>
              <span v-else-if="currentDisposition(entry) === 'completed'" role="status">
                Classification attempt completed. Refresh current evidence to inspect its result.
              </span>
              <span v-else-if="entry.recoveryAction === 'none'">
                Retained execution or concern proof is incomplete. Collection retries cannot reconstruct it.
              </span>
              <span v-if="localAction(entry)?.requestedAt">
                Request acknowledged
                <time :datetime="localAction(entry)?.requestedAt">{{ dateLabel(localAction(entry)!.requestedAt!) }}</time>
              </span>
              <template v-if="canRetry(entry)">
                <button
                  type="button"
                  class="performance-action"
                  data-test="retry-evidence"
                  :disabled="refreshing || localAction(entry)?.state === 'pending'"
                  @click="retry(entry)"
                >
                  {{ localAction(entry)?.state === 'pending' ? 'Requesting…' : entry.recoveryAction === 'retry-model-evidence' ? 'Retry retained classification' : 'Requeue collection' }}
                </button>
                <p v-if="localAction(entry)?.error" role="alert">
                  {{ localAction(entry)?.error }}
                </p>
              </template>
              <small v-else-if="!frozen && !canManage">
                Tenant administration and the Code Insights capability are required to requeue.
              </small>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <footer class="performance-exclusions__footer">
      <nav aria-label="Exclusions pages">
        <button
          type="button"
          class="performance-action"
          aria-label="Previous exclusions page"
          :disabled="page === 0"
          @click="page--"
        >
          Previous
        </button>
        <span aria-live="polite">{{ page + 1 }} / {{ pageCount }}</span>
        <button
          type="button"
          class="performance-action"
          aria-label="Next exclusions page"
          :disabled="page + 1 >= pageCount"
          @click="page++"
        >
          Next
        </button>
      </nav>
      <button
        v-if="!frozen"
        type="button"
        class="performance-action"
        :disabled="refreshing || recoveryPending"
        :aria-busy="refreshing || recoveryPending"
        @click="$emit('refresh')"
      >
        {{ refreshing ? 'Refreshing…' : 'Refresh current evidence' }}
      </button>
    </footer>
  </dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'
import { type PerformanceExclusion } from '@/services/reviewerPerformanceService'
import { requestEvidenceRecovery, recoveryActionKey, type PerformanceRecoveryActions, type ModelRecoveryTarget } from '../reviewerPerformanceRecovery'
import { exclusionReasonLabel } from '../reviewerPerformanceEvidence'
import { useReviewerPerformanceExplorerDialog } from '../composables/useReviewerPerformanceExplorerDialog'

const props = defineProps<{
  entries: PerformanceExclusion[]
  label: string
  windowFrom?: string
  windowTo?: string
  frozen: boolean
  refreshing?: boolean
  refreshError?: string
  requestCollection?: (aggregateId: string, source?: ModelRecoveryTarget) => Promise<void>
}>()
defineEmits<{ close: []; refresh: [] }>()
const { dialog, trapFocus } = useReviewerPerformanceExplorerDialog()
const session = useSession()
const canManage = computed(() => session.isCapabilityAvailable('code-insights') && (
  session.isAdmin.value || Object.values(session.tenantRoles.value).some((role) => role >= RoleLevel.Administrator)
))
const page = ref(0)
const pageSize = 10
const pageCount = computed(() => Math.max(1, Math.ceil(props.entries.length / pageSize)))
const pageEntries = computed(() => props.entries.slice(page.value * pageSize, (page.value + 1) * pageSize))
const actions = defineModel<PerformanceRecoveryActions>('actions', { default: () => ({}) })
const recoveryPending = computed(() => !props.frozen && Object.values(actions.value).some((action) => action.state === 'pending'))
watch(pageCount, (count) => { page.value = Math.min(page.value, count - 1) })
function localAction(entry: PerformanceExclusion) {
  return props.frozen ? undefined : actions.value[recoveryActionKey(entry.aggregateId ?? '', modelTarget(entry))]
}
function currentDisposition(entry: PerformanceExclusion): string | undefined {
  const state = localAction(entry)?.state
  return state === 'queued' || state === 'completed' ? state : entry.recoveryDisposition ?? undefined
}
function modelTarget(entry: PerformanceExclusion): ModelRecoveryTarget | undefined {
  return entry.recoveryAction === 'retry-model-evidence' && entry.providerScope && entry.sourceConnectionId
    ? { providerScope: entry.providerScope, connectionId: entry.sourceConnectionId } : undefined
}
function canRetry(entry: PerformanceExclusion): boolean {
  return !props.frozen && canManage.value && !!entry.aggregateId && entry.collectionEnabled !== false && entry.recoveryAction !== 'none'
    && (entry.recoveryAction !== 'retry-model-evidence' || !!modelTarget(entry))
    && !['in-progress', 'queued', 'completed', 'cooldown'].includes(currentDisposition(entry) ?? '')
}
async function retry(entry: PerformanceExclusion): Promise<void> {
  if (!canRetry(entry) || props.refreshing || localAction(entry)?.state === 'pending') return
  if (props.requestCollection) await props.requestCollection(entry.aggregateId!, modelTarget(entry))
  else await requestEvidenceRecovery(actions, entry.aggregateId!, modelTarget(entry))
}
const dateFormatter = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
  timeZone: 'UTC', timeZoneName: 'short',
})
function dateLabel(value: string): string {
  const date = new Date(value)
  return Number.isFinite(date.getTime()) ? dateFormatter.format(date) : 'Time unavailable'
}
function sourceLink(value?: string | null): string | undefined {
  if (!value) return undefined
  try {
    const url = new URL(value)
    return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : undefined
  } catch {
    return undefined
  }
}
function dispositionLabel(value?: string | null): string {
  return ({
    'repair-required': 'Repair required; automatic retries paused',
    retryable: 'Automatic retry eligible',
    'collection-disabled': 'Collection disabled; automatic retries paused',
    'in-progress': 'Collection in progress',
    queued: 'Collection queued',
    complete: 'Last collection completed',
    ready: 'Ready for collection',
    cooldown: 'Classification retry cooldown',
    completed: 'Classification attempt completed',
    'unreconstructable-history': 'Historical model evidence cannot be reconstructed',
  } as Record<string, string>)[value ?? ''] ?? 'Recovery status unavailable'
}

function retryTimeLabel(entry: PerformanceExclusion): string | undefined {
  if (['repair-required', 'collection-disabled'].includes(entry.recoveryDisposition ?? '')) return undefined
  if (entry.recoveryDisposition === 'complete') return undefined
  if (entry.recoveryDisposition === 'queued') return 'Queued / eligible for a background sweep from'
  return currentDisposition(entry) === 'in-progress' ? 'Observation lease expires' : 'Automatic retry eligible after'
}

function refreshReason(value: string): string {
  return ({
    SourceCoordinatesUnavailable: 'Source coordinates unavailable',
    ProviderConnectionUnavailable: 'Provider connection unavailable',
    ProviderReadFailed: 'Provider read failed',
    IncompleteEnumeration: 'Thread collection incomplete',
    OwnershipUnavailable: 'Source ownership unavailable',
    ProviderIdentityUnavailable: 'Provider identity unavailable',
    EvidenceRetentionFailed: 'Evidence could not be retained',
    ClassificationUnavailable: 'Classification unavailable',
    ProcessingDeadlineExceeded: 'Collection time limit exceeded',
    HistoricalCapabilityUnsupported: 'Historical collection is unsupported by this provider',
    ObserverUnavailable: 'Historical collection is unavailable for this source',
    CollectionDisabled: 'Collection was disabled at the last attempt',
    Unknown: 'The previous collection attempt did not complete',
  } as Record<string, string>)[value] ?? value
}
</script>

<style scoped>
.performance-exclusions {
  width: min(1260px, calc(100vw - 2rem));
  max-width: calc(100vw - 2rem);
  max-height: calc(100dvh - 2rem);
  margin: auto;
  box-sizing: border-box;
  overflow: auto;
  padding: 1.25rem;
  color: var(--color-text);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
}

.performance-exclusions::backdrop {
  background: var(--color-bg);
  opacity: 0.82;
}

.performance-exclusions__header, .performance-exclusions__footer, nav {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

h2 {
  margin: 0;
  font-size: 1.2rem;
}

.performance-exclusions__header p {
  margin: 0.5rem 0 0;
}

.performance-exclusions__note, small {
  color: var(--color-text-muted);
}

.performance-exclusions__note {
  line-height: 1.6;
  font-size: 0.85rem;
}

.performance-exclusions__scroll {
  overflow: auto;
  margin: 1rem 0;
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.8rem;
  line-height: 1.6;
}

caption {
  text-align: left;
  padding-bottom: 0.75rem;
  color: var(--color-text-muted);
}

th, td {
  text-align: left;
  vertical-align: top;
  padding: 0.75rem;
  border-bottom: 1px solid var(--color-border);
  overflow-wrap: anywhere;
}

thead th {
  background: var(--color-surface-raised);
}

td {
  min-width: 125px;
}

td > span, td > strong, td > small, th small {
  display: block;
}

td:last-child {
  min-width: 180px;
}

ul {
  margin: 0;
  padding-left: 1rem;
}

a {
  color: var(--color-accent);
}

button {
  min-height: 44px;
}

td button {
  margin-top: 0.75rem;
}

[role='alert'] {
  color: var(--color-danger);
}

[role='status'] {
  color: var(--color-success);
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

@media (max-width: 640px) {
  .performance-exclusions {
    padding: 1rem;
  }

  table, tbody {
    display: block;
  }

  caption {
    display: block;
  }

  thead {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }

  tr {
    display: grid;
    grid-template-columns: 1fr;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    margin-bottom: 1rem;
  }

  th, td, td:last-child {
    display: block;
    min-width: 0;
    padding: 0.75rem;
  }

  td:last-child {
    border-bottom: 0;
  }

  th::before, td::before {
    content: attr(data-label);
    display: block;
    font-weight: 600;
    color: var(--color-text-muted);
    margin-bottom: 0.25rem;
  }

}
</style>
