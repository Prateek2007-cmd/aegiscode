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
