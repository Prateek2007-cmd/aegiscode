// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Fix Queue: the human gate for fix proposals.

  Lists the proposals for one client (GET /clients/{clientId}/fix-proposals) with Approve / Reject
  controls and an optional reason, which is recorded with the decision for audit. An approved proposal
  shows its full fix guidance (remediation text, confidence, file/line) with "Copy fix guidance" and
  "Download .md" actions, plus "Create draft PR": when the proposal stored the exact replacement
  (repository, line range, original and replacement code) one click opens a real draft pull request
  on GitHub; otherwise an inline form collects the missing patch details first. The created PR's URL
  is shown on the proposal afterwards, and a failed attempt shows GitHub's own error — no proposal
  ever claims a pull request was created when it was not.

  When the fix-proposals API is not deployed yet the server answers 404 and this section says so
  plainly instead of showing an empty queue. When no client is selected, an administrator picks one
  from the clients they administer.
-->

<template>
    <section class="fix-queue" aria-label="Fix queue">
        <div class="fix-queue-head">
            <h3 class="fix-queue-title">Fix Queue</h3>
            <p class="fix-queue-sub">
                Drafted fixes waiting for a human decision. Approval releases the fix guidance — and an approved proposal can open a draft pull request on GitHub.
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
                            Approved — fix guidance ready to apply.
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

                        <div class="draft-pr">
                            <p v-if="proposal.draftPrUrl" class="draft-pr-link" role="status">
                                Draft PR:
                                <a :href="proposal.draftPrUrl" target="_blank" rel="noopener">{{ proposal.draftPrUrl }}</a>
                                <span v-if="proposal.draftPrBranch" class="mono">({{ proposal.draftPrBranch }})</span>
                            </p>
                            <p v-if="proposal.draftPrUrl && draftPrNotes.get(proposal.id)" class="draft-pr-note">
                                {{ draftPrNotes.get(proposal.id) }}
                            </p>
                            <template v-else>
                                <button
                                    type="button"
                                    class="btn-ghost draft-pr-btn"
                                    :disabled="creatingDraftPrIds.has(proposal.id)"
                                    @click="startDraftPr(proposal)"
                                >
                                    {{ creatingDraftPrIds.has(proposal.id) ? 'Creating draft PR…' : 'Create draft PR' }}
                                </button>
                                <form
                                    v-if="draftPrForms[proposal.id]"
                                    class="draft-pr-form"
                                    @submit.prevent="submitDraftPr(proposal)"
                                >
                                    <p class="draft-pr-form-note">
                                        This proposal did not store the full patch, so fill in what is missing. The replacement is
                                        verified against the file at the base before anything is created.
                                    </p>
                                    <div class="draft-pr-fields">
                                        <label class="draft-pr-field">
                                            Repository owner
                                            <input v-model="draftPrForms[proposal.id]!.repositoryOwner" type="text" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field">
                                            Repository name
                                            <input v-model="draftPrForms[proposal.id]!.repositoryName" type="text" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field">
                                            Base branch <span class="field-hint">(blank = default branch)</span>
                                            <input v-model="draftPrForms[proposal.id]!.baseRef" type="text" :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field">
                                            Base commit SHA <span class="field-hint">(optional, pins the branch point)</span>
                                            <input v-model="draftPrForms[proposal.id]!.baseSha" type="text" :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field draft-pr-field--wide">
                                            File path
                                            <input v-model="draftPrForms[proposal.id]!.filePath" type="text" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field">
                                            Start line
                                            <input v-model="draftPrForms[proposal.id]!.startLine" type="number" min="1" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field">
                                            End line
                                            <input v-model="draftPrForms[proposal.id]!.endLine" type="number" min="1" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field draft-pr-field--wide">
                                            Original lines (exact text at the base)
                                            <textarea v-model="draftPrForms[proposal.id]!.originalCode" rows="3" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                        <label class="draft-pr-field draft-pr-field--wide">
                                            Replacement code
                                            <textarea v-model="draftPrForms[proposal.id]!.suggestedCode" rows="3" required :disabled="creatingDraftPrIds.has(proposal.id)" />
                                        </label>
                                    </div>
                                    <button
                                        type="submit"
                                        class="btn-ghost draft-pr-submit"
                                        :disabled="creatingDraftPrIds.has(proposal.id)"
                                    >
                                        {{ creatingDraftPrIds.has(proposal.id) ? 'Creating draft PR…' : 'Open draft PR' }}
                                    </button>
                                </form>
                                <p v-if="draftPrErrors.get(proposal.id)" class="error draft-pr-error" role="alert">
                                    {{ draftPrErrors.get(proposal.id) }}
                                </p>
                            </template>
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
    createDraftPullRequest,
    downloadFixGuidance,
    fixGuidanceMarkdown,
    isFixProposalsUnavailable,
    listFixProposals,
    rejectFixProposal,
    type CreateDraftPullRequestRequest,
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

// Draft-PR state, keyed by proposal id. A form only opens for proposals that did not store the
// full patch; proposals with complete patch data create the PR in one click instead.
interface DraftPrFormValues {
    repositoryOwner: string
    repositoryName: string
    baseRef: string
    baseSha: string
    filePath: string
    startLine: string
    endLine: string
    originalCode: string
    suggestedCode: string
}

const draftPrForms = ref<Record<string, DraftPrFormValues>>({})
const creatingDraftPrIds = ref(new Set<string>())
const draftPrErrors = ref(new Map<string, string>())
const draftPrNotes = ref(new Map<string, string>())

