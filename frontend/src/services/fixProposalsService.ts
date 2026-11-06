// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Fix Proposals API.
 *
 * A fix proposal is a human-gated draft: a finding (from a review or a security scan) is turned into
 * fix guidance that stays PendingApproval until an administrator approves or rejects it. Approval does
 * NOT create a pull request by itself — the separate create-draft-pr endpoint is the step that opens
 * a real draft PR on GitHub for an approved proposal, after verifying the replacement against the
 * file at the base. When a finding carried an exact suggested replacement, the proposal stores it
 * (repository, line range, original and replacement code) so the draft PR can open in one click;
 * otherwise the caller supplies the patch with the create-draft-pr call.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way jobsService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as FixProposalsUnavailableError so the UI can say honestly that the API update
 * is not available yet instead of inventing proposals or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type FixProposalSource = 'scan' | 'review'
export type FixProposalStatus = 'PendingApproval' | 'Approved' | 'Rejected'

export interface CreateFixProposalRequest {
    findingRef: string
    source: FixProposalSource
    title: string
    filePath?: string | null
    line?: number | null
    remediation?: string | null
    confidence?: string | null
    repositoryOwner?: string | null
    repositoryName?: string | null
    baseRef?: string | null
    startLine?: number | null
    endLine?: number | null
    originalCode?: string | null
    suggestedCode?: string | null
}

export interface FixProposal {
    id: string
    findingRef: string
    source: FixProposalSource
    title: string
    filePath?: string | null
    line?: number | null
    remediation?: string | null
    confidence?: string | null
    status: FixProposalStatus
    createdAt: string
    decidedAt?: string | null
    decisionReason?: string | null
    repositoryOwner?: string | null
    repositoryName?: string | null
    baseRef?: string | null
    startLine?: number | null
    endLine?: number | null
    originalCode?: string | null
    suggestedCode?: string | null
    draftPrUrl?: string | null
    draftPrBranch?: string | null
    draftPrBaseBranch?: string | null
    draftPrCreatedAt?: string | null
}

/**
 * Overrides for a create-draft-pr call. Every field is optional: a value left out falls back to
 * what the proposal stored at creation. At least the repository, the file, the line range, the
 * original text, and the replacement must resolve from the two combined.
 */
export interface CreateDraftPullRequestRequest {
    repositoryOwner?: string | null
    repositoryName?: string | null
    providerScopePath?: string | null
    baseRef?: string | null
    baseSha?: string | null
    filePath?: string | null
    startLine?: number | null
    endLine?: number | null
    originalCode?: string | null
    suggestedCode?: string | null
}

export interface DraftPullRequestResult {
    proposal: FixProposal
    pullRequestUrl: string
    branch: string
    baseBranch: string
    usedDefaultBranch: boolean
    note: string | null
}

export interface FixProposalListResponse {
    items: FixProposal[]
}

/**
 * Thrown when the fix-proposals endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty queue.
 */
export class FixProposalsUnavailableError extends Error {
    constructor() {
        super('Fix proposals need the API update — not available yet.')
        this.name = 'FixProposalsUnavailableError'
    }
}

export function isFixProposalsUnavailable(error: unknown): boolean {
    return error instanceof FixProposalsUnavailableError
}

