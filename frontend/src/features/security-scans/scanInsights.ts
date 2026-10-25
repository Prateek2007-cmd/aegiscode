// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Pure helpers for the security dashboard: joining SBOM components to dep-scan
 * findings, and aggregating findings by file. No API calls, no invented data.
 */
import type {
  CycloneDxComponent,
  SecurityFinding,
  SecurityFindingSeverity,
} from './types'
import { SEVERITY_ORDER } from './types'

export interface SbomComponentMatch {
  component: CycloneDxComponent
  displayName: string
  version: string
  /** Max severity of matched dep-scan findings, or null when nothing matched. */
  maxSeverity: SecurityFindingSeverity | null
  vulnCount: number
  /** Rule id of the highest-severity match, if any. */
  topRuleId: string | null
}

/** Display name for a CycloneDX component: explicit name first, else the purl tail. */
export function componentDisplayName(component: CycloneDxComponent): string {
  if (component.name) return component.name
  const purl = component.purl ?? ''
  const tail = purl.split('/').pop() ?? ''
  return tail.split('@')[0] || 'unknown component'
}

export function componentVersion(component: CycloneDxComponent): string {
  if (component.version) return component.version
  const purl = component.purl ?? ''
  const at = purl.lastIndexOf('@')
  return at >= 0 ? purl.slice(at + 1) : ''
}

function severityRank(severity: SecurityFindingSeverity): number {
  return SEVERITY_ORDER.indexOf(severity)
}

/**
 * Conservative join: a finding matches a component when the finding's title or
 * rule id contains the component name (case-insensitive, name at least 3 chars).
 * Unmatched components stay neutral — never attributed a vulnerability they
 * don't have.
 */
export function matchSbomComponents(
  components: CycloneDxComponent[],
  depScanFindings: SecurityFinding[],
): SbomComponentMatch[] {
  return components.map((component) => {
    const name = componentDisplayName(component).toLowerCase()
    let maxSeverity: SecurityFindingSeverity | null = null
    let vulnCount = 0
    let topRuleId: string | null = null
    if (name.length >= 3) {
      for (const f of depScanFindings) {
        const hay = `${f.title} ${f.ruleId ?? ''}`.toLowerCase()
        if (!hay.includes(name)) continue
        vulnCount += 1
        if (maxSeverity === null || severityRank(f.severity) < severityRank(maxSeverity)) {
          maxSeverity = f.severity
          topRuleId = f.ruleId ?? null
        }
      }
    }
    return {
      component,
      displayName: componentDisplayName(component),
      version: componentVersion(component),
      maxSeverity,
      vulnCount,
      topRuleId,
    }
  })
}

export interface FileHeatEntry {
  filePath: string
  count: number
  maxSeverity: SecurityFindingSeverity
}

/** Aggregate findings by file, top N, ranked by count. Files without a path group under "(no file)". */
export function aggregateFindingsByFile(
  findings: SecurityFinding[],
  topN = 8,
): FileHeatEntry[] {
  const byFile = new Map<string, { count: number; maxSeverity: SecurityFindingSeverity }>()
  for (const f of findings) {
    const key = f.filePath?.trim() ? f.filePath : '(no file)'
    const entry = byFile.get(key) ?? { count: 0, maxSeverity: f.severity }
    entry.count += 1
    if (severityRank(f.severity) < severityRank(entry.maxSeverity)) entry.maxSeverity = f.severity
    byFile.set(key, entry)
  }
  return [...byFile.entries()]
    .map(([filePath, v]) => ({ filePath, count: v.count, maxSeverity: v.maxSeverity }))
    .sort((a, b) => b.count - a.count)
    .slice(0, topN)
}

/** Total findings in a scan summary. */
export function summaryTotal(summary: {
  critical: number
  high: number
  medium: number
  low: number
  info: number
}): number {
  return summary.critical + summary.high + summary.medium + summary.low + summary.info
}
