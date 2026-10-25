// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * API client for deterministic security scanning.
 * Base: {apiBaseUrl}/clients/{clientId}/security-scans
 * All calls use the authenticated session; the server enforces client roles.
 */
import { authedFetch } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type {
  CycloneDxBom,
  PagedSecurityFindings,
  SecurityFindingSeverity,
  SecurityFindingStatus,
  SecurityScan,
  StartSecurityScanRequest,
  StartSecurityScanResponse,
} from './types'

function baseUrl(clientId: string): string {
  return `${getActiveRuntime().apiBaseUrl}/clients/${clientId}/security-scans`
}

async function throwForStatus(res: Response, what: string): Promise<never> {
  let detail = ''
  try {
    const body = (await res.json()) as { error?: string }
    if (body?.error) detail = `: ${body.error}`
  } catch {
    /* non-JSON error body */
  }
  throw new Error(`${what} failed (${res.status})${detail}`)
}

export async function startSecurityScan(
  clientId: string,
  request: StartSecurityScanRequest,
): Promise<StartSecurityScanResponse> {
  const res = await authedFetch(baseUrl(clientId), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })
  if (!res.ok) await throwForStatus(res, 'Start scan')
  return (await res.json()) as StartSecurityScanResponse
}

export async function listSecurityScans(
  clientId: string,
  limit = 20,
  offset = 0,
): Promise<SecurityScan[]> {
  const res = await authedFetch(`${baseUrl(clientId)}?limit=${limit}&offset=${offset}`)
  if (!res.ok) await throwForStatus(res, 'List scans')
  return (await res.json()) as SecurityScan[]
}

export async function getSecurityScan(clientId: string, scanId: string): Promise<SecurityScan> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}`)
  if (!res.ok) await throwForStatus(res, 'Get scan')
  return (await res.json()) as SecurityScan
}

export interface FindingsQuery {
  severity?: SecurityFindingSeverity
  scanner?: string
  status?: SecurityFindingStatus
  limit?: number
  offset?: number
}

export async function getScanFindings(
  clientId: string,
  scanId: string,
  query: FindingsQuery = {},
): Promise<PagedSecurityFindings> {
  const params = new URLSearchParams()
  if (query.severity) params.set('severity', query.severity)
  if (query.scanner) params.set('scanner', query.scanner)
  if (query.status) params.set('status', query.status)
  params.set('limit', String(query.limit ?? 50))
  params.set('offset', String(query.offset ?? 0))
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}/findings?${params}`)
  if (!res.ok) await throwForStatus(res, 'Get findings')
  return (await res.json()) as PagedSecurityFindings
}

export async function cancelSecurityScan(clientId: string, scanId: string): Promise<void> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}`, { method: 'DELETE' })
  if (!res.ok) await throwForStatus(res, 'Cancel scan')
}

export async function suppressFinding(
  clientId: string,
  scanId: string,
  findingId: string,
  reason: string,
): Promise<void> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}/findings/${findingId}/suppress`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ reason }),
  })
  if (!res.ok) await throwForStatus(res, 'Suppress finding')
}

export async function acknowledgeFinding(
  clientId: string,
  scanId: string,
  findingId: string,
): Promise<void> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}/findings/${findingId}/acknowledge`, {
    method: 'POST',
  })
  if (!res.ok) await throwForStatus(res, 'Acknowledge finding')
}

/**
 * Fetches the CycloneDX SBOM as parsed JSON for in-app visualization.
 * Returns null when the scan produced no SBOM (404) — never throws for that.
 */
export async function getScanSbomJson(
  clientId: string,
  scanId: string,
): Promise<CycloneDxBom | null> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}/sbom`)
  if (res.status === 404) return null
  if (!res.ok) await throwForStatus(res, 'Get SBOM')
  return (await res.json()) as CycloneDxBom
}

/** Downloads a blob (SBOM or report) and triggers a save-as in the browser. */
export async function downloadScanArtifact(
  clientId: string,
  scanId: string,
  kind: 'sbom' | 'report',
): Promise<void> {
  const res = await authedFetch(`${baseUrl(clientId)}/${scanId}/${kind}`)
  if (!res.ok) await throwForStatus(res, `Download ${kind}`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = kind === 'sbom' ? `${scanId}.cyclonedx.json` : `${scanId}.security-report.json`
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}
