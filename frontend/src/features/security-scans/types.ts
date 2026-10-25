// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Types for deterministic security scanning. Mirror the C# DTOs in
 * MeisterDev.ProPR.SecurityScanning/Models/SecurityScanDtos.cs — keep them in sync.
 */

export type SecurityScanStatus =
  | 'Queued'
  | 'Running'
  | 'Succeeded'
  | 'PartiallySucceeded'
  | 'Failed'
  | 'Cancelled'

export type SecurityFindingSeverity = 'Critical' | 'High' | 'Medium' | 'Low' | 'Info'

export type SecurityFindingStatus = 'Open' | 'Acknowledged' | 'Suppressed' | 'Resolved'

export interface SecurityScanCoverage {
  manifestsFound: string[]
  filesScanned: number
  scannersRun: string[]
  scannersFailed: string[]
  scannersSkipped: string[]
}

export interface SecurityScanSummary {
  critical: number
  high: number
  medium: number
  low: number
  info: number
}

export interface SecurityScan {
  scanId: string
  clientId: string
  repositoryUrl: string
  ref?: string | null
  status: SecurityScanStatus
  progressPct: number
  queuedAt: string
  startedAt?: string | null
  finishedAt?: string | null
  error?: string | null
  coverage?: SecurityScanCoverage | null
  summary: SecurityScanSummary
}

export interface SecurityFinding {
  id: string
  scanner: string
  ruleId?: string | null
  severity: SecurityFindingSeverity
  priorityScore: number
  scoreGaps: string[]
  title: string
  category: string
  filePath?: string | null
  line?: number | null
  evidence?: string | null
  remediation?: string | null
  confidence: string
  status: SecurityFindingStatus
  firstSeenAt: string
  lastSeenAt: string
}

export interface PagedSecurityFindings {
  items: SecurityFinding[]
  totalCount: number
}

/** Minimal CycloneDX 1.5 shape returned by GET /{scanId}/sbom. */
export interface CycloneDxComponent {
  type?: string
  name?: string
  version?: string
  purl?: string
}

export interface CycloneDxBom {
  bomFormat?: string
  specVersion?: string
  components?: CycloneDxComponent[]
}

/** One client-side poll snapshot. NOT an engine log — recorded in this browser session. */
export interface PollSnapshot {
  at: string
  status: SecurityScanStatus
  progressPct: number
}

export interface InferredLanguage {
  language: string
  manifests: string[]
  /** Share of matched manifests, 0–1. */
  share: number
}

export interface StartSecurityScanRequest {
  repositoryUrl: string
  ref?: string
  scanners?: string[]
}

export interface StartSecurityScanResponse {
  scanId: string
  status: string
}

export const SECURITY_SCANNERS = ['dep-scan', 'secret-scan', 'package-risk', 'sbom'] as const

export const SEVERITY_ORDER: SecurityFindingSeverity[] = ['Critical', 'High', 'Medium', 'Low', 'Info']

/** Display metadata for the four deterministic scanners. */
export const SCANNER_META: Record<string, { label: string; blurb: string }> = {
  'dep-scan': { label: 'Dependencies', blurb: 'OSV.dev vulnerability scan' },
  'secret-scan': { label: 'Secrets', blurb: 'Leaked credential sweep' },
  'package-risk': { label: 'Package Risk', blurb: 'Supply-chain risk signals' },
  sbom: { label: 'SBOM', blurb: 'CycloneDX inventory' },
}

export const SCANNER_LABELS: Record<string, string> = Object.fromEntries(
  Object.entries(SCANNER_META).map(([id, meta]) => [id, meta.label]),
)

/** True when a rule id is a real vulnerability identifier we can link to OSV.dev. */
export function isOsvRuleId(ruleId: string): boolean {
  return /^(GHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}|CVE-\d{4}-\d{4,7}|OSV-\d{4}-\d+)$/i.test(ruleId.trim())
}

export function osvUrl(ruleId: string): string | null {
  return isOsvRuleId(ruleId) ? `https://osv.dev/vulnerability/${ruleId.trim()}` : null
}

const MANIFEST_LANGUAGE_MAP: Array<[RegExp, string]> = [
  [/^(package\.json|package-lock\.json|yarn\.lock|pnpm-lock\.yaml)$/i, 'JavaScript/TypeScript'],
  [/^(requirements\.txt|pyproject\.toml|Pipfile|setup\.py)$/i, 'Python'],
  [/^(go\.mod|go\.sum)$/i, 'Go'],
  [/^(Cargo\.toml|Cargo\.lock)$/i, 'Rust'],
  [/^(composer\.json|composer\.lock)$/i, 'PHP'],
  [/\.csproj$/i, 'C#'],
]

/**
 * Infer languages from manifest file names only. Never invents languages the
 * manifests don't evidence; manifests with no known mapping are ignored.
 */
export function inferLanguages(manifestsFound: string[]): InferredLanguage[] {
  const byLanguage = new Map<string, string[]>()
  for (const manifest of manifestsFound) {
    const base = manifest.split('/').pop() ?? manifest
    const match = MANIFEST_LANGUAGE_MAP.find(([re]) => re.test(base))
    if (!match) continue
    const list = byLanguage.get(match[1]) ?? []
    list.push(manifest)
    byLanguage.set(match[1], list)
  }
  const total = [...byLanguage.values()].reduce((n, list) => n + list.length, 0)
  if (total === 0) return []
  return [...byLanguage.entries()]
    .map(([language, manifests]) => ({ language, manifests, share: manifests.length / total }))
    .sort((a, b) => b.manifests.length - a.manifests.length)
}

export function severityColor(severity: SecurityFindingSeverity): string {
  switch (severity) {
    case 'Critical': return '#f43f5e'
    case 'High': return '#fb7185'
    case 'Medium': return '#f59e0b'
    case 'Low': return '#38bdf8'
    case 'Info': return '#94a3b8'
  }
}

export function scanStatusColor(status: SecurityScanStatus): string {
  switch (status) {
    case 'Succeeded': return '#10b981'
    case 'PartiallySucceeded': return '#f59e0b'
    case 'Running': return '#38bdf8'
    case 'Queued': return '#94a3b8'
    case 'Failed': return '#f43f5e'
    case 'Cancelled': return '#64748b'
  }
}

/** Human-readable explanation of what a scan status means. Never implies more than the backend reported. */
export function scanStatusHint(scan: SecurityScan): string {
  switch (scan.status) {
    case 'Queued': return 'Waiting for a worker.'
    case 'Running': return `Scanning… ${scan.progressPct}%`
    case 'Succeeded': return 'All scanners completed.'
    case 'PartiallySucceeded':
      return 'Completed with gaps — check coverage for skipped or failed scanners.'
    case 'Failed': return scan.error ?? 'Scan failed.'
    case 'Cancelled': return 'Cancelled.'
  }
}
