// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Review depth panel: what one review actually did, from real data only.

  - Files reviewed / in scope, total in/out tokens, estimated cost, model, duration.
  - Passes run, with per-pass tokens and iterations.
  - The verification funnel (candidates → published / withheld for weak evidence / summary-only),
    parsed from the final-gate record in the protocol events. When the record is absent the panel
    says "verification data not recorded for this job" — it never invents zeros.
  - The published findings, with the lens filter and per-finding Draft fix / Dismiss actions.

  Mounted lazily (only once opened) by its parents, so a history page with many jobs does not fetch a
  protocol trace per row.
-->

<template>
    <section class="depth-panel" aria-label="Review depth">
        <div class="depth-head">
            <h4 class="depth-title">Review depth</h4>
            <span class="depth-sub">What this review actually did — measured, not estimated.</span>
        </div>

        <p v-if="loading" class="loading">Loading review depth…</p>
        <p v-else-if="error" class="error" role="alert">{{ error }}</p>

        <template v-else-if="detail">
            <div class="depth-numerals">
                <div class="numeral">
                    <span class="numeral-value">{{ filesLabel }}</span>
                    <span class="numeral-label">Files reviewed</span>
                </div>
                <div class="numeral">
                    <span class="numeral-value">{{ formatTokenCount(detail.totalInputTokens) }}</span>
                    <span class="numeral-label">Input tokens</span>
                </div>
                <div class="numeral">
                    <span class="numeral-value">{{ formatTokenCount(detail.totalOutputTokens) }}</span>
                    <span class="numeral-label">Output tokens</span>
                </div>
                <div class="numeral">
                    <span class="numeral-value">{{ formatUsdAmount(totalCostUsd, detail.costIsApproximate) }}</span>
                    <span class="numeral-label">Est. cost</span>
                </div>
                <div class="numeral">
                    <span class="numeral-value">{{ durationLabel }}</span>
                    <span class="numeral-label">Duration</span>
                </div>
                <div class="numeral">
                    <span class="numeral-value numeral-value--model">{{ detail.aiModel ?? '—' }}</span>
                    <span class="numeral-label">Model</span>
                </div>
            </div>

            <div v-if="tokenRows.length > 0" class="token-breakdown-mini">
                <div v-for="row in tokenRows" :key="row.modelId" class="token-row">
                    <span class="token-model">{{ row.modelId }}</span>
                    <span class="token-nums">
                        {{ formatTokenCount(row.totalInputTokens) }} in / {{ formatTokenCount(row.totalOutputTokens) }} out
                    </span>
                    <span v-if="row.estimatedCostUsd != null" class="token-cost">
                        {{ formatUsdAmount(row.estimatedCostUsd, row.costIsApproximate) }}
                    </span>
                </div>
                <p v-if="detail.breakdownConsistent === false" class="breakdown-warning">
                    The per-tier token breakdown does not add up to the job totals.
                </p>
            </div>

            <div class="funnel-block">
                <h5 class="block-title">Verification funnel</h5>
                <template v-if="funnel">
                    <div class="funnel" role="list" aria-label="Verification funnel">
                        <div class="funnel-stage funnel-stage--candidates" role="listitem">
                            <span class="funnel-value">{{ funnelNumber(funnel.candidateCount) }}</span>
                            <span class="funnel-label">Candidates</span>
                            <span class="funnel-bar" :style="{ width: funnelWidth(funnel.candidateCount) }"></span>
                        </div>
                        <div class="funnel-arrow" aria-hidden="true">↓</div>
                        <div class="funnel-outcomes">
                            <div class="funnel-stage funnel-stage--published" role="listitem">
                                <span class="funnel-value">{{ funnelNumber(funnel.publishCount) }}</span>
                                <span class="funnel-label">Published</span>
                                <span class="funnel-bar" :style="{ width: funnelWidth(funnel.publishCount) }"></span>
                            </div>
                            <div class="funnel-stage funnel-stage--withheld" role="listitem">
                                <span class="funnel-value">{{ funnelNumber(funnel.dropCount) }}</span>
                                <span class="funnel-label">Withheld — weak evidence</span>
                                <span class="funnel-bar" :style="{ width: funnelWidth(funnel.dropCount) }"></span>
                            </div>
                            <div class="funnel-stage funnel-stage--summary" role="listitem">
                                <span class="funnel-value">{{ funnelNumber(funnel.summaryOnlyCount) }}</span>
                                <span class="funnel-label">Summary-only</span>
                                <span class="funnel-bar" :style="{ width: funnelWidth(funnel.summaryOnlyCount) }"></span>
                            </div>
                        </div>
                    </div>
                    <p class="funnel-note">
                        Candidates are the findings the review produced; published ones reached the pull request,
                        withheld ones were dropped for weak evidence, and summary-only ones appear in the summary
                        without an inline comment.
                    </p>
                </template>
                <p v-else class="funnel-missing">Verification data not recorded for this job.</p>
            </div>

            <div class="passes-block">
                <h5 class="block-title">Passes run ({{ passes.length }})</h5>
                <p v-if="passes.length === 0" class="empty-state">No passes recorded for this job.</p>
                <table v-else class="passes-table">
                    <thead>
                        <tr>
                            <th>Pass</th>
                            <th>Outcome</th>
                            <th>Input</th>
                            <th>Output</th>
                            <th>Iterations</th>
                            <th>Tool calls</th>
                            <th>Findings</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="pass in passes" :key="pass.id ?? pass.label ?? ''">
                            <td class="pass-label-cell">{{ pass.label ?? 'Pass' }}</td>
                            <td>{{ pass.outcome ?? '—' }}</td>
                            <td class="mono">{{ formatTokenCount(pass.totalInputTokens) }}</td>
                            <td class="mono">{{ formatTokenCount(pass.totalOutputTokens) }}</td>
                            <td class="mono">{{ pass.iterationCount ?? '—' }}</td>
                            <td class="mono">{{ pass.toolCallCount ?? '—' }}</td>
                            <td class="mono">{{ pass.finalComments?.length ?? 0 }}</td>
                        </tr>
                    </tbody>
                </table>
            </div>

            <ReviewFindingsList
                :findings="findings"
                :client-id="clientId"
                :job-id="jobId"
                :can-dismiss="canDismiss"
            />
        </template>
    </section>
