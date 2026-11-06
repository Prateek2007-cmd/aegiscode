// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Push Scans API.
 *
 * A push webhook for a covered repository starts a security scan of the pushed
 * commit automatically: no click needed, one scan per (repository, commit SHA)
 * at most, main branch only unless the client enables more branches. The card
 * shows the toggle, the branch filter, and the most recent push-triggered scans
 * with their commit SHAs — and says plainly when a push did not start a scan.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way fixProposalsService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as PushScanUnavailableError so the UI can say honestly that the API update
 * is not available yet instead of inventing a push-scan state or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type PushScanOutcome = 'pending' | 'started' | 'failed'

export interface PushScanConfig {
    enabled: boolean
    branches: string[]
}

export interface PushScanEntry {
    repositoryUrl: string
    branch: string
    headSha: string
    pusher?: string | null
    outcome: PushScanOutcome
    reason: string
    scanId?: string | null
    scanStatus?: string | null
    updatedAt: string
}

export interface PushScanStatus {
    config: PushScanConfig
    recentScans: PushScanEntry[]
}

/**
 * Thrown when the push-scan endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as disabled push scans or an empty scan list.
 */
export class PushScanUnavailableError extends Error {
    constructor() {
        super('Push scans need the API update — not available yet.')
        this.name = 'PushScanUnavailableError'
    }
}

export function isPushScanUnavailable(error: unknown): boolean {
    return error instanceof PushScanUnavailableError
}

function pushScanUrl(clientId: string): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/push-scans`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/** Loads the client's push-scan config plus its latest push-triggered scans, newest first. */
export async function getPushScanStatus(clientId: string): Promise<PushScanStatus> {
    try {
        const res = await authedFetch(pushScanUrl(clientId))
        if (res.status === 404) throw new PushScanUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load push scans.'))
        const body = (await res.json()) as PushScanStatus
        return {
            config: body?.config ?? { enabled: true, branches: ['main'] },
            recentScans: Array.isArray(body?.recentScans) ? body.recentScans : [],
        }
    } catch (error) {
        if (isPushScanUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load push scans.'))
    }
}

/** Saves the client's push-scan configuration. An empty branch list resets to main only. */
export async function updatePushScanConfig(
    clientId: string,
    config: PushScanConfig,
): Promise<PushScanConfig> {
    try {
        const res = await authedFetch(pushScanUrl(clientId), {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config),
        })
        if (res.status === 404) throw new PushScanUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to save push scan settings.'))
        return (await res.json()) as PushScanConfig
    } catch (error) {
        if (isPushScanUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to save push scan settings.'))
    }
}
