// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { ref, shallowRef } from 'vue'
import { useSession } from '@/composables/useSession'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { JobDiffDto } from '../types'

/**
 * Owns the lazily-fetched job-level diff shown in the Diff tab: every reviewed
 * file's unified diff in one payload, its loading/error flags, and an in-memory
 * cache keyed by job. Mirrors `useFileDiff` (single-file read path) — the tab
 * reads the whole PR at once instead of one file per trace pass.
 */
export function useJobDiff() {
    const jobDiff = shallowRef<JobDiffDto | null>(null)
    const jobDiffLoading = ref(false)
    const jobDiffError = ref<string | null>(null)
    const jobDiffCache = new Map<string, JobDiffDto>()

    async function loadJobDiff(jobId: string): Promise<void> {
        if (!jobId) {
            jobDiff.value = null
            jobDiffError.value = null
            return
        }

        const cached = jobDiffCache.get(jobId)
        if (cached) {
            jobDiff.value = cached
            jobDiffError.value = null
            jobDiffLoading.value = false
            return
        }

        jobDiffLoading.value = true
        jobDiffError.value = null
        try {
            const { getAccessToken } = useSession()
            const token = getAccessToken()
            const headers: Record<string, string> = { Accept: 'application/json' }
            if (token) {
                headers.Authorization = `Bearer ${token}`
            }

            const res = await fetch(
                `${getActiveRuntime().apiBaseUrl}/reviewing/jobs/${jobId}/diff`,
                { headers },
            )
            if (res.status === 404) {
                jobDiff.value = null
                jobDiffError.value = null
                return
            }
            if (!res.ok) {
                jobDiff.value = null
                jobDiffError.value = `Diff unavailable (HTTP ${res.status})`
                return
            }

            const payload = (await res.json()) as JobDiffDto
            jobDiffCache.set(jobId, payload)
            jobDiff.value = payload
            jobDiffError.value = null
        } catch (error) {
            jobDiff.value = null
            jobDiffError.value = `Diff unavailable: ${error instanceof Error ? error.message : 'unknown error'}`
        } finally {
            jobDiffLoading.value = false
        }
    }

    function resetJobDiff(): void {
        jobDiff.value = null
        jobDiffError.value = null
    }

    return {
        jobDiff,
        jobDiffLoading,
        jobDiffError,
        loadJobDiff,
        resetJobDiff,
    }
}
