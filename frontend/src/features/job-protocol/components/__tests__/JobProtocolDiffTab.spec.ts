// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { defineComponent, h, reactive, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import JobProtocolDiffTab from '../JobProtocolDiffTab.vue'
import type { FileDiffDto, JobDiffDto, ProtocolReviewComment } from '../../types'
import type { JobProtocolViewModel } from '../../composables/useJobProtocolViewModel'

// The real viewer renders diffs through diff2html; the tab test only needs to know
// which file and threads it was handed, and to exercise the thread slot. The stub
// renders each thread through the slot so the real finding markers mount.
const DiffViewerStub = defineComponent({
    name: 'JobProtocolDiffViewer',
    props: {
        diff: { type: Object, default: null },
        inlineThreads: { type: Array, default: () => [] },
        fileResultId: { type: String, default: null },
    },
    emits: ['update:anchored-ids'],
    setup(props, { emit, slots }) {
        return () =>
            h('div', { 'data-testid': 'diff-viewer-stub', 'data-file': props.fileResultId }, [
                h('span', { 'data-testid': 'stub-diff-path' }, (props.diff as FileDiffDto | null)?.filePath ?? ''),
                h('button', {
                    'data-testid': 'stub-anchor-all',
                    onClick: () => emit('update:anchored-ids', (props.inlineThreads as Array<{ id: string }>).map(t => t.id)),
                }, 'anchor all'),
                ...(props.inlineThreads as Array<{ id: string }>).map(thread =>
                    h('div', { key: thread.id }, slots.thread?.({ thread })),
                ),
            ])
    },
})

function fileDiff(overrides: Partial<FileDiffDto> = {}): FileDiffDto {
    return {
        filePath: 'src/Foo.cs',
        unifiedDiff: '--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n',
        changeType: 'Modified',
        isBinary: false,
        originalPath: null,
        availability: 'Available',
        availabilityMessage: null,
        ...overrides,
    } as FileDiffDto
}

function buildVm(overrides: {
    jobDiff?: JobDiffDto | null
    jobDiffLoading?: boolean
    jobDiffError?: string | null
    diffFindings?: ProtocolReviewComment[]
} = {}) {
    const loadJobDiff = vi.fn()
    const vm = reactive({
        jobDiff: ref(overrides.jobDiff ?? null),
        jobDiffLoading: ref(overrides.jobDiffLoading ?? false),
        jobDiffError: ref(overrides.jobDiffError ?? null),
        diffFindings: ref(overrides.diffFindings ?? []),
        loadJobDiff,
    })
    return { vm: vm as unknown as JobProtocolViewModel, loadJobDiff }
}

function mountTab(vm: JobProtocolViewModel) {
    return mount(JobProtocolDiffTab, {
        props: { vm },
        global: { stubs: { JobProtocolDiffViewer: DiffViewerStub } },
    })
}

describe('JobProtocolDiffTab', () => {
    it('shows a loading state while the diff loads', () => {
        const { vm } = buildVm({ jobDiffLoading: true })
        const wrapper = mountTab(vm)
        expect(wrapper.get('[data-testid="job-diff-loading"]').text()).toContain('Loading reviewed diff')
    })

    it('shows the honest empty state when the job retained no diff', () => {
        const { vm } = buildVm({ jobDiff: { jobId: 'job-1', files: [] } })
        const wrapper = mountTab(vm)
        expect(wrapper.get('[data-testid="job-diff-empty"]').text()).toContain('No diff retained for this job')
    })

    it('shows the error with a retry that reloads the diff', async () => {
        const { vm, loadJobDiff } = buildVm({ jobDiffError: 'Diff unavailable (HTTP 500)' })
        const wrapper = mountTab(vm)
        expect(wrapper.get('[data-testid="job-diff-error"]').text()).toContain('HTTP 500')
        await wrapper.get('[data-testid="job-diff-error-retry"]').trigger('click')
        expect(loadJobDiff).toHaveBeenCalledTimes(1)
    })

    it('lists files with finding counts and defaults to the first file with findings', async () => {
        const { vm } = buildVm({
            jobDiff: {
                jobId: 'job-1',
                files: [
                    fileDiff({ filePath: 'src/Aaa.cs' }),
                    fileDiff({ filePath: 'src/Foo.cs' }),
                ],
            },
            diffFindings: [
                { filePath: 'src/Foo.cs', lineNumber: 1, severity: 'warning', message: 'Watch this.' } as ProtocolReviewComment,
            ],
        })
        const wrapper = mountTab(vm)
        await flushPromises()

        expect(wrapper.get('[data-testid="job-diff-file-src/Aaa.cs"]').text()).not.toContain('⚠')
        expect(wrapper.get('[data-testid="job-diff-file-src/Foo.cs"]').text()).toContain('1 ⚠')
        // Default selection is the file carrying the finding.
        expect(wrapper.get('[data-testid="diff-viewer-stub"]').attributes('data-file')).toBe('src/Foo.cs')
    })

    it('pins findings to their lines as markers that reveal on click', async () => {
        const { vm } = buildVm({
            jobDiff: { jobId: 'job-1', files: [fileDiff()] },
            diffFindings: [
                { filePath: 'src/Foo.cs', lineNumber: 1, severity: 'error', message: 'Null dereference here.' } as ProtocolReviewComment,
            ],
        })
        const wrapper = mountTab(vm)
        await flushPromises()

        // The stub viewer reports the marker's line as anchored, so the finding
        // is pinned in the diff (not duplicated in the unanchored list below).
        await wrapper.get('[data-testid="stub-anchor-all"]').trigger('click')
        await flushPromises()

        const marker = wrapper.get('[data-testid="diff-finding-marker"]')
        expect(marker.text()).toContain('error')
        expect(marker.text()).toContain('L1')
        expect(wrapper.find('[data-testid="diff-finding-message"]').exists()).toBe(false)

        await marker.get('.diff-finding-marker-toggle').trigger('click')
        expect(wrapper.get('[data-testid="diff-finding-message"]').text()).toBe('Null dereference here.')
    })

    it('lists findings whose line never anchored below the diff', async () => {
        const { vm } = buildVm({
            jobDiff: { jobId: 'job-1', files: [fileDiff()] },
            diffFindings: [
                { filePath: 'src/Foo.cs', lineNumber: null, severity: 'info', message: 'File-level note.' } as ProtocolReviewComment,
            ],
        })
        const wrapper = mountTab(vm)
        await flushPromises()

        // The file-level finding has no line to anchor to, so it lands in the
        // unanchored list below the diff, expanded.
        expect(wrapper.text()).toContain('Findings without a diff line')
        expect(wrapper.get('[data-testid="diff-finding-message"]').text()).toBe('File-level note.')
    })

    it('moves a finding out of the unanchored list once its line anchors', async () => {
        const { vm } = buildVm({
            jobDiff: { jobId: 'job-1', files: [fileDiff()] },
            diffFindings: [
                { filePath: 'src/Foo.cs', lineNumber: 1, severity: 'warning', message: 'Anchors late.' } as ProtocolReviewComment,
            ],
        })
        const wrapper = mountTab(vm)
        await flushPromises()

        expect(wrapper.text()).toContain('Findings without a diff line')
        await wrapper.get('[data-testid="stub-anchor-all"]').trigger('click')
        await flushPromises()
        expect(wrapper.text()).not.toContain('Findings without a diff line')
    })
})
