// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

<!--
  Job-level diff view for one review job: every file the review looked at, with
  the published findings pinned to their NEW-side lines. The diffs come from the
  job-level endpoint (`GET /reviewing/jobs/{jobId}/diff`), which serves the same
  retained-first per-file diffs the trace view shows — this tab only gathers them
  in one place instead of re-fetching anything from the provider.

  A finding marker sits collapsed under its line; clicking it shows the finding.
  Findings that cannot be pinned (no line, or a line the rendered diff does not
  contain) are listed below the diff rather than dropped. When the job retained
  no diff at all, the tab says so instead of rendering invented lines.
-->

<template>
    <section class="job-diff-tab" data-testid="job-diff-tab" aria-label="Reviewed diff">
        <div v-if="vm.jobDiffLoading" class="job-diff-state" data-testid="job-diff-loading" role="status">
            <p>Loading reviewed diff…</p>
        </div>

        <div v-else-if="vm.jobDiffError" class="job-diff-state job-diff-state--error" data-testid="job-diff-error" role="alert">
            <p class="job-diff-state-title">Diff unavailable</p>
            <p>{{ vm.jobDiffError }}</p>
            <button type="button" class="btn-secondary btn-sm" data-testid="job-diff-error-retry" @click="vm.loadJobDiff()">
                Retry
            </button>
        </div>

        <div v-else-if="diffFiles.length === 0" class="job-diff-state" data-testid="job-diff-empty">
            <p class="job-diff-state-title">No diff retained for this job</p>
            <p>This job reviewed no files with a retained diff, so there is nothing to render here.</p>
        </div>

        <div v-else class="job-diff-layout">
            <aside class="job-diff-files" aria-label="Changed files">
                <p class="job-diff-files-heading">{{ diffFiles.length }} {{ diffFiles.length === 1 ? 'file' : 'files' }}</p>
                <ul class="job-diff-file-list">
                    <li v-for="file in diffFiles" :key="file.filePath ?? ''">
                        <button
                            type="button"
                            class="job-diff-file-btn"
                            :class="{ 'is-active': file.filePath === selectedFilePath }"
                            :aria-current="file.filePath === selectedFilePath"
                            :data-testid="`job-diff-file-${file.filePath}`"
                            @click="selectedFilePath = file.filePath ?? null"
                        >
                            <span class="job-diff-file-path" :title="file.filePath ?? ''">{{ file.filePath }}</span>
                            <span v-if="file.changeType" class="job-diff-file-change">{{ file.changeType }}</span>
                            <span v-if="findingCountFor(file.filePath) > 0" class="job-diff-file-findings" :title="`${findingCountFor(file.filePath)} findings`">
                                {{ findingCountFor(file.filePath) }} ⚠
                            </span>
                        </button>
                    </li>
                </ul>
            </aside>

            <div class="job-diff-detail">
                <JobProtocolDiffViewer
                    :key="selectedFilePath ?? ''"
                    :file-result-id="selectedFilePath"
                    :diff="selectedDiff"
                    :inline-threads="inlineThreads"
                    scroll-mode="content"
                    @update:anchored-ids="onAnchoredIdsChange"
                >
                    <template #thread="{ thread }">
                        <DiffFindingMarker :finding="(thread.payload as DiffFindingPayload)" />
                    </template>
                </JobProtocolDiffViewer>

                <template v-if="unanchoredFindings.length > 0">
                    <h4 class="job-diff-subhead">Findings without a diff line</h4>
                    <p class="job-diff-subhead-note">
                        These findings belong to this file but have no line to pin to, or their line is not in the rendered diff.
                    </p>
                    <ul class="job-diff-unanchored-list">
                        <li v-for="finding in unanchoredFindings" :key="unanchoredKey(finding)">
                            <DiffFindingMarker
                                :finding="{
                                    severity: (finding.severity ?? 'info').toLowerCase(),
                                    message: finding.message ?? '',
                                    lineNumber: finding.lineNumber ?? finding.line_number ?? 0,
                                    filePath: finding.filePath ?? finding.file_path ?? '',
                                }"
                                start-expanded
                            />
                        </li>
                    </ul>
                </template>
            </div>
        </div>
    </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import JobProtocolDiffViewer, { type InlineDiffThread } from './JobProtocolDiffViewer.vue'
import DiffFindingMarker from './DiffFindingMarker.vue'
import {
    countFindingsByFile,
    findingsToInlineThreads,
    unanchoredFindingsForFile,
    type DiffFindingPayload,
} from '../utils/diffFindings'
import type { ProtocolReviewComment } from '../types'

const props = defineProps<{ vm: JobProtocolViewModel }>()

