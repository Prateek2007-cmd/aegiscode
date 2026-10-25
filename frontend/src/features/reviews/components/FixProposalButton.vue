// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Reusable "Draft fix" button.

  Turns one finding into a fix proposal (POST /clients/{clientId}/fix-proposals) that waits in the Fix
  Queue as PendingApproval until an administrator approves or rejects it. Approval does NOT create a
  pull request — draft-PR creation is not wired yet, and the queue says so.

  Wiring for other features (e.g. the security-scans finding rows): render this button with the
  finding's stable ref and a short title. findingRef must be stable per finding so a repeat click
  drafts the same proposal instead of a duplicate. The button handles the "API not deployed yet"
  state itself and shows it honestly instead of failing silently.

  <FixProposalButton
      client-id="…"
      source="scan"
      finding-ref="scan:…:…"
      title="Short finding title"
      file-path="src/foo.ts"
      :line="42"
      remediation="…"
      confidence="high"
      @created="onProposalCreated"
  />
-->

<template>
    <span class="fix-proposal-button">
        <button
            type="button"
            class="btn-ghost draft-fix-btn"
            :class="{ 'draft-fix-btn--created': state === 'created' }"
            :disabled="busy || state === 'created'"
            :title="buttonTitle"
            @click="draftFix"
        >
            {{ buttonLabel }}
        </button>
        <span v-if="state === 'created'" class="draft-fix-note" role="status">
            Fix proposed — PendingApproval in the Fix Queue.
        </span>
        <span v-else-if="state === 'unavailable'" class="draft-fix-note draft-fix-note--warn" role="status">
            Fix proposals need the API update — not available yet.
        </span>
        <span v-else-if="state === 'error'" class="draft-fix-note draft-fix-note--error" role="alert">
            {{ errorMessage }}
        </span>
    </span>
</template>

<script lang="ts" setup>
import { computed, ref } from 'vue'
import {
    buildFixProposalRequest,
    createFixProposal,
    isFixProposalsUnavailable,
    type FixProposal,
    type FixProposalSource,
} from '@/services/fixProposalsService'

const props = defineProps<{
    clientId: string
    source: FixProposalSource
    findingRef: string
    title: string
    filePath?: string | null
    line?: number | null
    remediation?: string | null
    confidence?: string | null
}>()

const emit = defineEmits<{
    created: [proposal: FixProposal]
}>()

type ButtonState = 'idle' | 'busy' | 'created' | 'unavailable' | 'error'

const state = ref<ButtonState>('idle')
const errorMessage = ref('')
const busy = computed(() => state.value === 'busy')

const buttonLabel = computed(() => {
    switch (state.value) {
        case 'busy': return 'Drafting fix…'
        case 'created': return 'Fix proposed ✓'
        case 'unavailable': return 'Draft fix'
        default: return 'Draft fix'
    }
})

const buttonTitle = computed(() => {
    if (state.value === 'created') return 'This finding already has a fix proposal waiting in the Fix Queue.'
    return 'Draft a fix proposal for this finding. It waits in the Fix Queue for approval — no pull request is created.'
})

async function draftFix(): Promise<void> {
    if (busy.value || state.value === 'created') return
    state.value = 'busy'
    errorMessage.value = ''
    try {
        const proposal = await createFixProposal(props.clientId, buildFixProposalRequest({
            findingRef: props.findingRef,
            source: props.source,
            title: props.title,
            filePath: props.filePath ?? null,
            line: props.line ?? null,
            remediation: props.remediation ?? null,
            confidence: props.confidence ?? null,
        }))
        state.value = 'created'
        emit('created', proposal)
    } catch (err) {
        if (isFixProposalsUnavailable(err)) {
            state.value = 'unavailable'
        } else {
            state.value = 'error'
            errorMessage.value = err instanceof Error ? err.message : 'Failed to draft a fix proposal.'
        }
    }
}
</script>

<style scoped>
.fix-proposal-button {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
}

.draft-fix-btn {
    font-size: 0.78rem;
    padding: 0.25rem 0.6rem;
    color: var(--color-accent);
    border: 1px solid rgba(34, 211, 238, 0.3);
    white-space: nowrap;
}

.draft-fix-btn:hover:not(:disabled) {
    background: rgba(34, 211, 238, 0.1);
    border-color: rgba(34, 211, 238, 0.55);
}

.draft-fix-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.draft-fix-btn--created {
    color: var(--color-success);
    border-color: rgba(34, 197, 94, 0.4);
}

.draft-fix-note {
    font-size: 0.78rem;
    color: var(--color-text-muted);
}

.draft-fix-note--warn {
    color: var(--color-warning);
}

.draft-fix-note--error {
    color: var(--color-danger);
}
</style>
