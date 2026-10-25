// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Pure presentation helpers for the per-client dashboard. No API calls, no
 * invented data: every helper formats a value the view model already loaded,
 * and falls back to an honest placeholder ('—' / 'unknown') when it is absent.
 */
import type { SecurityScanStatus, SecurityScanSummary } from '@/features/security-scans/types'
import { SEVERITY_ORDER } from '@/features/security-scans/types'

/** Short repo label for a scan row: "owner/repo" when the URL carries a path, else the URL itself. */
export function repoShortName(repositoryUrl: string): string {
  const trimmed = repositoryUrl.trim()
  if (!trimmed) return 'unknown repository'
  const withoutScheme = trimmed
    .replace(/^[a-z][a-z0-9+.-]*:\/\//i, '')
    // SSH form: git@github.com:owner/repo — drop the user@host: prefix.
    .replace(/^[^/:@\s]+@[^/:@\s]+:/, '')
  const withoutGit = withoutScheme.replace(/\.git$/i, '')
  const parts = withoutGit.split('/').filter((part) => part.length > 0)
  if (parts.length >= 2) {
    // Drop the host (github.com, gitlab.com, …) and keep owner/repo.
    return parts.slice(-2).join('/')
  }
  return parts[0] ?? trimmed
}

/** Locale date/time for a dashboard timestamp, or '—' when missing or unparseable. */
export function formatDashboardDate(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '—'
  return date.toLocaleString()
}

/** A count that may still be unknown (null/undefined) renders as '—', never as 0. */
export function formatNullableCount(value: number | null | undefined): string {
  return value == null ? '—' : String(value)
}

/** True while a scan is still in flight (queued or running). */
export function isScanActive(status: SecurityScanStatus): boolean {
  return status === 'Queued' || status === 'Running'
}

export interface SeverityChip {
  severity: (typeof SEVERITY_ORDER)[number]
  /** Single-letter chip label: C / H / M / L / I. */
  label: string
  count: number
}

const SEVERITY_LETTERS: Record<(typeof SEVERITY_ORDER)[number], string> = {
  Critical: 'C',
  High: 'H',
  Medium: 'M',
  Low: 'L',
  Info: 'I',
}

/** One chip per severity, in severity order, for a scan summary. */
export function scanSummaryChips(summary: SecurityScanSummary): SeverityChip[] {
  return SEVERITY_ORDER.map((severity) => ({
    severity,
    label: SEVERITY_LETTERS[severity],
    count: summary[severity.toLowerCase() as keyof SecurityScanSummary],
  }))
}

/** Icon class for an activity row, by kind. */
export function activityKindIcon(kind: 'scan' | 'review'): string {
  return kind === 'scan' ? 'fi fi-rr-shield-check' : 'fi fi-rr-search'
}

/** Plain-language label for an activity row, by kind. */
export function activityKindLabel(kind: 'scan' | 'review'): string {
  return kind === 'scan' ? 'Scan' : 'Review'
}