const diffFiles = computed(() => props.vm.jobDiff?.files ?? [])
const findings = computed<ProtocolReviewComment[]>(() => props.vm.diffFindings)
const findingCounts = computed(() => countFindingsByFile(findings.value))

const selectedFilePath = ref<string | null>(null)
const anchoredIds = ref<string[]>([])

function findingCountFor(filePath: string | null | undefined): number {
    if (!filePath) return 0
    return findingCounts.value.get(filePath) ?? 0
}

// Default to the first file that carries findings, else the first file. The
// selection only resets when the job's diff itself changes, not when the user
// picks a file — otherwise every findings refresh would yank them back.
watch(
    diffFiles,
    files => {
        if (files.length === 0) {
            selectedFilePath.value = null
            return
        }
        if (selectedFilePath.value && files.some(file => file.filePath === selectedFilePath.value)) {
            return
        }
        const withFindings = files.find(file => findingCountFor(file.filePath) > 0)
        selectedFilePath.value = (withFindings ?? files[0]).filePath ?? null
    },
    { immediate: true },
)

const selectedDiff = computed(() =>
    diffFiles.value.find(file => file.filePath === selectedFilePath.value) ?? null,
)

const inlineThreads = computed<InlineDiffThread<DiffFindingPayload>[]>(() => {
    if (!selectedFilePath.value) return []
    return findingsToInlineThreads(findings.value, selectedFilePath.value)
})

function onAnchoredIdsChange(ids: string[]): void {
    anchoredIds.value = ids
}

const unanchoredFindings = computed<ProtocolReviewComment[]>(() => {
    if (!selectedFilePath.value) return []
    return unanchoredFindingsForFile(findings.value, selectedFilePath.value, anchoredIds.value)
})

function unanchoredKey(finding: ProtocolReviewComment): string {
    return `${finding.filePath ?? finding.file_path ?? ''}:${finding.lineNumber ?? finding.line_number ?? 0}:${(finding.message ?? '').slice(0, 80)}`
}
</script>

<style scoped>
.job-diff-tab {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    min-width: 0;
}

.job-diff-state {
    padding: 1.1rem 1.25rem;
    border: 1px dashed var(--color-border);
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.02);
    color: var(--color-text-muted);
}

.job-diff-state--error {
    border-color: rgba(239, 68, 68, 0.4);
    background: rgba(239, 68, 68, 0.06);
}

.job-diff-state-title {
    margin: 0 0 0.3rem;
    font-size: 0.95rem;
    font-weight: 600;
    color: var(--color-text);
}

.job-diff-state p {
    margin: 0;
}

.job-diff-layout {
    display: grid;
    grid-template-columns: 17rem minmax(0, 1fr);
    gap: 1rem;
    align-items: start;
}

@media (max-width: 900px) {
    .job-diff-layout {
        grid-template-columns: 1fr;
    }
}

.job-diff-files {
    position: sticky;
    top: 1rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: var(--color-surface);
    padding: 0.75rem;
}

.job-diff-files-heading {
    margin: 0 0 0.5rem;
    padding: 0 0.25rem;
    font-size: 0.75rem;
    font-weight: 700;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--color-text-muted);
}

.job-diff-file-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    max-height: 70vh;
    overflow-y: auto;
}

.job-diff-file-btn {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    padding: 0.5rem 0.6rem;
    background: transparent;
    border: 1px solid transparent;
    border-radius: var(--radius-md);
    color: var(--color-text);
    font: inherit;
    font-size: 0.85rem;
    text-align: left;
    cursor: pointer;
    min-width: 0;
}

.job-diff-file-btn:hover {
    background: rgba(255, 255, 255, 0.03);
}

.job-diff-file-btn.is-active {
    background: rgba(34, 211, 238, 0.08);
    border-color: rgba(34, 211, 238, 0.35);
}

.job-diff-file-path {
    flex: 1 1 auto;
    font-family: var(--font-mono, monospace);
    font-size: 0.8rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.job-diff-file-change {
    flex: 0 0 auto;
    font-size: 0.65rem;
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    padding: 0.1rem 0.45rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
}

.job-diff-file-findings {
    flex: 0 0 auto;
    font-size: 0.75rem;
    font-weight: 700;
    font-family: var(--font-mono, monospace);
    color: var(--color-warning, #f59e0b);
}

.job-diff-detail {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    min-width: 0;
}

.job-diff-subhead {
    margin: 0;
    font-size: 0.95rem;
    font-weight: 600;
    color: var(--color-text);
}

.job-diff-subhead-note {
    margin: 0;
    font-size: 0.85rem;
    color: var(--color-text-muted);
}

.job-diff-unanchored-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}
</style>
