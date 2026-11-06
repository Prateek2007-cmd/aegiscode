// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const { authedFetchMock, getActiveRuntimeMock } = vi.hoisted(() => ({
  authedFetchMock: vi.fn(),
  getActiveRuntimeMock: vi.fn(() => ({ apiBaseUrl: 'https://api.test' })),
}))

vi.mock('@/services/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/services/api')>()),
  authedFetch: authedFetchMock,
}))
vi.mock('@/app/runtime/runtimeContext', () => ({ getActiveRuntime: getActiveRuntimeMock }))

import {
  askCodeQuestion,
  CodeQaUnavailableError,
  isCodeQaUnavailable,
} from '../codeQaService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

describe('codeQaService', () => {
  it('asks a question and returns the grounded answer with citations', async () => {
    authedFetchMock.mockResolvedValue(
      okJson({
        answered: true,
        answer: 'The handler lives in [1].',
        citations: [
          {
            contentPath: 'src/Auth/Handler.cs',
            title: 'Auth handler',
            excerpt: 'public sealed class AuthHandler { }',
            score: 0.9,
            sourceKind: 'Repository',
            branch: 'main',
            commitSha: 'abc123',
            freshnessStatus: 'fresh',
          },
        ],
        retrievalStatus: 'complete',
        retrievalNote: null,
        modelId: 'test-model',
      }),
    )

    const res = await askCodeQuestion('client-1', 'Where is auth handled?')

    expect(res.answered).toBe(true)
    expect(res.answer).toBe('The handler lives in [1].')
    expect(res.citations).toHaveLength(1)
    expect(res.citations[0].contentPath).toBe('src/Auth/Handler.cs')
    expect(res.modelId).toBe('test-model')
    const [url, init] = authedFetchMock.mock.calls[0]
    expect(url).toBe('https://api.test/clients/client-1/code-qa/ask')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body).question).toBe('Where is auth handled?')
  })

  it('returns the honest unanswered response when nothing matched', async () => {
    authedFetchMock.mockResolvedValue(
      okJson({
        answered: false,
        answer: "I don't know from this codebase — no indexed code matched your question.",
        citations: [],
        retrievalStatus: 'noResult',
        retrievalNote: 'No indexed knowledge matched the requested question.',
        modelId: null,
      }),
    )

    const res = await askCodeQuestion('client-1', 'Where is the flux capacitor?')

    expect(res.answered).toBe(false)
    expect(res.citations).toHaveLength(0)
    expect(res.retrievalStatus).toBe('noResult')
  })

  it('defaults missing fields instead of crashing on a partial payload', async () => {
    authedFetchMock.mockResolvedValue(okJson({ answered: true, answer: 'See [1].' }))

    const res = await askCodeQuestion('client-1', 'Where is auth handled?')

    expect(res.answered).toBe(true)
    expect(res.citations).toHaveLength(0)
    expect(res.retrievalStatus).toBe('')
    expect(res.modelId).toBeNull()
  })

  it('surfaces the server error message on failure', async () => {
    authedFetchMock.mockResolvedValue(errJson(400, { error: 'Question is required.' }))

    await expect(askCodeQuestion('client-1', '  ')).rejects.toThrow(/Question is required/)
  })

  it('maps a 404 to CodeQaUnavailableError so the UI can say the API update is missing', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { error: 'Not found' }))

    await expect(askCodeQuestion('client-1', 'Where is auth handled?')).rejects.toBeInstanceOf(
      CodeQaUnavailableError,
    )
  })

  it('recognises the unavailable error', () => {
    expect(isCodeQaUnavailable(new CodeQaUnavailableError())).toBe(true)
    expect(isCodeQaUnavailable(new Error('boom'))).toBe(false)
  })
})
