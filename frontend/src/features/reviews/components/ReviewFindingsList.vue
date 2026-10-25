// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Published findings for one review job, with a lens filter.

  The toggle filters the findings this review already published by the pass lens that produced them —
  it does not re-run the review. Lenses are assigned by the runtime (triage / security floor), never
  by the submit request, so there is no lens parameter to send.

  Security = the security-specialist ('security') and knowledge-guided ('prorv') passes.
  Quality  = ordinary passes (no lens) and the defect inventory ('inventory').
  All      = everything the review published.

  Each finding can be dismissed (stored as an admin-dismissed memory record; similar findings are
  suppressed in future reviews) or drafted into a fix proposal for the Fix Queue.
-->

<template>
    <section class="findings-list" aria-label="Published findings">
        <div class="lens-toggle" role="group" aria-label="Filter findings by pass lens">
            <button
                v-for="option in lensOptions"
                :key="option.value"
                type="button"
                class="lens-option"
                :class="{ 'lens-option--active': activeLens === option.value }"
                :aria-pressed="activeLens === option.value"
                @click="activeLens = option.value"
            >
                {{ option.label }}
                <span class="lens-count">{{ option.count }}</span>
            </button>
        </div>
        <p class="lens-note">
            Filters the findings this review already published by the pass lens that produced them — it does not re-run the review.
        </p>

        <p v-if="findings.length === 0" class="empty-state">This review published no findings.</p>
        <p v-else-if="visibleFindings.length === 0" class="empty-state">
            No {{ activeLens === 'security' ? 'security' : 'quality' }} findings in this review.
        </p>

        <ul v-else class="finding-rows">
            <li
                v-for="finding in visibleFindings"
                :key="finding.key"
                class="finding-row"
                :class="{ 'finding-row--dismissed': dismissedKeys.has(findingKey(finding)) }"
            >
                <div class="finding-main">
                    <div class="finding-head">
                        <span v-if="finding.severity" class="severity-badge" :class="'severity-' + finding.severity.toLowerCase()">
                            {{ finding.severity }}
                        </span>
                        <span class="finding-file" :title="finding.filePath ?? ''">
                            {{ finding.filePath ?? 'Pull request' }}{{ finding.lineNumber != null ? ':' + finding.lineNumber : '' }}
                        </span>
                        <span class="lens-tag" :title="lensLabel(finding.originPassLens)">
                            {{ lensLabel(finding.originPassLens) }}
                        </span>
                        <span v-if="finding.originPassLabel" class="pass-tag">{{ finding.originPassLabel }}</span>
                    </div>
                    <p class="finding-message">{{ finding.message }}</p>
                    <p v-if="dismissedKeys.has(findingKey(finding))" class="dismissed-note" role="status">
                        Dismissed — similar findings will be suppressed in future reviews.
                    </p>
                    <p v-if="dismissErrors.get(findingKey(finding))" class="finding-error" role="alert">
                        {{ dismissErrors.get(findingKey(finding)) }}
                    </p>
                </div>
                <div class="finding-actions">
                    <FixProposalButton
                        v-if="clientId"
                        :client-id="clientId"
                        source="review"
                        :finding-ref="findingRef(finding)"
                        :title="findingTitle(finding)"
                        :file-path="finding.filePath"
                        :line="finding.lineNumber"
                        :remediation="finding.message"
                    />
                    <button
                        v-if="canDismiss && !dismissedKeys.has(findingKey(finding))"
                        type="button"
                        class="btn-ghost dismiss-btn"
                        :disabled="dismissingKeys.has(findingKey(finding))"
                        title="Dismiss this finding. Similar findings will be suppressed in future reviews."
                        @click="dismiss(finding)"
                    >
                        {{ dismissingKeys.has(findingKey(finding)) ? 'Dismissing…' : 'Dismiss' }}
                    </button>
                    <span v-else-if="dismissedKeys.has(findingKey(finding))" class="dismissed-badge">Dismissed</span>
                </div>
            </li>
        </ul>
    </section>
</template>

<script lang="ts" setup>
import { computed, ref } from 'vue'
import { dismissFinding } from '@/services/findingDismissalsService'
import FixProposalButton from '@/features/reviews/components/FixProposalButton.vue'
import {
    countFindingsByLens,
    filterFindingsByLens,
    lensLabel,
    type LensFilter,
    type PublishedFinding,
} from '@/features/reviews/utils/reviewDepth'

const props = withDefaults(defineProps<{
    findings: PublishedFinding[]
    clientId?: string | null
    jobId?: string | null
    canDismiss?: boolean
}>(), {
    clientId: null,
    jobId: null,
    canDismiss: false,
})

const activeLens = ref<LensFilter>('all')
const dismissedKeys = ref(new Set<string>())
const dismissingKeys = ref(new Set<string>())
const dismissErrors = ref(new Map<string, string>())

const counts = computed(() => countFindingsByLens(props.findings))

const lensOptions = computed(() => [
    { value: 'all' as LensFilter, label: 'All', count: counts.value.all },
    { value: 'security' as LensFilter, label: 'Security', count: counts.value.security },
    { value: 'quality' as LensFilter, label: 'Quality', count: counts.value.quality },
])

const visibleFindings = computed(() => filterFindingsByLens(props.findings, activeLens.value))

function findingKey(finding: PublishedFinding): string {
    return finding.key
}

