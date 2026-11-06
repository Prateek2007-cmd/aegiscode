// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

export type PushScanTone = 'success' | 'failure' | 'neutral'

export interface PushScanOutcomeDescription {
    label: string
    tone: PushScanTone
}

/** Human label + tone for a push-scan attempt outcome. Unknown values render as neutral, never as a scan. */
export function describePushScanOutcome(outcome: string | null | undefined): PushScanOutcomeDescription {
    switch (outcome) {
        case 'started':
            return { label: 'Scan started', tone: 'success' }
        case 'pending':
            return { label: 'Scan starting…', tone: 'neutral' }
        case 'failed':
            return { label: 'Could not start scan', tone: 'failure' }
        default:
            return { label: 'No scan recorded', tone: 'neutral' }
    }
}

/** Shortens a commit SHA for display; leaves short values untouched. */
export function shortPushSha(sha: string): string {
    return sha.length > 7 ? sha.slice(0, 7) : sha
}

/**
 * Parses the comma-separated branch filter the card's text field holds into the
 * list the API expects. Empty input resets to main only.
 */
export function parseBranchFilter(input: string): string[] {
    const branches = input
        .split(',')
        .map((branch) => branch.trim())
        .filter((branch) => branch.length > 0)
    return branches.length > 0 ? branches : ['main']
}

/** Joins the stored branch list back into the card's text field. */
export function formatBranchFilter(branches: string[] | null | undefined): string {
    return Array.isArray(branches) && branches.length > 0 ? branches.join(', ') : 'main'
}

/** One-line summary of a push-triggered scan for the recent list. */
export function describePushScanEntry(
    entry: { repositoryUrl: string; branch: string; headSha: string; pusher?: string | null },
): string {
    const pusher = entry.pusher ? ` by ${entry.pusher}` : ''
    return `${entry.repositoryUrl} — ${entry.branch} at ${shortPushSha(entry.headSha)}${pusher}`
}
