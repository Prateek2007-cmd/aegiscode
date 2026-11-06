// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { MergeGuardThreshold } from '@/services/mergeGuardService'

export type GuardTone = 'success' | 'failure' | 'neutral'

export interface GuardDecisionDescription {
    label: string
    tone: GuardTone
}

/** Human label + tone for a guard conclusion. Unknown values render as neutral, never as a pass. */
export function describeGuardDecision(conclusion: string | null | undefined): GuardDecisionDescription {
    switch (conclusion) {
        case 'success':
            return { label: 'Passed', tone: 'success' }
        case 'failure':
            return { label: 'Failed — merging should wait', tone: 'failure' }
        case 'neutral':
            return { label: 'Could not decide', tone: 'neutral' }
        default:
            return { label: 'No check reported', tone: 'neutral' }
    }
}

export interface GuardThresholdOption {
    value: MergeGuardThreshold
    label: string
    detail: string
}

/** Threshold choices, most severe first. A finding blocks when it is at least this severe. */
export const guardThresholdOptions: GuardThresholdOption[] = [
    { value: 'Critical', label: 'Critical only', detail: 'Only critical findings block merging.' },
    { value: 'High', label: 'High and above', detail: 'Critical and high findings block merging.' },
    { value: 'Medium', label: 'Medium and above', detail: 'Critical, high, and medium findings block merging.' },
    { value: 'Low', label: 'Low and above', detail: 'Anything low or worse blocks merging.' },
]

/** One-line summary of what blocked (or did not block) a check. */
export function describeBlockingFindings(blockingScanFindings: number, blockingReviewFindings: number): string {
    const total = blockingScanFindings + blockingReviewFindings
    if (total === 0) return 'No blocking findings.'
    const parts: string[] = []
    if (blockingScanFindings > 0) {
        parts.push(`${blockingScanFindings} from the security scan`)
    }
    if (blockingReviewFindings > 0) {
        parts.push(`${blockingReviewFindings} from the review`)
    }
    return `${total} blocking finding${total === 1 ? '' : 's'} (${parts.join(', ')}).`
}
