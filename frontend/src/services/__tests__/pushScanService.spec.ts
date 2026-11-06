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
  getPushScanStatus,
  isPushScanUnavailable,
  PushScanUnavailableError,
  updatePushScanConfig,
} from '../pushScanService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

const sampleStatus = {
  config: { enabled: true, branches: ['main'] },
  recentScans: [
    {
      repositoryUrl: 'https://github.com/acme/web',
      branch: 'main',
      headSha: 'abc1234567890abcdef1234567890abcdef12345678',
      pusher: 'prateek',
      outcome: 'started',
      reason: 'Scan queued.',
      scanId: 'scan-1',
      scanStatus: 'Queued',
      updatedAt: '2026-11-06T00:00:00Z',
    },
  ],
}

describe('pushScanService', () => {
  it('loads the config and recent push-triggered scans for the client', async () => {
    authedFetchMock.mockResolvedValue(okJson(sampleStatus))
    const status = await getPushScanStatus('client-1')
    expect(status.config).toEqual({ enabled: true, branches: ['main'] })
    expect(status.recentScans).toHaveLength(1)
    expect(status.recentScans[0].headSha).toBe('abc1234567890abcdef1234567890abcdef12345678')
    expect(authedFetchMock.mock.calls[0][0]).toBe('https://api.test/clients/client-1/push-scans')
  })

  it('defaults to enabled main-only with no scans when the server reports nothing', async () => {
    authedFetchMock.mockResolvedValue(okJson({}))
    await expect(getPushScanStatus('client-1')).resolves.toEqual({
      config: { enabled: true, branches: ['main'] },
      recentScans: [],
    })
  })

  it('throws PushScanUnavailableError when the endpoints answer 404', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { title: 'Not Found' }))
    await expect(getPushScanStatus('client-1')).rejects.toBeInstanceOf(PushScanUnavailableError)
    await expect(getPushScanStatus('client-1')).rejects.toThrow('API update')
    expect(isPushScanUnavailable(new PushScanUnavailableError())).toBe(true)
  })

  it('saves the config with a PUT', async () => {
    authedFetchMock.mockResolvedValue(okJson({ enabled: false, branches: ['main'] }))
    const saved = await updatePushScanConfig('client-1', { enabled: false, branches: ['main'] })
    expect(saved).toEqual({ enabled: false, branches: ['main'] })
    expect(authedFetchMock.mock.calls[0][0]).toBe('https://api.test/clients/client-1/push-scans')
    expect(authedFetchMock.mock.calls[0][1]).toMatchObject({ method: 'PUT' })
  })

  it('surfaces a 400 from the server when the branch list is unusable', async () => {
    authedFetchMock.mockResolvedValue(errJson(400, { error: 'Branch names must not contain whitespace.' }))
    await expect(
      updatePushScanConfig('client-1', { enabled: true, branches: ['my branch'] }),
    ).rejects.toThrow('Branch names must not contain whitespace.')
  })
})
