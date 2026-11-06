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
  formatMeanTimeToResolve,
  formatTrendSpan,
  getSecurityTrends,
  isSecurityTrendsUnavailable,
  SecurityTrendsUnavailableError,
  trendsHeadline,
} from '../securityTrendsService'
import type { SecurityTrends } from '../securityTrendsService'

function okJson(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as Response
}

function errJson(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as Response
}

beforeEach(() => {
  authedFetchMock.mockReset()
})

const twoScanTrends: SecurityTrends = {
  hasEnoughHistory: true,
  scanCount: 2,
  points: [
    {
      scanId: 'scan-1',
      repositoryUrl: 'https://github.com/o/r',
      at: '2026-10-20T10:00:00Z',
      critical: 14,
      high: 5,
      medium: 2,
      low: 0,
      info: 0,
      total: 21,
      newFindings: 21,
      fixedFindings: 0,
    },
    {
      scanId: 'scan-2',
      repositoryUrl: 'https://github.com/o/r',
      at: '2026-11-03T10:00:00Z',
      critical: 3,
      high: 2,
      medium: 1,
      low: 0,
      info: 0,
      total: 6,
      newFindings: 0,
      fixedFindings: 15,
    },
  ],
  meanTimeToResolveDays: 14,
  resolvedFindingsCount: 15,
  message: null,
}

describe('securityTrendsService', () => {
  it('loads the trend for the client', async () => {
    authedFetchMock.mockResolvedValue(okJson(twoScanTrends))
    const trends = await getSecurityTrends('client-1')
    expect(trends.hasEnoughHistory).toBe(true)
    expect(trends.points).toHaveLength(2)
    expect(trends.points[1].fixedFindings).toBe(15)
    expect(authedFetchMock.mock.calls[0][0]).toBe(
      'https://api.test/clients/client-1/security-scans/trends',
    )
  })

  it('returns the not-enough-history response without inventing points', async () => {
    authedFetchMock.mockResolvedValue(
      okJson({
        hasEnoughHistory: false,
        scanCount: 1,
        points: [],
        meanTimeToResolveDays: null,
        resolvedFindingsCount: 0,
        message: 'Not enough history yet — run at least 2 completed scans to see trends.',
      }),
    )
    const trends = await getSecurityTrends('client-1')
    expect(trends.hasEnoughHistory).toBe(false)
    expect(trends.points).toEqual([])
    expect(trends.meanTimeToResolveDays).toBeNull()
    expect(trends.message).toContain('Not enough history')
  })

  it('throws SecurityTrendsUnavailableError when the endpoint answers 404', async () => {
    authedFetchMock.mockResolvedValue(errJson(404, { title: 'Not Found' }))
    await expect(getSecurityTrends('client-1')).rejects.toBeInstanceOf(SecurityTrendsUnavailableError)
    await expect(getSecurityTrends('client-1')).rejects.toThrow('API update')
  })

  it('throws a readable error on a server failure', async () => {
    authedFetchMock.mockResolvedValue(errJson(500, { error: 'Boom.' }))
    await expect(getSecurityTrends('client-1')).rejects.toThrow('Boom.')
  })

  it('headlines the before/after criticals story', () => {
    expect(trendsHeadline(twoScanTrends)).toBe('14 criticals → 3 criticals in 2 weeks')
  })

  it('returns no headline with fewer than 2 points', () => {
    expect(
      trendsHeadline({ ...twoScanTrends, hasEnoughHistory: false, points: [] }),
    ).toBeNull()
    expect(
      trendsHeadline({ ...twoScanTrends, points: twoScanTrends.points.slice(0, 1) }),
    ).toBeNull()
  })

  it('formats the mean time to resolve only when there are resolved findings', () => {
    expect(formatMeanTimeToResolve(14)).toBe('14 days')
    expect(formatMeanTimeToResolve(1)).toBe('1 day')
    expect(formatMeanTimeToResolve(1.5)).toBe('1.5 days')
    expect(formatMeanTimeToResolve(0.5)).toBe('12 hours')
    expect(formatMeanTimeToResolve(null)).toBeNull()
    expect(formatMeanTimeToResolve(undefined)).toBeNull()
  })

  it('formats trend spans in whole weeks, days, or hours', () => {
    expect(formatTrendSpan(14 * 86_400_000)).toBe('2 weeks')
    expect(formatTrendSpan(7 * 86_400_000)).toBe('1 week')
    expect(formatTrendSpan(3 * 86_400_000)).toBe('3 days')
    expect(formatTrendSpan(1 * 86_400_000)).toBe('1 day')
    expect(formatTrendSpan(5 * 3_600_000)).toBe('5 hours')
    expect(formatTrendSpan(30 * 60_000)).toBe('under an hour')
  })

  it('isSecurityTrendsUnavailable only matches the unavailable error', () => {
    expect(isSecurityTrendsUnavailable(new SecurityTrendsUnavailableError())).toBe(true)
    expect(isSecurityTrendsUnavailable(new Error('other'))).toBe(false)
  })
})
