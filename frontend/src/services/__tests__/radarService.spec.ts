// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const { authedFetchMock, getActiveRuntimeMock } = vi.hoisted(() => ({
  authedFetchMock: vi.fn(),
  getActiveRuntimeMock: vi.fn(() => ({ apiBaseUrl: 'https://api.test' })),
}))

vi.mock('@/services/api', () => ({
  authedFetch: authedFetchMock,
  getApiErrorMessage: (body: unknown, fallback: string) => {
    const record = body as { error?: string; title?: string } | null
    return record?.error ?? record?.title ?? fallback
  },
}))
vi.mock('@/app/runtime/runtimeContext', () => ({ getActiveRuntime: getActiveRuntimeMock }))

import {
  acknowledgeRadarAlert,
  checkRadarNow,
  getRadarSettings,
  isRadarUnavailable,
  listRadarAlerts,
  radarPassSummary,
  RadarUnavailableError,
  updateRadarSettings,
} from '../radarService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

const sampleAlert = {
  id: 'alert-1',
  scanId: 'scan-1',
  repositoryUrl: 'https://github.com/o/r',
  packageName: 'lodash',
  packageVersion: '4.17.21',
  ecosystem: 'npm',
  vulnId: 'GHSA-new1',
  cveId: 'CVE-2026-0001',
  severity: 'Critical',
  summary: 'Bad vuln',
  fixedVersion: '4.17.22',
  osvUrl: 'https://osv.dev/vulnerability/GHSA-new1',
  firstSeenAt: '2026-11-06T00:00:00Z',
  lastSeenAt: '2026-11-06T00:00:00Z',
  status: 'New',
}

describe('radarService', () => {
  it('lists alerts for the client', async () => {
    authedFetchMock.mockResolvedValue(okJson({ items: [sampleAlert] }))
    const alerts = await listRadarAlerts('client-1')
    expect(alerts).toHaveLength(1)
    expect(alerts[0].vulnId).toBe('GHSA-new1')
    expect(authedFetchMock.mock.calls[0][0]).toBe('https://api.test/clients/client-1/radar/alerts')
  })

  it('returns an empty list when the server reports no alerts', async () => {
    authedFetchMock.mockResolvedValue(okJson({ items: [] }))
    await expect(listRadarAlerts('client-1')).resolves.toEqual([])
  })

  it('throws RadarUnavailableError when the endpoints answer 404', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { title: 'Not Found' }))
    await expect(listRadarAlerts('client-1')).rejects.toBeInstanceOf(RadarUnavailableError)
    await expect(listRadarAlerts('client-1')).rejects.toThrow('API update')
  })

  it('surfaces the server error message on failure', async () => {
    authedFetchMock.mockResolvedValue(errJson(500, { error: 'Radar is disabled.' }))
    await expect(listRadarAlerts('client-1')).rejects.toThrow(/Radar is disabled/)
  })

  it('runs a check-now pass and returns the pass result', async () => {
    const pass = {
      checkedAt: '2026-11-06T00:00:00Z',
      reposChecked: 1,
      componentsChecked: 2,
      componentsSkipped: 0,
      newOccurrences: 1,
      newAlerts: 1,
      nothingNew: false,
      repos: [],
    }
    authedFetchMock.mockResolvedValue(okJson(pass))
    const result = await checkRadarNow('client-1')
    expect(result.newAlerts).toBe(1)
    const [url, init] = authedFetchMock.mock.calls[0]
    expect(url).toBe('https://api.test/clients/client-1/radar/check-now')
    expect(init.method).toBe('POST')
  })

  it('reads and updates radar settings', async () => {
    authedFetchMock.mockResolvedValue(okJson({ enabled: true, intervalMinutes: 360 }))
    const settings = await getRadarSettings('client-1')
    expect(settings.intervalMinutes).toBe(360)
    expect(authedFetchMock.mock.calls[0][0]).toBe('https://api.test/clients/client-1/radar/settings')

    authedFetchMock.mockResolvedValue(okJson({ enabled: false, intervalMinutes: 120 }))
    const updated = await updateRadarSettings('client-1', { enabled: false, intervalMinutes: 120 })
    expect(updated.enabled).toBe(false)
    const [, init] = authedFetchMock.mock.calls[1]
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body)).toEqual({ enabled: false, intervalMinutes: 120 })
  })

  it('acknowledges an alert', async () => {
    authedFetchMock.mockResolvedValue(okJson({ ...sampleAlert, status: 'Acknowledged' }))
    const alert = await acknowledgeRadarAlert('client-1', 'alert-1')
    expect(alert.status).toBe('Acknowledged')
    const [url, init] = authedFetchMock.mock.calls[0]
    expect(url).toBe('https://api.test/clients/client-1/radar/alerts/alert-1/acknowledge')
    expect(init.method).toBe('POST')
  })

  it('isRadarUnavailable only matches RadarUnavailableError', () => {
    expect(isRadarUnavailable(new RadarUnavailableError())).toBe(true)
    expect(isRadarUnavailable(new Error('other'))).toBe(false)
  })
})

describe('radarPassSummary', () => {
  const base = {
    checkedAt: '2026-11-06T00:00:00Z',
    reposChecked: 1,
    componentsChecked: 2,
    componentsSkipped: 0,
    newOccurrences: 0,
    newAlerts: 0,
    nothingNew: true,
    repos: [],
  }

  it('says nothing new when the pass found nothing', () => {
    expect(radarPassSummary(base)).toContain('nothing new')
  })

  it('counts new vulnerabilities and alerts when the pass found some', () => {
    const result = { ...base, nothingNew: false, newOccurrences: 2, newAlerts: 1 }
    expect(radarPassSummary(result)).toContain('2 new vulnerabilities')
    expect(radarPassSummary(result)).toContain('1 new alert')
  })

  it('prefers the server message when nothing could be checked', () => {
    const result = { ...base, reposChecked: 0, nothingNew: false, message: 'No scans with an SBOM yet.' }
    expect(radarPassSummary(result)).toBe('No scans with an SBOM yet.')
  })
})
