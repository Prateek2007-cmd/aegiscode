// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Fix Queue: the human gate for fix proposals.

  Lists the proposals for one client (GET /clients/{clientId}/fix-proposals) with Approve / Reject
  controls and an optional reason, which is recorded with the decision for audit. An approved proposal
  shows its full fix guidance (remediation text, confidence, file/line) with "Copy fix guidance" and
  "Download .md" actions, and says honestly: "Approved — fix guidance ready to apply. Draft-PR
  creation is not wired yet." No proposal ever claims a pull request was created.

  When the fix-proposals API is not deployed yet the server answers 404 and this section says so
  plainly instead of showing an empty queue. When no client is selected, an administrator picks one
  from the clients they administer.
-->

<template>
    <section class="fix-queue" aria-label="Fix queue">
        <div class="fix-queue-head">
            <h3 class="fix-queue-title">Fix Queue</h3>
            <p class="fix-queue-sub">
                Drafted fixes waiting for a human decision. Approval releases the fix guidance — it does not create a pull request.
            </p>
        </div>

        <div v-if="!effectiveClientId" class="client-picker">
            <label class="client-picker-label" for="fix-queue-client">Client</label>
            <select
                id="fix-queue-client"
                v-model="selectedClientId"
                class="client-picker-select"
                :disabled="clientsLoading"
                @change="loadProposals"
            >
                <option value="">Select a client…</option>
                <option v-for="client in clients" :key="client.id" :value="client.id">
                    {{ client.label }}
                </option>
            </select>
            <p v-if="clientsLoading" class="loading">Loading clients…</p>
            <p v-else-if="clientsError" class="error" role="alert">{{ clientsError }}</p>
            <p v-else-if="clients.length === 0" class="empty-state">
                No administered clients found. The Fix Queue is per-client; an administrator picks the client whose proposals to review.
            </p>
        </div>

        <template v-else>
            <p v-if="loading" class="loading">Loading fix proposals…</p>
            <p v-else-if="unavailable" class="unavailable" role="status">
                Fix proposals need the API update — not available yet. The queue will appear here once the fix-proposals API is deployed.
            </p>
            <p v-else-if="error" class="error" role="alert">{{ error }}</p>
            <p v-else-if="proposals.length === 0" class="empty-state">
                No fix proposals for this client yet. Draft one from a review finding (Draft fix) or a scan finding.
            </p>

            <ul v-else class="proposal-list">
                <li
                    v-for="proposal in proposals"
                    :key="proposal.id"
                    class="proposal-card"
                    :class="'proposal-card--' + proposal.status.toLowerCase()"
                >
                    <div class="proposal-head">
                        <span class="proposal-source" :class="'proposal-source--' + proposal.source">
                            {{ proposal.source === 'review' ? 'Review' : 'Scan' }}
                        </span>
                        <span class="proposal-title">{{ proposal.title }}</span>
                        <span class="status-badge" :class="'status-' + proposal.status.toLowerCase()">
                            {{ statusLabel(proposal.status) }}
                        </span>
                    </div>
                    <p class="proposal-meta">
                        <span v-if="proposal.filePath" class="mono">{{ proposal.filePath }}{{ proposal.line != null ? ':' + proposal.line : '' }}</span>
                        <span v-if="proposal.confidence != null">Confidence {{ proposal.confidence }}</span>
                        <span>Created {{ formatDate(proposal.createdAt) }}</span>
                        <span v-if="proposal.decidedAt">Decided {{ formatDate(proposal.decidedAt) }}</span>
                    </p>
                    <p v-if="proposal.decisionReason" class="proposal-reason">
                        Decision note: {{ proposal.decisionReason }}
                    </p>

                    <div v-if="proposal.status === 'Approved'" class="approved-guidance">
                        <p class="approved-note">
                            Approved — fix guidance ready to apply. Draft-PR creation is not wired yet.
                        </p>
                        <p v-if="proposal.remediation" class="guidance-text">{{ proposal.remediation }}</p>
                        <p v-else class="guidance-text guidance-text--empty">No remediation text was recorded for this proposal.</p>
                        <div class="guidance-actions">
                            <button type="button" class="btn-ghost guidance-btn" @click="copyGuidance(proposal)">
                                {{ copiedId === proposal.id ? 'Copied ✓' : 'Copy fix guidance' }}
                            </button>
                            <button type="button" class="btn-ghost guidance-btn" @click="downloadFixGuidance(proposal)">
                                Download .md
                            </button>
                        </div>
                    </div>

                    <div v-if="proposal.status === 'PendingApproval'" class="decision-row">
                        <input
                            v-model="reasons[proposal.id]"
                            type="text"
                            class="reason-input"
                            :placeholder="'Reason (optional, recorded with the decision)'"
                            :aria-label="'Reason for deciding proposal ' + proposal.title"
                            :disabled="decidingIds.has(proposal.id)"
                        />
                        <button
                            type="button"
                            class="btn-ghost approve-btn"
                            :disabled="decidingIds.has(proposal.id)"
                            @click="decide(proposal, 'approve')"
                        >
                            {{ decidingIds.has(proposal.id) ? 'Saving…' : 'Approve' }}
                        </button>
                        <button
                            type="button"
                            class="btn-ghost reject-btn"
                            :disabled="decidingIds.has(proposal.id)"
                            @click="decide(proposal, 'reject')"
                        >
                            {{ decidingIds.has(proposal.id) ? 'Saving…' : 'Reject' }}
                        </button>
                    </div>
                    <p v-if="decisionErrors.get(proposal.id)" class="error decision-error" role="alert">
                        {{ decisionErrors.get(proposal.id) }}
                    </p>
                </li>
            </ul>
        </template>
    </section>
