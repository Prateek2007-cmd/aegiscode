// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const { authedFetchMock, getActiveRuntimeMock } = vi.hoisted(() => ({
  authedFetchMock: vi.fn(),
  getActiveRuntimeMock: vi.fn(() => ({ apiBaseUrl: 'https://api.test' })),
}))

vi.mock('@/services/api', () => ({ authedFetch: authedFetchMock }))
vi.mock('@/app/runtime/runtimeContext', () => ({ getActiveRuntime: getActiveRuntimeMock }))

import {
  getScanFindings,
  listSecurityScans,
  startSecurityScan,
  suppressFinding,
} from '../securityScansService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

describe('securityScansService', () => {
  it('starts a scan with the repository URL and returns the scan id', async () => {
    authedFetchMock.mockResolvedValue(okJson({ scanId: 'abc', status: 'Queued' }))
    const res = await startSecurityScan('client-1', { repositoryUrl: 'https://github.com/o/r' })
    expect(res.scanId).toBe('abc')
    const [url, init] = authedFetchMock.mock.calls[0]
    expect(url).toBe('https://api.test/clients/client-1/security-scans')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body).repositoryUrl).toBe('https://github.com/o/r')
  })

  it('surfaces the server error message on start failure', async () => {
    authedFetchMock.mockResolvedValue(errJson(422, { error: 'Only https repository URLs are supported.' }))
    await expect(startSecurityScan('client-1', { repositoryUrl: 'http://x/y' })).rejects.toThrow(
      /Only https repository URLs are supported/,
    )
  })

  it('lists scans for the client', async () => {
    authedFetchMock.mockResolvedValue(okJson([{ scanId: 'a' }, { scanId: 'b' }]))
    const scans = await listSecurityScans('client-1')
    expect(scans).toHaveLength(2)
    expect(authedFetchMock.mock.calls[0][0]).toContain('/clients/client-1/security-scans?')
  })

  it('fetches findings with filter query params', async () => {
    authedFetchMock.mockResolvedValue(okJson({ items: [], totalCount: 0 }))
    await getScanFindings('client-1', 'scan-1', { severity: 'High', scanner: 'dep-scan', limit: 25, offset: 50 })
    const url = authedFetchMock.mock.calls[0][0] as string
    expect(url).toContain('/clients/client-1/security-scans/scan-1/findings?')
    expect(url).toContain('severity=High')
    expect(url).toContain('scanner=dep-scan')
    expect(url).toContain('limit=25')
    expect(url).toContain('offset=50')
  })

  it('suppresses a finding with the reason', async () => {
    authedFetchMock.mockResolvedValue(okJson({ status: 'Suppressed' }))
    await suppressFinding('client-1', 'scan-1', 'finding-1', 'false positive: test fixture')
    const [url, init] = authedFetchMock.mock.calls[0]
    expect(url).toContain('/findings/finding-1/suppress')
    expect(JSON.parse(init.body).reason).toBe('false positive: test fixture')
  })
})
