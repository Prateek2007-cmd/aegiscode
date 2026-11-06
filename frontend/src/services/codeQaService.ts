// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Ask-your-codebase API.
 *
 * A question is answered only from code retrieved through ProCursor for this client, and
 * the answer carries file citations for every claim. When nothing relevant is retrieved,
 * ProCursor is unavailable, or the model answers without citing the retrieved snippets,
 * the API answers honestly that it does not know (`answered: false`) — it never invents
 * an answer, a file, or a line number. The UI shows that honest answer, plus whatever
 * snippets were retrieved, instead of failing silently.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with
 * authedFetch the same way fixProposalsService does. When the backend does not have the
 * endpoints yet the server answers 404; that is surfaced as CodeQaUnavailableError so the
 * UI can say honestly that the API update is not available yet instead of inventing
 * answers or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export interface CodeQaCitation {
    contentPath: string
    title?: string | null
    excerpt: string
    score: number
    sourceKind: string
    branch: string
    commitSha: string
    freshnessStatus: string
}

export interface AskCodeQuestionResponse {
    answered: boolean
    answer: string
    citations: CodeQaCitation[]
    retrievalStatus: string
    retrievalNote?: string | null
    modelId?: string | null
}

/**
 * Thrown when the code-qa endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty answer.
 */
export class CodeQaUnavailableError extends Error {
    constructor() {
        super('Ask your codebase needs the API update — not available yet.')
        this.name = 'CodeQaUnavailableError'
    }
}

export function isCodeQaUnavailable(error: unknown): boolean {
    return error instanceof CodeQaUnavailableError
}

function codeQaUrl(clientId: string): string {
    return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/code-qa/ask`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
    try {
        const body = (await res.json()) as unknown
        return getApiErrorMessage(body, fallback)
    } catch {
        return fallback
    }
}

/** Asks the client's codebase a question. Returns the grounded answer, or an honest "don't know". */
export async function askCodeQuestion(
    clientId: string,
    question: string,
): Promise<AskCodeQuestionResponse> {
    try {
        const res = await authedFetch(codeQaUrl(clientId), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ question }),
        })
        if (res.status === 404) throw new CodeQaUnavailableError()
        if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to ask the codebase.'))
        const body = (await res.json()) as AskCodeQuestionResponse
        return {
            answered: body.answered === true,
            answer: typeof body.answer === 'string' ? body.answer : '',
            citations: Array.isArray(body.citations) ? body.citations : [],
            retrievalStatus: typeof body.retrievalStatus === 'string' ? body.retrievalStatus : '',
            retrievalNote: body.retrievalNote ?? null,
            modelId: body.modelId ?? null,
        }
    } catch (error) {
        if (isCodeQaUnavailable(error)) throw error
        throw new Error(sanitizeErrorMessage(error, 'Failed to ask the codebase.'))
    }
}
