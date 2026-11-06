// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const { authedFetchMock } = vi.hoisted(() => ({
  authedFetchMock: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  authedFetch: authedFetchMock,
  getApiErrorMessage: (body: unknown, fallback: string) => {
    const record = body as { error?: string; title?: string } | null
    return record?.error ?? record?.title ?? fallback
  },
}))
vi.mock('@/services/jobsService', () => ({
  getJobsBaseUrl: () => 'https://api.test',
}))

import {
  createReviewRule,
  deleteReviewRule,
  isReviewRulesUnavailable,
  listReviewRules,
  ReviewRulesUnavailableError,
  setReviewRuleEnabled,
  updateReviewRule,
} from '../reviewRulesService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

const sampleRule = {
  id: 'rule-1',
  name: 'No Console.WriteLine',
  pattern: 'Console.WriteLine',
  isRegex: false,
  severity: 'Error',
  message: 'Do not write to the console in production code.',
  enabled: true,
  createdAt: '2026-11-06T00:00:00Z',
  updatedAt: '2026-11-06T00:00:00Z',
}

describe('reviewRulesService', () => {
  it('lists the client rules', async () => {
    authedFetchMock.mockResolvedValue(okJson({ items: [sampleRule] }))
    const rules = await listReviewRules('client-1')
    expect(rules).toEqual([sampleRule])
    expect(authedFetchMock).toHaveBeenCalledWith(
      'https://api.test/clients/client-1/review-rules',
    )
  })

  it('creates a rule and returns it', async () => {
    authedFetchMock.mockResolvedValue(okJson(sampleRule))
    const created = await createReviewRule('client-1', {
      name: 'No Console.WriteLine',
      pattern: 'Console.WriteLine',
      isRegex: false,
      severity: 'Error',
      message: 'Do not write to the console in production code.',
    })
    expect(created).toEqual(sampleRule)
    expect(authedFetchMock.mock.calls[0][1].method).toBe('POST')
  })

  it('surfaces the API error message when the server rejects a rule', async () => {
    authedFetchMock.mockResolvedValue(errJson(400, { error: 'Invalid regex: missing closing parenthesis.' }))
    await expect(
      createReviewRule('client-1', {
        name: 'Bad regex',
        pattern: '([unclosed',
        isRegex: true,
        severity: 'Error',
        message: 'Never stored.',
      }),
    ).rejects.toThrow('Invalid regex: missing closing parenthesis.')
  })

  it('updates a rule and toggles its enabled state', async () => {
    authedFetchMock.mockResolvedValue(okJson({ ...sampleRule, severity: 'Warning' }))
    const updated = await updateReviewRule('client-1', 'rule-1', {
      name: 'No Console.WriteLine',
      pattern: 'Console.WriteLine',
      isRegex: false,
      severity: 'Warning',
      message: 'Do not write to the console in production code.',
      enabled: true,
    })
    expect(updated.severity).toBe('Warning')

    authedFetchMock.mockResolvedValue(okJson({ ...sampleRule, enabled: false }))
    const disabled = await setReviewRuleEnabled('client-1', 'rule-1', false)
    expect(disabled.enabled).toBe(false)
  })

  it('deletes a rule', async () => {
    authedFetchMock.mockResolvedValue(okJson({ deleted: true }))
    await deleteReviewRule('client-1', 'rule-1')
    expect(authedFetchMock.mock.calls[0][1].method).toBe('DELETE')
  })

  it('reports 404 as the API update not being deployed yet, never as an empty list', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { error: 'Not found.' }))
    await expect(listReviewRules('client-1')).rejects.toBeInstanceOf(ReviewRulesUnavailableError)
    try {
      await listReviewRules('client-1')
    } catch (err) {
      expect(isReviewRulesUnavailable(err)).toBe(true)
    }
  })
})
