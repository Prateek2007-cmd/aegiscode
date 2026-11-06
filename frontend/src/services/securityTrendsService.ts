// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the security posture trends API.
 *
 * The trends endpoint aggregates the client's stored scans and findings into
 * the before/after story: findings by severity per completed scan, mean time
 * to resolve, and new-vs-fixed per scan. Every number comes from stored data
 * — with fewer than 2 completed scans the server answers "not enough history
 * yet" and this module surfaces that honestly instead of inventing a trend.
 *
 * The endpoint lives outside the generated OpenAPI schema, so this module calls
 * it with authedFetch the same way jobsService does. When the backend does not
 * have the endpoint yet the server answers 404; that is surfaced as
 * SecurityTrendsUnavailableError so the UI can say the API update is not
 * available yet instead of inventing a trend or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export interface SecurityTrendPoint {
  scanId: string
  repositoryUrl: string
  at: string
  critical: number
  high: number
  medium: number
  low: number
  info: number
  total: number
  newFindings: number
  fixedFindings: number
}

export interface SecurityTrends {
  hasEnoughHistory: boolean
  scanCount: number
  points: SecurityTrendPoint[]
  meanTimeToResolveDays?: number | null
  resolvedFindingsCount: number
  message?: string | null
}

/**
 * Thrown when the trends endpoint answers 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty trend.
 */
export class SecurityTrendsUnavailableError extends Error {
  constructor() {
    super('Posture trends need the API update — not available yet.')
    this.name = 'SecurityTrendsUnavailableError'
  }
}

export function isSecurityTrendsUnavailable(error: unknown): boolean {
  return error instanceof SecurityTrendsUnavailableError
}

function trendsUrl(clientId: string): string {
  return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/security-scans/trends`
}

/** Loads the client's posture trend. Never invents numbers — it only returns what the server reported. */
export async function getSecurityTrends(clientId: string): Promise<SecurityTrends> {
  try {
    const res = await authedFetch(trendsUrl(clientId))
    if (res.status === 404) throw new SecurityTrendsUnavailableError()
    if (!res.ok) {
      let fallback = 'Failed to load posture trends.'
      try {
        const body = (await res.json()) as unknown
        fallback = getApiErrorMessage(body, fallback)
      } catch {
        // Keep the fallback when the error body is not JSON.
      }
      throw new Error(fallback)
    }
    const body = (await res.json()) as SecurityTrends
    return {
      hasEnoughHistory: body?.hasEnoughHistory === true,
      scanCount: typeof body?.scanCount === 'number' ? body.scanCount : 0,
      points: Array.isArray(body?.points) ? body.points : [],
      meanTimeToResolveDays: body?.meanTimeToResolveDays ?? null,
      resolvedFindingsCount: typeof body?.resolvedFindingsCount === 'number' ? body.resolvedFindingsCount : 0,
      message: body?.message ?? null,
    }
  } catch (error) {
    if (isSecurityTrendsUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to load posture trends.'))
  }
}

/** Total findings in one trend point. Only sums what the server reported. */
export function trendPointTotal(point: SecurityTrendPoint): number {
  return point.total
}

/**
 * The before/after headline: criticals in the first scan vs the latest scan,
 * e.g. "14 criticals → 3 in two weeks". Returns null when there is no trend to
 * headline (fewer than 2 points) — the UI shows the empty state instead.
 */
export function trendsHeadline(trends: SecurityTrends): string | null {
  if (!trends.hasEnoughHistory || trends.points.length < 2) return null
  const first = trends.points[0]
  const latest = trends.points[trends.points.length - 1]
  const firstAt = new Date(first.at)
  const latestAt = new Date(latest.at)
  const span = Number.isNaN(firstAt.getTime()) || Number.isNaN(latestAt.getTime())
    ? null
    : latestAt.getTime() - firstAt.getTime()
  const spanLabel = span === null ? '' : ` in ${formatTrendSpan(span)}`
  const firstLabel = first.critical === 1 ? '1 critical' : `${first.critical} criticals`
  const latestLabel = latest.critical === 1 ? '1 critical' : `${latest.critical} criticals`
  return `${firstLabel} → ${latestLabel}${spanLabel}`
}

/** Human span between two scans, e.g. "2 weeks", "3 days", "5 hours". Never invents precision — days are rounded down. */
export function formatTrendSpan(ms: number): string {
  const days = Math.floor(ms / 86_400_000)
  if (days >= 7 && days % 7 === 0) {
    const weeks = days / 7
    return weeks === 1 ? '1 week' : `${weeks} weeks`
  }
  if (days >= 1) return days === 1 ? '1 day' : `${days} days`
  const hours = Math.floor(ms / 3_600_000)
  if (hours >= 1) return hours === 1 ? '1 hour' : `${hours} hours`
  return 'under an hour'
}

/**
 * Mean time to resolve as a display string, e.g. "14 days" or "1.5 days".
 * Returns null when the server reported no resolved findings — the UI says
 * "no fixed findings yet" instead of showing a fake 0.
 */
export function formatMeanTimeToResolve(meanDays: number | null | undefined): string | null {
  if (meanDays === null || meanDays === undefined) return null
  if (meanDays < 1) {
    const hours = Math.round(meanDays * 24)
    return hours <= 1 ? 'under an hour' : `${hours} hours`
  }
  const rounded = Math.round(meanDays * 10) / 10
  return rounded === 1 ? '1 day' : `${rounded} days`
}
