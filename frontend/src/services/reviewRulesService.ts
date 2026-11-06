// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Custom Review Rules API.
 *
 * Custom review rules are the team's explicit, written-down policy for one client:
 * { name, pattern (substring or regex, matched against file path and/or file
 * content), severity, message }. They are distinct from reviewer memory, which
 * learns implicitly from accept/dismiss decisions. A rule never invents a finding —
 * enforcement only matches its pattern literally against real code — it only ever
 * upgrades a finding's severity, and it cites itself by name on the finding.
 * An invalid regex is rejected at save time with the real regex error (400).
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way reviewerMemoryService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as ReviewRulesUnavailableError so the UI can say honestly that the API update
 * is not available yet instead of inventing rules or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type ReviewRuleSeverity = 'Error' | 'Warning' | 'Suggestion' | 'Info'

export interface ReviewRuleEntry {
    id: string
    name: string
    pattern: string
    isRegex: boolean
    severity: ReviewRuleSeverity
    message: string
    enabled: boolean
    createdAt: string
    updatedAt: string
}

export interface ReviewRuleListResponse {
    items: ReviewRuleEntry[]
}

export interface ReviewRuleInput {
    name: string
    pattern: string
    isRegex: boolean
    severity: ReviewRuleSeverity
    message: string
    enabled?: boolean
}

/**
 * Thrown when the review-rules endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty rule list.
 */
export class ReviewRulesUnavailableError extends Error {
    constructor() {
        super('Review rules need the API update — not available yet.')
        this.name = 'ReviewRulesUnavailableError'
    }
}

export function isReviewRulesUnavailable(error: unknown): boolean {
    return error instanceof ReviewRulesUnavailableError
}

function rulesUrl(clientId: string, suffix = ''): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/review-rules${suffix}`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/** Lists the client's custom review rules, in creation order. */
export async function listReviewRules(clientId: string): Promise<ReviewRuleEntry[]> {
    try {
        const res = await authedFetch(rulesUrl(clientId))
        if (res.status === 404) throw new ReviewRulesUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load review rules.'))
        const body = (await res.json()) as ReviewRuleListResponse
        return Array.isArray(body?.items) ? body.items : []
    } catch (error) {
        if (isReviewRulesUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load review rules.'))
    }
}

/** Creates a custom review rule. An invalid regex is rejected by the API with the real regex error. */
export async function createReviewRule(
    clientId: string,
    input: ReviewRuleInput,
): Promise<ReviewRuleEntry> {
    try {
        const res = await authedFetch(rulesUrl(clientId), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(input),
        })
        if (res.status === 404) throw new ReviewRulesUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to create the rule.'))
        return (await res.json()) as ReviewRuleEntry
    } catch (error) {
        if (isReviewRulesUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to create the rule.'))
    }
}

/** Replaces a custom review rule, including its enabled state. */
export async function updateReviewRule(
    clientId: string,
    ruleId: string,
    input: ReviewRuleInput & { enabled: boolean },
): Promise<ReviewRuleEntry> {
    try {
        const res = await authedFetch(rulesUrl(clientId, `/${encodeURIComponent(ruleId)}`), {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(input),
        })
        if (res.status === 404) throw new ReviewRulesUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to update the rule.'))
        return (await res.json()) as ReviewRuleEntry
    } catch (error) {
        if (isReviewRulesUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to update the rule.'))
    }
}

/** Enables or disables one custom review rule without changing anything else. */
export async function setReviewRuleEnabled(
    clientId: string,
    ruleId: string,
    enabled: boolean,
): Promise<ReviewRuleEntry> {
    try {
        const res = await authedFetch(rulesUrl(clientId, `/${encodeURIComponent(ruleId)}/set-enabled`), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled }),
        })
        if (res.status === 404) throw new ReviewRulesUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to change the rule.'))
        return (await res.json()) as ReviewRuleEntry
    } catch (error) {
        if (isReviewRulesUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to change the rule.'))
    }
}

/** Deletes a custom review rule entirely. */
export async function deleteReviewRule(clientId: string, ruleId: string): Promise<void> {
    try {
        const res = await authedFetch(rulesUrl(clientId, `/${encodeURIComponent(ruleId)}`), {
            method: 'DELETE',
        })
        if (res.status === 404) throw new ReviewRulesUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to delete the rule.'))
    } catch (error) {
        if (isReviewRulesUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to delete the rule.'))
    }
}
