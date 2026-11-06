// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Pure helpers for the Reviewer Memory panel. No API calls, no invented data:
 * every helper describes a value the API returned.
 */
import type { ReviewerMemoryEntry } from '@/services/reviewerMemoryService'

/** Human label for a rule's source. Unknown sources pass through unchanged. */
export function sourceLabel(source: string): string {
    switch (source) {
        case 'review':
            return 'Review'
        case 'scan':
            return 'Scan'
        default:
            return source
    }
}

/** Status badge text for a learned rule. */
export function statusLabel(entry: ReviewerMemoryEntry): string {
    return entry.muted ? 'Muted' : 'Active'
}

/** "3 accepted · 5 dismissed" with singular/plural handled. */
export function countsText(entry: ReviewerMemoryEntry): string {
    const accepts = `${entry.accepts} ${entry.accepts === 1 ? 'accept' : 'accepts'}`
    const dismissals = `${entry.dismissals} ${entry.dismissals === 1 ? 'dismissal' : 'dismissals'}`
    return `${accepts} · ${dismissals}`
}

/** Why a rule is muted, or null when it is not muted. Never invents a reason. */
export function mutedDetail(entry: ReviewerMemoryEntry): string | null {
    if (!entry.muted) return null
    return entry.mutedReason?.trim() ? entry.mutedReason.trim() : 'Muted by reviewer memory.'
}

/** Header summary: "3 rules learned · 1 muted". Empty memory reports itself honestly. */
export function describeMemory(entries: ReviewerMemoryEntry[]): string {
    if (entries.length === 0) return 'Nothing learned yet'
    const muted = entries.filter((entry) => entry.muted).length
    const rules = `${entries.length} ${entries.length === 1 ? 'rule' : 'rules'} learned`
    if (muted === 0) return rules
    return `${rules} · ${muted} muted`
}