</template>

<script lang="ts" setup>
import { computed, onMounted, ref, watch } from 'vue'
import {
    approveFixProposal,
    downloadFixGuidance,
    fixGuidanceMarkdown,
    isFixProposalsUnavailable,
    listFixProposals,
    rejectFixProposal,
    type FixProposal,
    type FixProposalStatus,
} from '@/services/fixProposalsService'
import { listPerformanceClients, type PerformanceClient } from '@/services/reviewerPerformanceService'

const props = withDefaults(defineProps<{
    clientId?: string | null
}>(), {
    clientId: null,
})

const selectedClientId = ref('')
const effectiveClientId = computed(() => props.clientId ?? (selectedClientId.value || null))

const clients = ref<PerformanceClient[]>([])
const clientsLoading = ref(false)
const clientsError = ref('')

const proposals = ref<FixProposal[]>([])
const loading = ref(false)
const error = ref('')
const unavailable = ref(false)

const reasons = ref<Record<string, string>>({})
const decidingIds = ref(new Set<string>())
const decisionErrors = ref(new Map<string, string>())
const copiedId = ref<string | null>(null)

function statusLabel(status: FixProposalStatus): string {
    switch (status) {
        case 'PendingApproval': return 'Pending approval'
        case 'Approved': return 'Approved'
        case 'Rejected': return 'Rejected'
        default: return status
    }
}

function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}

async function loadClients(): Promise<void> {
    if (props.clientId) return
    clientsLoading.value = true
    clientsError.value = ''
    try {
        clients.value = await listPerformanceClients()
    } catch (err) {
        clientsError.value = err instanceof Error ? err.message : 'Clients could not be loaded.'
    } finally {
        clientsLoading.value = false
    }
}

async function loadProposals(): Promise<void> {
    const clientId = effectiveClientId.value
    if (!clientId) return
    loading.value = true
    error.value = ''
    unavailable.value = false
    try {
        proposals.value = await listFixProposals(clientId)
    } catch (err) {
        if (isFixProposalsUnavailable(err)) {
            unavailable.value = true
        } else {
            error.value = err instanceof Error ? err.message : 'Failed to load fix proposals.'
        }
    } finally {
        loading.value = false
    }
}

async function decide(proposal: FixProposal, decision: 'approve' | 'reject'): Promise<void> {
    const clientId = effectiveClientId.value
    if (!clientId) return
    decidingIds.value = new Set([...decidingIds.value, proposal.id])
    const nextErrors = new Map(decisionErrors.value)
    nextErrors.delete(proposal.id)
    decisionErrors.value = nextErrors
    try {
        const reason = reasons.value[proposal.id]?.trim() || undefined
        const updated = decision === 'approve'
            ? await approveFixProposal(clientId, proposal.id, reason)
            : await rejectFixProposal(clientId, proposal.id, reason)
        proposals.value = proposals.value.map((item) => (item.id === proposal.id ? updated : item))
    } catch (err) {
        const errors = new Map(decisionErrors.value)
        errors.set(proposal.id, err instanceof Error ? err.message : 'Failed to record the decision.')
        decisionErrors.value = errors
    } finally {
        const next = new Set(decidingIds.value)
        next.delete(proposal.id)
        decidingIds.value = next
    }
}

async function copyGuidance(proposal: FixProposal): Promise<void> {
    try {
        await navigator.clipboard.writeText(fixGuidanceMarkdown(proposal))
        copiedId.value = proposal.id
        setTimeout(() => {
            if (copiedId.value === proposal.id) copiedId.value = null
        }, 2500)
    } catch {
        // Clipboard is unavailable (permissions, non-secure context): the Download .md action next to it
        // is the fallback, so the failure stays silent rather than alarming.
    }
}

watch(effectiveClientId, () => {
    void loadProposals()
})

onMounted(() => {
    void loadClients()
    void loadProposals()
})
</script>

<style scoped>
.fix-queue {
    padding: 1.25rem 1.5rem;
    border: 1px solid rgba(245, 158, 11, 0.3);
    border-radius: var(--radius-lg);
    background: linear-gradient(180deg, rgba(10, 17, 40, 0.92), rgba(8, 13, 32, 0.96));
}