/** Stable per-finding ref so a repeat Draft-fix click addresses the same proposal. */
function findingRef(finding: PublishedFinding): string {
    const job = props.jobId ?? 'unknown-job'
    return `review:${job}:${finding.passIndex}:${finding.filePath ?? ''}:${finding.lineNumber ?? ''}`
}

function findingTitle(finding: PublishedFinding): string {
    const location = finding.filePath
        ? `${finding.filePath}${finding.lineNumber != null ? `:${finding.lineNumber}` : ''}`
        : 'Pull request'
    const firstLine = finding.message.split('\n')[0] ?? finding.message
    const short = firstLine.length > 120 ? firstLine.slice(0, 117) + '…' : firstLine
    return `${short} (${location})`
}

async function dismiss(finding: PublishedFinding): Promise<void> {
    if (!props.clientId) return
    const key = findingKey(finding)
    dismissingKeys.value = new Set([...dismissingKeys.value, key])
    const nextErrors = new Map(dismissErrors.value)
    nextErrors.delete(key)
    dismissErrors.value = nextErrors
    try {
        await dismissFinding(props.clientId, {
            findingMessage: finding.message,
            filePath: finding.filePath,
        })
        dismissedKeys.value = new Set([...dismissedKeys.value, key])
    } catch (err) {
        const errors = new Map(dismissErrors.value)
        errors.set(key, err instanceof Error ? err.message : 'Failed to dismiss finding.')
        dismissErrors.value = errors
    } finally {
        const next = new Set(dismissingKeys.value)
        next.delete(key)
        dismissingKeys.value = next
    }
}
</script>

<style scoped>
.findings-list {
    margin-top: 1rem;
}

.lens-toggle {
    display: inline-flex;
    gap: 0.25rem;
    padding: 0.25rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: rgba(8, 15, 40, 0.55);
}

.lens-option {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.35rem 0.8rem;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text-muted);
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
}

.lens-option:hover {
    color: var(--color-text);
}

.lens-option--active {
    color: var(--color-primary-text);
    background: var(--color-accent);
    border-color: var(--color-accent);
}

.lens-count {
    font-family: var(--font-mono, monospace);
    font-size: 0.8rem;
    font-weight: 700;
    padding: 0 0.35rem;
    border-radius: var(--radius-pill);
    background: rgba(255, 255, 255, 0.12);
}

.lens-option--active .lens-count {
    background: rgba(0, 0, 0, 0.18);
}

.lens-note {
    margin: 0.5rem 0 0;
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.empty-state {
    color: var(--color-text-muted);
    font-style: italic;
    margin: 1rem 0 0;
}

.finding-rows {
    list-style: none;
    margin: 1rem 0 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

.finding-row {
    display: flex;
    gap: 1rem;
    align-items: flex-start;
    padding: 0.85rem 1rem;
    border: 1px solid var(--color-border);
    border-left: 3px solid var(--color-accent);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.02);
}

.finding-row--dismissed {
    opacity: 0.65;
    border-left-color: var(--color-text-muted);
}

.finding-main {
    flex: 1;
    min-width: 0;
}

.finding-head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
}

.severity-badge {
    padding: 0.1rem 0.5rem;
    border-radius: var(--radius-pill);
    font-size: 0.72rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.03em;
}

.severity-critical,
.severity-high {
    background: rgba(239, 68, 68, 0.15);
    color: var(--color-danger);
}

.severity-medium {
    background: rgba(245, 158, 11, 0.15);
    color: var(--color-warning);
}

.severity-low,
.severity-suggestion {
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
}

.severity-info,
.severity-note {
    background: rgba(161, 161, 170, 0.15);
    color: var(--color-text-muted);
}

.finding-file {
    font-family: var(--font-mono, monospace);
    font-size: 0.82rem;
    color: var(--color-text);
    overflow-wrap: anywhere;
}

.lens-tag,
.pass-tag {
    font-size: 0.72rem;
    font-weight: 600;
    color: var(--color-text-muted);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-pill);
    padding: 0.1rem 0.5rem;
    white-space: nowrap;
}

.lens-tag {
    color: var(--color-warning);
    border-color: rgba(245, 158, 11, 0.4);
}

.finding-message {
    margin: 0.5rem 0 0;
    font-size: 0.9rem;
    line-height: 1.55;
    color: var(--color-text);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}

.dismissed-note {
    margin: 0.5rem 0 0;
    font-size: 0.82rem;
    color: var(--color-success);
}

.dismissed-badge {
    font-size: 0.78rem;
    font-weight: 600;
    color: var(--color-success);
    border: 1px solid rgba(34, 197, 94, 0.4);
    border-radius: var(--radius-pill);
    padding: 0.25rem 0.6rem;
    white-space: nowrap;
}

.finding-error {
    margin: 0.5rem 0 0;
    font-size: 0.82rem;
    color: var(--color-danger);
}

.finding-actions {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 0.5rem;
    flex-shrink: 0;
}

.dismiss-btn {
    font-size: 0.78rem;
    padding: 0.25rem 0.6rem;
    color: var(--color-text-muted);
    border: 1px solid var(--color-border);
    white-space: nowrap;
}

.dismiss-btn:hover:not(:disabled) {
    color: var(--color-warning);
    background: rgba(245, 158, 11, 0.08);
    border-color: rgba(245, 158, 11, 0.5);
}

.dismiss-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

@media (max-width: 720px) {
    .finding-row {
        flex-direction: column;
    }

    .finding-actions {
        flex-direction: row;
        align-items: center;
    }
}
</style>
