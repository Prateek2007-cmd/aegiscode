// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
    buildFixProposalRequest,
    createDraftPullRequest,
    FixProposalsUnavailableError,
    type DraftPullRequestResult,
} from '@/services/fixProposalsService'
import { authedFetch } from '@/services/api'

vi.mock('@/services/api', async (importOriginal) => ({
    ...(await importOriginal<typeof import('@/services/api')>()),
    authedFetch: vi.fn(),
}))

vi.mock('@/services/jobsService', () => ({
    getJobsBaseUrl: () => '',
}))

function jsonResponse(status: number, body: unknown): Response {
    return {
        status,
        ok: status >= 200 && status < 300,
        json: async () => body,
    } as Response
}

const clientId = '11111111-1111-1111-1111-111111111111'
const proposalId = '22222222-2222-2222-2222-222222222222'

const createdResult: DraftPullRequestResult = {
    proposal: {
        id: proposalId,
        findingRef: 'finding-1',
        source: 'review',
        title: 'Fix the off-by-one',
        status: 'Approved',
        createdAt: '2026-11-05T00:00:00Z',
        draftPrUrl: 'https://github.com/octo/demo/pull/42',
        draftPrBranch: 'aegiscode/fix-22222222-1',
        draftPrBaseBranch: 'main',
    },
    pullRequestUrl: 'https://github.com/octo/demo/pull/42',
    branch: 'aegiscode/fix-22222222-1',
    baseBranch: 'main',
    usedDefaultBranch: true,
    note: "Branched from the repository default branch 'main' because no base was supplied.",
}

describe('createDraftPullRequest', () => {
    beforeEach(() => {
        vi.mocked(authedFetch).mockReset()
    })

    it('posts the patch overrides to the create-draft-pr endpoint and returns the PR', async () => {
        vi.mocked(authedFetch).mockResolvedValue(jsonResponse(200, createdResult))

        const result = await createDraftPullRequest(clientId, proposalId, {
            repositoryOwner: 'octo',
            repositoryName: 'demo',
            filePath: 'src/Foo.cs',
            startLine: 7,
            endLine: 7,
            originalCode: '        var x = 1;',
            suggestedCode: '        var x = 2;',
        })

        expect(result).toEqual(createdResult)
        expect(authedFetch).toHaveBeenCalledWith(
            `/clients/${clientId}/fix-proposals/${proposalId}/create-draft-pr`,
            expect.objectContaining({ method: 'POST' }),
        )
        const body = JSON.parse(String(vi.mocked(authedFetch).mock.calls[0]?.[1]?.body ?? '{}')) as Record<string, unknown>
        expect(body.repositoryOwner).toBe('octo')
        expect(body.suggestedCode).toBe('        var x = 2;')
    })

    it('sends an empty body when the proposal already stored the patch', async () => {
        vi.mocked(authedFetch).mockResolvedValue(jsonResponse(200, createdResult))

        await createDraftPullRequest(clientId, proposalId)

        const body = JSON.parse(String(vi.mocked(authedFetch).mock.calls[0]?.[1]?.body ?? '{}')) as Record<string, unknown>
        expect(body).toEqual({})
    })

    it('surfaces the API update as unavailable on 404', async () => {
        vi.mocked(authedFetch).mockResolvedValue(jsonResponse(404, { error: 'not found' }))

        await expect(createDraftPullRequest(clientId, proposalId)).rejects.toBeInstanceOf(FixProposalsUnavailableError)
    })

    it('surfaces the server refusal message on 422', async () => {
        vi.mocked(authedFetch).mockResolvedValue(jsonResponse(422, {
            error: 'The file has changed since the proposal was made.',
        }))

        await expect(createDraftPullRequest(clientId, proposalId)).rejects.toThrow(
            'The file has changed since the proposal was made.',
        )
    })

    it('falls back to a usable message when the body explains nothing', async () => {
        vi.mocked(authedFetch).mockResolvedValue(jsonResponse(502, {}))

        await expect(createDraftPullRequest(clientId, proposalId)).rejects.toThrow(
            'Failed to create the draft pull request.',
        )
    })
})

describe('buildFixProposalRequest', () => {
    it('passes patch fields through so an approved proposal can open a draft PR in one click', () => {
        const request = buildFixProposalRequest({
            findingRef: 'finding-1',
            source: 'review',
            title: 'Fix the off-by-one',
            filePath: 'src/Foo.cs',
            line: 7,
            repositoryOwner: 'octo',
            repositoryName: 'demo',
            baseRef: 'main',
            startLine: 7,
            endLine: 7,
            originalCode: '        var x = 1;',
            suggestedCode: '        var x = 2;',
        })

        expect(request.repositoryOwner).toBe('octo')
        expect(request.repositoryName).toBe('demo')
        expect(request.baseRef).toBe('main')
        expect(request.startLine).toBe(7)
        expect(request.originalCode).toBe('        var x = 1;')
        expect(request.suggestedCode).toBe('        var x = 2;')
    })
})