.fix-queue-head {
    margin-bottom: 1rem;
}

.fix-queue-title {
    margin: 0;
    font-size: 1.05rem;
    font-weight: 700;
    color: var(--color-text);
}

.fix-queue-sub {
    margin: 0.35rem 0 0;
    font-size: 0.85rem;
    color: var(--color-text-muted);
}

.loading,
.empty-state {
    color: var(--color-text-muted);
    font-style: italic;
}

.error {
    color: var(--color-danger);
}

.unavailable {
    padding: 0.85rem 1rem;
    border: 1px solid rgba(245, 158, 11, 0.45);
    border-radius: var(--radius-md);
    background: rgba(245, 158, 11, 0.08);
    color: var(--color-warning);
    font-size: 0.9rem;
}

.client-picker {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    max-width: 24rem;
}

.client-picker-label {
    font-size: 0.82rem;
    font-weight: 600;
    color: var(--color-text-muted);
}

.client-picker-select {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    font-size: 0.9rem;
}

.proposal-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
}

.proposal-card {
    padding: 1rem 1.1rem;
    border: 1px solid var(--color-border);
    border-left: 3px solid var(--color-warning);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.02);
}

.proposal-card--approved {
    border-left-color: var(--color-success);
}

.proposal-card--rejected {
    border-left-color: var(--color-text-muted);
    opacity: 0.75;
}

.proposal-head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
}

.proposal-source {
    font-size: 0.72rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.03em;
    color: var(--color-accent);
    border: 1px solid rgba(34, 211, 238, 0.4);
    border-radius: var(--radius-pill);
    padding: 0.1rem 0.5rem;
}

.proposal-source--scan {
    color: var(--color-warning);
    border-color: rgba(245, 158, 11, 0.4);
}

.proposal-title {
    flex: 1;
    font-weight: 600;
    color: var(--color-text);
    overflow-wrap: anywhere;
}

.status-badge {
    padding: 0.2rem 0.6rem;
    border-radius: var(--radius-pill);
    font-size: 0.75rem;
    font-weight: 600;
    white-space: nowrap;
}

.status-pendingapproval {
    background: rgba(245, 158, 11, 0.15);
    color: var(--color-warning);
}

.status-approved {
    background: rgba(34, 197, 94, 0.15);
    color: var(--color-success);
}

.status-rejected {
    background: rgba(161, 161, 170, 0.15);
    color: var(--color-text-muted);
}

.proposal-meta {
    display: flex;
    gap: 1rem;
    flex-wrap: wrap;
    margin: 0.55rem 0 0;
    font-size: 0.82rem;
    color: var(--color-text-muted);
}

.mono {
    font-family: var(--font-mono, monospace);
}

.proposal-reason {
    margin: 0.5rem 0 0;
    font-size: 0.85rem;
    font-style: italic;
    color: var(--color-text-muted);
}

.approved-guidance {
    margin-top: 0.85rem;
    padding-top: 0.85rem;
    border-top: 1px solid var(--color-border);
}

.approved-note {
    margin: 0;
    font-size: 0.85rem;
    font-weight: 600;
    color: var(--color-success);
}

.guidance-text {
    margin: 0.6rem 0 0;
    font-size: 0.9rem;
    line-height: 1.6;
    color: var(--color-text);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}

.guidance-text--empty {
    font-style: italic;
    color: var(--color-text-muted);
}

.guidance-actions {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.75rem;
    flex-wrap: wrap;
}

.guidance-btn {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
    color: var(--color-accent);
    border: 1px solid rgba(34, 211, 238, 0.3);
}

.guidance-btn:hover {
    background: rgba(34, 211, 238, 0.1);
    border-color: rgba(34, 211, 238, 0.55);
}

.decision-row {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.85rem;
    flex-wrap: wrap;
}

.reason-input {
    flex: 1;
    min-width: 12rem;
    padding: 0.4rem 0.65rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    font-size: 0.85rem;
}

.approve-btn {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
    color: var(--color-success);
    border: 1px solid rgba(34, 197, 94, 0.4);
}

.approve-btn:hover:not(:disabled) {
    background: rgba(34, 197, 94, 0.1);
    border-color: rgba(34, 197, 94, 0.65);
}

.reject-btn {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
    color: var(--color-danger);
    border: 1px solid rgba(239, 68, 68, 0.35);
}

.reject-btn:hover:not(:disabled) {
    background: rgba(239, 68, 68, 0.1);
    border-color: rgba(239, 68, 68, 0.6);
}

.approve-btn:disabled,
.reject-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.decision-error {
    margin: 0.5rem 0 0;
    font-size: 0.85rem;
}

@media (max-width: 720px) {
    .fix-queue {
        padding: 1rem;
    }
}
</style>