/** Whether the proposal stored everything the draft-PR call needs — the one-click case. */
function hasPatchData(proposal: FixProposal): boolean {
    return Boolean(
        proposal.repositoryOwner?.trim()
        && proposal.repositoryName?.trim()
        && proposal.filePath?.trim()
        && proposal.startLine != null
        && proposal.originalCode?.trim()
        && proposal.suggestedCode?.trim(),
    )
}

function draftPrFormDefaults(proposal: FixProposal): DraftPrFormValues {
    return {
        repositoryOwner: proposal.repositoryOwner ?? '',
        repositoryName: proposal.repositoryName ?? '',
        baseRef: proposal.baseRef ?? '',
        baseSha: '',
        filePath: proposal.filePath ?? '',
        startLine: proposal.startLine != null ? String(proposal.startLine) : (proposal.line != null ? String(proposal.line) : ''),
        endLine: proposal.endLine != null ? String(proposal.endLine) : (proposal.line != null ? String(proposal.line) : ''),
        originalCode: proposal.originalCode ?? '',
        suggestedCode: proposal.suggestedCode ?? '',
    }
}

/** Starts draft-PR creation: one click when the patch is stored, otherwise opens the patch form. */
async function startDraftPr(proposal: FixProposal): Promise<void> {
    if (!hasPatchData(proposal)) {
        draftPrForms.value = { ...draftPrForms.value, [proposal.id]: draftPrFormDefaults(proposal) }
        return
    }

    await createDraftPr(proposal, {})
}

async function submitDraftPr(proposal: FixProposal): Promise<void> {
    const form = draftPrForms.value[proposal.id]
    if (!form) return
    const toLine = (value: string): number | null => {
        const parsed = Number.parseInt(value, 10)
        return Number.isNaN(parsed) ? null : parsed
    }
    const request: CreateDraftPullRequestRequest = {
        repositoryOwner: form.repositoryOwner.trim() || null,
        repositoryName: form.repositoryName.trim() || null,
        baseRef: form.baseRef.trim() || null,
        baseSha: form.baseSha.trim() || null,
        filePath: form.filePath.trim() || null,
        startLine: toLine(form.startLine),
        endLine: toLine(form.endLine),
        originalCode: form.originalCode || null,
        suggestedCode: form.suggestedCode || null,
    }
    await createDraftPr(proposal, request)
    const nextForms = { ...draftPrForms.value }
    delete nextForms[proposal.id]
    draftPrForms.value = nextForms
}

async function createDraftPr(proposal: FixProposal, request: CreateDraftPullRequestRequest): Promise<void> {
    const clientId = effectiveClientId.value
    if (!clientId) return
    creatingDraftPrIds.value = new Set([...creatingDraftPrIds.value, proposal.id])
    const nextErrors = new Map(draftPrErrors.value)
    nextErrors.delete(proposal.id)
    draftPrErrors.value = nextErrors
    try {
        const result = await createDraftPullRequest(clientId, proposal.id, request)
        proposals.value = proposals.value.map((item) => (item.id === proposal.id ? result.proposal : item))
        const notes = new Map(draftPrNotes.value)
        notes.set(proposal.id, result.note ?? `Draft PR created on branch ${result.branch}.`)
        draftPrNotes.value = notes
    } catch (err) {
        const errors = new Map(draftPrErrors.value)
        errors.set(proposal.id, err instanceof Error ? err.message : 'Failed to create the draft pull request.')
        draftPrErrors.value = errors
    } finally {
        const next = new Set(creatingDraftPrIds.value)
        next.delete(proposal.id)
        creatingDraftPrIds.value = next
    }
}

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

.draft-pr {
    margin-top: 0.85rem;
    padding-top: 0.85rem;
    border-top: 1px dashed var(--color-border);
}

.draft-pr-link {
    margin: 0;
    font-size: 0.88rem;
    color: var(--color-text);
    overflow-wrap: anywhere;
}

.draft-pr-link a {
    color: var(--color-accent);
}

.draft-pr-note {
    margin: 0.4rem 0 0;
    font-size: 0.82rem;
    color: var(--color-text-muted);
}

.draft-pr-btn {
    font-size: 0.82rem;
    padding: 0.35rem 0.8rem;
    color: var(--color-success);
    border: 1px solid rgba(34, 197, 94, 0.4);
}

.draft-pr-btn:hover:not(:disabled) {
    background: rgba(34, 197, 94, 0.1);
    border-color: rgba(34, 197, 94, 0.65);
}

.draft-pr-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.draft-pr-form {
    margin-top: 0.75rem;
    padding: 0.85rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.02);
}

.draft-pr-form-note {
    margin: 0 0 0.75rem;
    font-size: 0.83rem;
    color: var(--color-text-muted);
}

.draft-pr-fields {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.6rem;
}

.draft-pr-field {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--color-text-muted);
}

.draft-pr-field--wide {
    grid-column: 1 / -1;
}

.draft-pr-field input,
.draft-pr-field textarea {
    padding: 0.4rem 0.6rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    font-size: 0.85rem;
    font-weight: 400;
    font-family: inherit;
}

.draft-pr-field textarea {
    font-family: var(--font-mono, monospace);
    resize: vertical;
}

.field-hint {
    font-weight: 400;
    font-style: italic;
}

.draft-pr-submit {
    margin-top: 0.75rem;
    font-size: 0.82rem;
    padding: 0.35rem 0.8rem;
    color: var(--color-success);
    border: 1px solid rgba(34, 197, 94, 0.4);
}

.draft-pr-submit:hover:not(:disabled) {
    background: rgba(34, 197, 94, 0.1);
    border-color: rgba(34, 197, 94, 0.65);
}

.draft-pr-submit:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.draft-pr-error {
    margin: 0.5rem 0 0;
    font-size: 0.85rem;
}

@media (max-width: 720px) {
    .fix-queue {
        padding: 1rem;
    }
}
</style>
