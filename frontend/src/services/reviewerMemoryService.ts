// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Reviewer Memory API.
 *
 * Reviewer memory is what the reviewer learned from a client's accept and dismiss
 * decisions: per-rule accept/dismiss counts, and whether a rule muted itself after
 * repeated dismissals with no accepts. A muted rule never reaches a pull request —
 * it is withheld at publication and named in the summary — and it is always shown
 * as muted in the UI, never hidden.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way fixProposalsService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as ReviewerMemoryUnavailableError so the UI can say honestly that the API update
 * is not available yet instead of inventing learned rules or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type ReviewerMemorySource = 'review' | 'scan'
export type ReviewerMemoryOutcome = 'accepted' | 'dismissed'

export interface ReviewerMemoryEntry {
    ruleKey: string
    label: string
    source: ReviewerMemorySource
    accepts: number
    dismissals: number
    muted: boolean
    mutedAt?: string | null
    mutedReason?: string | null
    firstSeenAt: string
    lastOutcomeAt: string
}

export interface ReviewerMemoryListResponse {
    items: ReviewerMemoryEntry[]
}

export interface ReviewerMemoryGuidanceResponse {
    guidance: string[]
}

/**
 * Thrown when the reviewer-memory endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty memory.
 */
export class ReviewerMemoryUnavailableError extends Error {
    constructor() {
        super('Reviewer memory needs the API update — not available yet.')
        this.name = 'ReviewerMemoryUnavailableError'
    }
}

export function isReviewerMemoryUnavailable(error: unknown): boolean {
    return error instanceof ReviewerMemoryUnavailableError
}

function memoryUrl(clientId: string, suffix = ''): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/reviewer-memory${suffix}`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/** Lists the client's learned rules, most recently active first when the API orders them so. */
export async function listReviewerMemory(clientId: string): Promise<ReviewerMemoryEntry[]> {
    try {
        const res = await authedFetch(memoryUrl(clientId))
        if (res.status === 404) throw new ReviewerMemoryUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load reviewer memory.'))
        const body = (await res.json()) as ReviewerMemoryListResponse
        return Array.isArray(body?.items) ? body.items : []
    } catch (error) {
        if (isReviewerMemoryUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load reviewer memory.'))
    }
}

/** Records one human outcome for a rule. The rule key may be omitted and is derived from the label. */
export async function recordReviewerMemoryOutcome(
    clientId: string,
    request: {
        ruleKey?: string | null
        label: string
        source: ReviewerMemorySource
        outcome: ReviewerMemoryOutcome
    },
): Promise<ReviewerMemoryEntry> {
    try {
        const res = await authedFetch(memoryUrl(clientId, '/outcomes'), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(request),
        })
        if (res.status === 404) throw new ReviewerMemoryUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to record the outcome.'))
        return (await res.json()) as ReviewerMemoryEntry
    } catch (error) {
        if (isReviewerMemoryUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to record the outcome.'))
    }
}

/** Un-mutes a muted rule, keeping its counts. Returns the rule after un-muting. */
export async function unmuteReviewerMemoryRule(
    clientId: string,
    ruleKey: string,
): Promise<ReviewerMemoryEntry> {
    try {
        const res = await authedFetch(memoryUrl(clientId, '/unmute'), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ruleKey }),
        })
        if (res.status === 404) throw new ReviewerMemoryUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to un-mute the rule.'))
        return (await res.json()) as ReviewerMemoryEntry
    } catch (error) {
        if (isReviewerMemoryUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to un-mute the rule.'))
    }
}

/** Forgets a rule entirely — counts and mute state are deleted. */
export async function resetReviewerMemoryRule(clientId: string, ruleKey: string): Promise<void> {
    try {
        const res = await authedFetch(memoryUrl(clientId, '/reset'), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ruleKey }),
        })
        if (res.status === 404) throw new ReviewerMemoryUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to reset the rule.'))
    } catch (error) {
        if (isReviewerMemoryUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to reset the rule.'))
    }
}

/** The learned "do not flag" guidance lines for the client's next review, possibly empty. */
export async function getReviewerMemoryGuidance(clientId: string): Promise<string[]> {
    try {
        const res = await authedFetch(memoryUrl(clientId, '/prompt-guidance'))
        if (res.status === 404) throw new ReviewerMemoryUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load reviewer guidance.'))
        const body = (await res.json()) as ReviewerMemoryGuidanceResponse
        return Array.isArray(body?.guidance) ? body.guidance : []
    } catch (error) {
        if (isReviewerMemoryUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load reviewer guidance.'))
    }
}
