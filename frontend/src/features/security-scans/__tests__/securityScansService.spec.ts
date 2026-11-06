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
  getBlastRadius,
  getScanFindings,
  getScanSbomJson,
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

  it('fetches the SBOM as parsed JSON', async () => {
    const bom = { bomFormat: 'CycloneDX', specVersion: '1.5', components: [{ name: 'lodash', version: '4.17.21' }] }
    authedFetchMock.mockResolvedValue(okJson(bom))
    const res = await getScanSbomJson('client-1', 'scan-1')
    expect(res).toEqual(bom)
    expect(authedFetchMock.mock.calls[0][0]).toContain('/clients/client-1/security-scans/scan-1/sbom')
  })

  it('returns null when no SBOM was produced (404)', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { error: 'No SBOM produced for this scan.' }))
    const res = await getScanSbomJson('client-1', 'scan-1')
    expect(res).toBeNull()
  })

  it('fetches the blast-radius map for a finding', async () => {
    const map = {
      status: 'Available',
      reason: null,
      seed: { kind: 'file', filePath: 'src/vuln.ts', symbol: 'vulnerable' },
      affectedModules: [{ filePath: 'src/use.ts', callers: [{ name: 'run', kind: 'definition', startLine: 2, endLine: 4, referenceLines: [3] }], importLines: [] }],
      entryPoints: [{ filePath: 'src/use.ts', definition: 'run', kind: 'definition', line: 3 }],
      edges: [{ fromFile: 'src/use.ts', fromDefinition: 'run', fromLine: 3, toFile: 'src/vuln.ts', toSymbol: 'vulnerable', toLine: 1, verification: 'analyzer-confirmed' }],
      notes: [],
      filesScanned: 2,
      skippedFiles: [],
      totalSkipped: 0,
      truncated: false,
      analyzedAt: '2026-11-06T00:00:00Z',
    }
    authedFetchMock.mockResolvedValue(okJson(map))
    const res = await getBlastRadius('client-1', 'scan-1', 'finding-1')
    expect(res).toEqual(map)
    expect(authedFetchMock.mock.calls[0][0]).toBe(
      'https://api.test/clients/client-1/security-scans/scan-1/findings/finding-1/blast-radius',
    )
  })

  it('passes an Unavailable blast-radius map through with its reason', async () => {
    const map = {
      status: 'Unavailable',
      reason: 'The seed file is written in a language the analyzer does not support.',
      seed: { kind: 'file', filePath: 'docs/notes.md' },
      affectedModules: [],
      entryPoints: [],
      edges: [],
      notes: [],
      filesScanned: 0,
      skippedFiles: [],
      totalSkipped: 0,
      truncated: false,
      analyzedAt: '2026-11-06T00:00:00Z',
    }
    authedFetchMock.mockResolvedValue(okJson(map))
    const res = await getBlastRadius('client-1', 'scan-1', 'finding-1')
    expect(res.status).toBe('Unavailable')
    expect(res.reason).toContain('does not support')
  })

  it('surfaces the server error message on blast-radius failure', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { error: 'No such finding for this client.' }))
    await expect(getBlastRadius('client-1', 'scan-1', 'finding-1')).rejects.toThrow(
      /No such finding for this client/,
    )
  })
})