</template>

<script lang="ts" setup>
import { computed, onMounted } from 'vue'
import { useJobReviewDepth } from '@/features/reviews/composables/useJobReviewDepth'
import ReviewFindingsList from '@/features/reviews/components/ReviewFindingsList.vue'
import {
    formatDurationMs,
    formatTokenCount,
    formatUsdAmount,
} from '@/features/reviews/utils/reviewDepth'

const props = withDefaults(defineProps<{
    jobId: string | null
    clientId?: string | null
    canDismiss?: boolean
}>(), {
    clientId: null,
    canDismiss: false,
})

const depth = useJobReviewDepth(props.jobId)

// Destructured to top level: the template auto-unwraps only top-level refs, not refs nested inside
// the composable's return object.
const { loading, error, detail, passes, funnel, findings, totalCostUsd } = depth

const filesLabel = computed(() => {
    const reviewed = depth.detail.value?.filesReviewed
    const inScope = depth.detail.value?.filesInScope
    if (reviewed == null) return '—'
    return inScope != null ? `${reviewed} / ${inScope}` : String(reviewed)
})

const durationLabel = computed(() => {
    const detail = depth.detail.value
    if (!detail) return '—'
    const fromDetail = formatDurationMs(detail.processingStartedAt, detail.completedAt)
    if (fromDetail !== '—') return fromDetail
    const starts = depth.passes.value.map((pass) => pass.startedAt).filter((value): value is string => !!value)
    const ends = depth.passes.value.map((pass) => pass.completedAt).filter((value): value is string => !!value)
    if (starts.length === 0 || ends.length === 0) return '—'
    return formatDurationMs(starts.sort()[0], ends.sort().at(-1))
})

const tokenRows = computed(() => depth.detail.value?.tokenBreakdown ?? [])

function funnelNumber(value: number | undefined): string {
    return value == null ? '—' : value.toLocaleString('en-US')
}

function funnelWidth(value: number | undefined): string {
    const candidates = depth.funnel.value?.candidateCount
    if (value == null || candidates == null || candidates <= 0) return '0%'
    return `${Math.max(2, Math.round((value / candidates) * 100))}%`
}

onMounted(() => {
    void depth.load()
})
</script>

<style scoped>
.depth-panel {
    margin-top: 1rem;
    padding: 1.25rem 1.5rem;
    border: 1px solid rgba(34, 211, 238, 0.25);
    border-radius: var(--radius-lg);
    background:
        linear-gradient(180deg, rgba(10, 17, 40, 0.92), rgba(8, 13, 32, 0.96));
}

.depth-head {
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
    flex-wrap: wrap;
    margin-bottom: 1rem;
}

