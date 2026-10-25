// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Loads everything the review depth panel needs for one job — the job detail (tokens, cost, model,
 * duration, files in scope) and the protocol trace (passes and published findings, with events so the
 * verification funnel can be parsed) — and derives the funnel, the flattened findings and the
 * per-lens counts from them. The funnel is real data only: when no final-gate record is present the
 * panel says so instead of showing zeros.
 */

import { computed, ref, type ComputedRef, type Ref } from 'vue'
import {
    getJobDetail,
    getJobProtocol,
    type JobDetailResponse,
} from '@/services/jobsService'
import {
    countFindingsByLens,
    flattenPublishedFindings,
    parseVerificationFunnel,
    type LensCounts,
    type PublishedFinding,
    type ReviewJobProtocolDto,
    type VerificationFunnel,
} from '@/features/reviews/utils/reviewDepth'

/** Job detail plus the file-scope fields the detail endpoint returns at runtime. */
export interface JobDetailWithScope extends JobDetailResponse {
    filesReviewed?: number | null
    filesInScope?: number | null
}

export interface UseJobReviewDepth {
    loading: Ref<boolean>
    error: Ref<string>
    detail: Ref<JobDetailWithScope | null>
    passes: Ref<ReviewJobProtocolDto[]>
    funnel: ComputedRef<VerificationFunnel | null>
    findings: ComputedRef<PublishedFinding[]>
    lensCounts: ComputedRef<LensCounts>
    totalCostUsd: ComputedRef<number | null>
    load: () => Promise<void>
}

export function useJobReviewDepth(jobId: string | null): UseJobReviewDepth {
    const loading = ref(false)
    const error = ref('')
    const detail = ref<JobDetailWithScope | null>(null)
    const passes = ref<ReviewJobProtocolDto[]>([])
    const loaded = ref(false)

    const funnel = computed(() => parseVerificationFunnel(passes.value))
    const findings = computed(() => flattenPublishedFindings(passes.value))
    const lensCounts = computed(() => countFindingsByLens(findings.value))

    // The detail's own estimate wins; the per-tier breakdown is the fallback when it is absent.
    const totalCostUsd = computed<number | null>(() => {
        if (detail.value?.totalEstimatedCostUsd != null) return detail.value.totalEstimatedCostUsd
        const rows = detail.value?.tokenBreakdown ?? []
        if (rows.length === 0) return null
        return rows.reduce((sum, row) => sum + (row.estimatedCostUsd ?? 0), 0)
    })

    async function load(): Promise<void> {
        if (!jobId || loading.value || loaded.value) return
        loading.value = true
        error.value = ''
        try {
            const [jobDetail, protocol] = await Promise.all([
                getJobDetail(jobId),
                getJobProtocol(jobId, { includeEvents: true }),
            ])
            detail.value = jobDetail as JobDetailWithScope
            passes.value = protocol ?? []
            loaded.value = true
        } catch (err) {
            error.value = err instanceof Error ? err.message : 'Failed to load review depth.'
        } finally {
            loading.value = false
        }
    }

    return { loading, error, detail, passes, funnel, findings, lensCounts, totalCostUsd, load }
}
