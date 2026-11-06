// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the Dependency Radar API.
 *
 * The radar re-checks a client's stored SBOMs against OSV.dev and alerts when
 * a new Critical or High CVE appears that no scan has reported yet. Only OSV
 * data is ever shown — a CVE, severity, or fixed version is never invented,
 * and a pass that finds nothing new says so.
 *
 * The endpoints live outside the generated OpenAPI schema, so this module calls them with authedFetch
 * the same way jobsService does. When the backend does not have the endpoints yet the server answers
 * 404; that is surfaced as RadarUnavailableError so the UI can say honestly that the API update is
 * not available yet instead of inventing alerts or failing silently.
 */

import { authedFetch, getApiErrorMessage } from '@/services/api'
import { sanitizeErrorMessage } from '@/services/credentialSafety'
import { getJobsBaseUrl } from '@/services/jobsService'

export type RadarAlertStatus = 'New' | 'Acknowledged' | 'Resolved'

export interface RadarAlert {
  id: string
  scanId: string
  repositoryUrl: string
  packageName: string
  packageVersion: string
  ecosystem: string
  vulnId: string
  cveId?: string | null
  severity: string
  summary?: string | null
  fixedVersion?: string | null
  osvUrl: string
  firstSeenAt: string
  lastSeenAt: string
  status: RadarAlertStatus
  acknowledgedAt?: string | null
  resolvedAt?: string | null
}

export interface RadarAlertListResponse {
  items: RadarAlert[]
}

export interface RadarSettings {
  enabled: boolean
  intervalMinutes: number
  lastPassAt?: string | null
}

export interface RadarPassRepoResult {
  repositoryUrl: string
  scanId?: string | null
  componentsChecked: number
  componentsSkipped: number
  newOccurrences: number
  newAlerts: number
  detailsSkipped: number
  error?: string | null
}

export interface RadarPassResult {
  checkedAt: string
  reposChecked: number
  componentsChecked: number
  componentsSkipped: number
  newOccurrences: number
  newAlerts: number
  nothingNew: boolean
  repos: RadarPassRepoResult[]
  message?: string | null
}

/**
 * Thrown when the radar endpoints answer 404. The UI treats this as "the API update is not
 * deployed yet", never as an empty alert list.
 */
export class RadarUnavailableError extends Error {
  constructor() {
    super('Dependency Radar needs the API update — not available yet.')
    this.name = 'RadarUnavailableError'
  }
}

export function isRadarUnavailable(error: unknown): boolean {
  return error instanceof RadarUnavailableError
}

function radarUrl(clientId: string, suffix = ''): string {
  return `${getJobsBaseUrl()}/clients/${encodeURIComponent(clientId)}/radar${suffix}`
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
  try {
    const body = (await res.json()) as unknown
    return getApiErrorMessage(body, fallback)
  } catch {
    return fallback
  }
}

/** Lists the client's radar alerts, newest first. */
export async function listRadarAlerts(clientId: string): Promise<RadarAlert[]> {
  try {
    const res = await authedFetch(radarUrl(clientId, '/alerts'))
    if (res.status === 404) throw new RadarUnavailableError()
    if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load radar alerts.'))
    const body = (await res.json()) as RadarAlertListResponse
    return Array.isArray(body?.items) ? body.items : []
  } catch (error) {
    if (isRadarUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to load radar alerts.'))
  }
}

/**
 * Runs one radar pass right now and returns what it found. `nothingNew` is true only when at
 * least one repository was checked and no new vulnerability appeared; when nothing could be
 * checked the result carries a message instead.
 */
export async function checkRadarNow(clientId: string): Promise<RadarPassResult> {
  try {
    const res = await authedFetch(radarUrl(clientId, '/check-now'), { method: 'POST' })
    if (res.status === 404) throw new RadarUnavailableError()
    if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to run the radar check.'))
    return (await res.json()) as RadarPassResult
  } catch (error) {
    if (isRadarUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to run the radar check.'))
  }
}

/** Reads the client's radar watch settings. */
export async function getRadarSettings(clientId: string): Promise<RadarSettings> {
  try {
    const res = await authedFetch(radarUrl(clientId, '/settings'))
    if (res.status === 404) throw new RadarUnavailableError()
    if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to load radar settings.'))
    return (await res.json()) as RadarSettings
  } catch (error) {
    if (isRadarUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to load radar settings.'))
  }
}

/** Updates the client's radar watch settings. Null fields keep their current value. */
export async function updateRadarSettings(
  clientId: string,
  request: { enabled?: boolean | null; intervalMinutes?: number | null },
): Promise<RadarSettings> {
  try {
    const res = await authedFetch(radarUrl(clientId, '/settings'), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (res.status === 404) throw new RadarUnavailableError()
    if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to update radar settings.'))
    return (await res.json()) as RadarSettings
  } catch (error) {
    if (isRadarUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to update radar settings.'))
  }
}

/** Acknowledges a New alert. Idempotent: acknowledging an already-acknowledged alert returns it unchanged. */
export async function acknowledgeRadarAlert(clientId: string, alertId: string): Promise<RadarAlert> {
  try {
    const res = await authedFetch(radarUrl(clientId, `/alerts/${encodeURIComponent(alertId)}/acknowledge`), {
      method: 'POST',
    })
    if (res.status === 404) throw new RadarUnavailableError()
    if (!res.ok) throw new Error(await readErrorMessage(res, 'Failed to acknowledge the alert.'))
    return (await res.json()) as RadarAlert
  } catch (error) {
    if (isRadarUnavailable(error)) throw error
    throw new Error(sanitizeErrorMessage(error, 'Failed to acknowledge the alert.'))
  }
}

/** One-line summary of a radar pass for display. Never invents numbers — it only formats what the server reported. */
export function radarPassSummary(result: RadarPassResult): string {
  if (result.message) return result.message
  if (result.nothingNew) {
    return `Checked ${result.reposChecked} ${result.reposChecked === 1 ? 'repository' : 'repositories'}, ${result.componentsChecked} components — nothing new.`
  }
  const alerts = result.newAlerts === 1 ? '1 new alert' : `${result.newAlerts} new alerts`
  const occurrences = result.newOccurrences === 1 ? '1 new vulnerability' : `${result.newOccurrences} new vulnerabilities`
  return `Checked ${result.reposChecked} ${result.reposChecked === 1 ? 'repository' : 'repositories'}, ${result.componentsChecked} components — ${occurrences}, ${alerts}.`
}