.depth-title {
    margin: 0;
    font-size: 1rem;
    font-weight: 700;
    color: var(--color-text);
}

.depth-sub {
    font-size: 0.82rem;
    color: var(--color-text-muted);
}

.loading,
.error,
.empty-state {
    color: var(--color-text-muted);
    font-style: italic;
}

.error {
    color: var(--color-danger);
    font-style: normal;
}

.depth-numerals {
    display: flex;
    flex-wrap: wrap;
    gap: 1.5rem 2.5rem;
    padding-bottom: 1rem;
    border-bottom: 1px solid var(--color-border);
}

.numeral {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    min-width: 7rem;
}

.numeral-value {
    font-family: var(--font-mono, monospace);
    font-size: 1.9rem;
    font-weight: 700;
    line-height: 1.1;
    color: var(--color-accent);
    overflow-wrap: anywhere;
}

.numeral-value--model {
    font-size: 1.05rem;
    line-height: 1.4;
    padding-top: 0.4rem;
    color: var(--color-text);
}

.numeral-label {
    font-size: 0.75rem;
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    color: var(--color-text-muted);
}

.token-breakdown-mini {
    margin-top: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
}

.token-row {
    display: flex;
    gap: 1rem;
    align-items: baseline;
    font-size: 0.85rem;
    flex-wrap: wrap;
}

.token-model {
    font-family: var(--font-mono, monospace);
    color: var(--color-text);
}

.token-nums,
.token-cost {
    font-family: var(--font-mono, monospace);
    color: var(--color-text-muted);
}

.breakdown-warning {
    margin: 0.25rem 0 0;
    font-size: 0.82rem;
    color: var(--color-warning);
}

.block-title {
    margin: 1.25rem 0 0.75rem;
    font-size: 0.9rem;
    font-weight: 700;
    color: var(--color-text);
}

.funnel {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    max-width: 42rem;
}

.funnel-stage {
    position: relative;
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
    padding: 0.55rem 0.85rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.02);
    overflow: hidden;
}

.funnel-stage--candidates {
    border-color: rgba(34, 211, 238, 0.45);
}

.funnel-stage--published {
    border-color: rgba(34, 197, 94, 0.45);
}

.funnel-stage--withheld {
    border-color: rgba(245, 158, 11, 0.5);
}

.funnel-stage--summary {
    border-color: rgba(161, 161, 170, 0.45);
}

.funnel-value {
    font-family: var(--font-mono, monospace);
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--color-text);
    min-width: 3.5rem;
}

.funnel-stage--candidates .funnel-value { color: var(--color-accent); }
.funnel-stage--published .funnel-value { color: var(--color-success); }
.funnel-stage--withheld .funnel-value { color: var(--color-warning); }

.funnel-label {
    font-size: 0.88rem;
    font-weight: 600;
    color: var(--color-text);
    z-index: 1;
}

.funnel-bar {
    position: absolute;
    left: 0;
    bottom: 0;
    height: 3px;
    background: var(--color-accent);
    opacity: 0.7;
}

.funnel-stage--published .funnel-bar { background: var(--color-success); }
.funnel-stage--withheld .funnel-bar { background: var(--color-warning); }
.funnel-stage--summary .funnel-bar { background: var(--color-text-muted); }

.funnel-arrow {
    text-align: center;
    color: var(--color-text-muted);
    line-height: 1;
}

.funnel-outcomes {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
    gap: 0.5rem;
}

.funnel-note {
    margin: 0.75rem 0 0;
    font-size: 0.82rem;
    line-height: 1.5;
    color: var(--color-text-muted);
    max-width: 42rem;
}

.funnel-missing {
    margin: 0;
    font-size: 0.88rem;
    font-style: italic;
    color: var(--color-text-muted);
}

.passes-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.86rem;
}

.passes-table th,
.passes-table td {
    text-align: left;
    padding: 0.45rem 0.6rem;
    border-bottom: 1px solid var(--color-border);
}

.passes-table th {
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
}

.passes-table td {
    color: var(--color-text);
}

.mono {
    font-family: var(--font-mono, monospace);
}

.pass-label-cell {
    font-family: var(--font-mono, monospace);
    overflow-wrap: anywhere;
}

@media (max-width: 720px) {
    .depth-panel {
        padding: 1rem;
    }

    .numeral-value {
        font-size: 1.5rem;
    }

    .passes-table {
        display: block;
        overflow-x: auto;
    }
}

@media (prefers-reduced-motion: reduce) {
    .depth-panel,
    .funnel-stage {
        transition: none;
    }
}
</style>