function proposalsUrl(clientId: string, suffix = ''): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/fix-proposals${suffix}`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/**
 * Builds the request body for a fix proposal from a finding. Exported so other features (for example
 * the security-scans finding rows) can draft a fix for their own findings through the same shape
 * without duplicating the mapping. findingRef must be stable per finding so the same finding does not
 * produce duplicate proposals on repeat clicks.
 */
export function buildFixProposalRequest(input: {
    findingRef: string
    source: FixProposalSource
    title: string
    filePath?: string | null
    line?: number | null
    remediation?: string | null
    confidence?: string | null
    repositoryOwner?: string | null
    repositoryName?: string | null
    baseRef?: string | null
    startLine?: number | null
    endLine?: number | null
    originalCode?: string | null
    suggestedCode?: string | null
}): CreateFixProposalRequest {
    return {
        findingRef: input.findingRef,
        source: input.source,
        title: input.title,
        filePath: input.filePath ?? null,
        line: input.line ?? null,
        remediation: input.remediation ?? null,
        confidence: input.confidence ?? null,
        repositoryOwner: input.repositoryOwner ?? null,
        repositoryName: input.repositoryName ?? null,
        baseRef: input.baseRef ?? null,
        startLine: input.startLine ?? null,
        endLine: input.endLine ?? null,
        originalCode: input.originalCode ?? null,
        suggestedCode: input.suggestedCode ?? null,
    }
}

/** Drafts a fix proposal for a finding. Returns the created proposal (status PendingApproval). */
export async function createFixProposal(
    clientId: string,
    request: CreateFixProposalRequest,
): Promise<FixProposal> {
    try {
        const res = await authedFetch(proposalsUrl(clientId), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(request),
        })
        if (res.status === 404) throw new FixProposalsUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to draft a fix proposal.'))
        return (await res.json()) as FixProposal
    } catch (error) {
        if (isFixProposalsUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to draft a fix proposal.'))
    }
}

/** Lists the fix proposals for a client, newest first when the API orders them so. */
export async function listFixProposals(clientId: string): Promise<FixProposal[]> {
    try {
        const res = await authedFetch(proposalsUrl(clientId))
        if (res.status === 404) throw new FixProposalsUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load fix proposals.'))
        const body = (await res.json()) as FixProposalListResponse
        return Array.isArray(body?.items) ? body.items : []
    } catch (error) {
        if (isFixProposalsUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to load fix proposals.'))
    }
}

/** Approves a proposal. An optional reason is recorded with the decision for audit. */
export async function approveFixProposal(
    clientId: string,
    proposalId: string,
    reason?: string,
): Promise<FixProposal> {
    return decideFixProposal(clientId, proposalId, 'approve', reason)
}

/** Rejects a proposal. An optional reason is recorded with the decision for audit. */
export async function rejectFixProposal(
    clientId: string,
    proposalId: string,
    reason?: string,
): Promise<FixProposal> {
    return decideFixProposal(clientId, proposalId, 'reject', reason)
}

async function decideFixProposal(
    clientId: string,
    proposalId: string,
    decision: 'approve' | 'reject',
    reason?: string,
): Promise<FixProposal> {
    const fallback = decision === 'approve'
        ? 'Failed to approve the fix proposal.'
        : 'Failed to reject the fix proposal.'
    try {
        const res = await authedFetch(proposalsUrl(clientId, `/${encodeURIComponent(proposalId)}/${decision}`), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(reason ? { reason } : {}),
        })
        if (res.status === 404) throw new FixProposalsUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, fallback))
        return (await res.json()) as FixProposal
    } catch (error) {
        if (isFixProposalsUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, fallback))
    }
}

/** Opens a draft pull request on GitHub for an approved proposal. Returns the PR URL, branch, and base. */
export async function createDraftPullRequest(
    clientId: string,
    proposalId: string,
    request: CreateDraftPullRequestRequest = {},
): Promise<DraftPullRequestResult> {
    try {
        const res = await authedFetch(proposalsUrl(clientId, `/${encodeURIComponent(proposalId)}/create-draft-pr`), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(request),
        })
        if (res.status === 404) throw new FixProposalsUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to create the draft pull request.'))
        return (await res.json()) as DraftPullRequestResult
    } catch (error) {
        if (isFixProposalsUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to create the draft pull request.'))
    }
}

/** Renders an approved proposal as a Markdown file the reviewer can apply by hand. */
export function fixGuidanceMarkdown(proposal: FixProposal): string {
    const lines = [
        `# Fix guidance: ${proposal.title}`,
        '',
        `Source: ${proposal.source === 'review' ? 'AI review finding' : 'Security scan finding'}`,
        `Finding ref: ${proposal.findingRef}`,
        proposal.filePath ? `File: ${proposal.filePath}${proposal.line != null ? `:${proposal.line}` : ''}` : null,
        proposal.confidence != null ? `Confidence: ${proposal.confidence}` : null,
        `Status: ${proposal.status}`,
        '',
        '## Remediation',
        '',
        proposal.remediation?.trim() ? proposal.remediation.trim() : 'No remediation text was recorded for this proposal.',
        '',
        '_Approved — fix guidance ready to apply. Use “Create draft PR” in the Fix Queue to open a draft pull request on GitHub._',
        '',
    ]
    return lines.filter((line) => line !== null).join('\n')
}

/** Downloads an approved proposal's fix guidance as a Markdown file. */
export function downloadFixGuidance(proposal: FixProposal): void {
    const blob = new Blob([fixGuidanceMarkdown(proposal)], { type: 'text/markdown;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `fix-guidance-${proposal.id}.md`
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    URL.revokeObjectURL(url)
}
