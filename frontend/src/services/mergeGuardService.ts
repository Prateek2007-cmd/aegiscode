// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Merge Guard API.
 *
 * The guard reports a GitHub check run on a pull request head: failure when the
 * latest scan or the just-published review has blocking findings at or above the
 * client's threshold, success when the data is clean and fresh, neutral when the
 * data is missing or stale. A check run only blocks merging when the
 * "AegisCode Merge Guard" check is added to the branch's required status checks —
 * the card says so wherever the guard is shown.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way fixProposalsService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as MergeGuardUnavailableError so the UI can say honestly that the API update
 * is not available yet instead of inventing a guard state or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type MergeGuardThreshold = 'Critical' | 'High' | 'Medium' | 'Low'
export type MergeGuardConclusion = 'success' | 'failure' | 'neutral'

export interface MergeGuardConfig {
    enabled: boolean
    threshold: MergeGuardThreshold
}

export interface MergeGuardCheck {
    repositoryUrl: string
    pullRequestNumber: number
    headSha: string
    checkRunId?: number | null
    checkRunUrl?: string | null
    conclusion: MergeGuardConclusion
    title: string
    reason: string
    blockingScanFindings: number
    blockingReviewFindings: number
    updatedAt: string
    posted: boolean
    postError?: string | null
}

export interface MergeGuardStatus {
    config: MergeGuardConfig
    checks: MergeGuardCheck[]
}

/**
 * Thrown when the merge-guard endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as a disabled guard or an empty check list.
 */
export class MergeGuardUnavailableError extends Error {
    constructor() {
        super('Merge guard needs the API update — not available yet.')
        this.name = 'MergeGuardUnavailableError'
    }
}

export function isMergeGuardUnavailable(error: unknown): boolean {
    return error instanceof MergeGuardUnavailableError
}

function guardUrl(clientId: string): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/merge-guard`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/** Loads the client's guard config plus its latest checks, newest first. */
export async function getMergeGuardStatus(clientId: string): Promise<MergeGuardStatus> {
    try {
        const res = await authedFetch(guardUrl(clientId))
        if (res.status === 404) throw new MergeGuardUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load merge guard status.'))
        const body = (await res.json()) as MergeGuardStatus
        return {
            config: body?.config ?? { enabled: false, threshold: 'High' },
            checks: Array.isArray(body?.checks) ? body.checks : [],
        }
    } catch (error) {
        if (isMergeGuardUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load merge guard status.'))
    }
}

/** Saves the client's guard configuration. Threshold must be Critical, High, Medium, or Low. */
export async function updateMergeGuardConfig(
    clientId: string,
    config: MergeGuardConfig,
): Promise<MergeGuardConfig> {
    try {
        const res = await authedFetch(guardUrl(clientId), {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config),
        })
        if (res.status === 404) throw new MergeGuardUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to save merge guard settings.'))
        return (await res.json()) as MergeGuardConfig
    } catch (error) {
        if (isMergeGuardUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to save merge guard settings.'))
    }
}
