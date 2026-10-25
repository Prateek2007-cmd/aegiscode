// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it } from 'vitest'
import { aggregateFindingsByFile, matchSbomComponents } from '../scanInsights'
import { inferLanguages, isOsvRuleId, osvUrl } from '../types'
import type { SecurityFinding } from '../types'

function finding(partial: Partial<SecurityFinding>): SecurityFinding {
  return {
    id: 'f1',
    scanner: 'dep-scan',
    ruleId: null,
    severity: 'High',
    priorityScore: 70,
    scoreGaps: [],
    title: 'test finding',
    category: 'test',
    filePath: null,
    line: null,
    evidence: null,
    remediation: null,
    confidence: 'High',
    status: 'Open',
    firstSeenAt: '2026-10-25T00:00:00Z',
    lastSeenAt: '2026-10-25T00:00:00Z',
    ...partial,
  }
}

describe('inferLanguages', () => {
  it('infers languages from manifest names only', () => {
    const langs = inferLanguages(['package.json', 'requirements.txt', 'pyproject.toml', 'go.mod'])
    expect(langs.map((l) => l.language)).toEqual(['Python', 'JavaScript/TypeScript', 'Go'])
    expect(langs[0].share).toBeCloseTo(0.5)
    expect(langs[0].manifests).toEqual(['requirements.txt', 'pyproject.toml'])
  })

  it('ignores unknown manifests and matches *.csproj', () => {
    const langs = inferLanguages(['App.csproj', 'README.md', 'Dockerfile'])
    expect(langs).toHaveLength(1)
    expect(langs[0].language).toBe('C#')
  })

  it('returns an empty list when nothing matches', () => {
    expect(inferLanguages([])).toEqual([])
    expect(inferLanguages(['Makefile'])).toEqual([])
  })
})

describe('isOsvRuleId / osvUrl', () => {
  it('links GHSA, CVE and OSV ids to osv.dev', () => {
    expect(osvUrl('GHSA-xxxx-yyyy-zzzz')).toBe('https://osv.dev/vulnerability/GHSA-xxxx-yyyy-zzzz')
    expect(osvUrl('CVE-2024-12345')).toBe('https://osv.dev/vulnerability/CVE-2024-12345')
    expect(osvUrl('OSV-2024-1')).toBe('https://osv.dev/vulnerability/OSV-2024-1')
  })

  it('does not link internal rule ids', () => {
    expect(isOsvRuleId('SECRET-001')).toBe(false)
    expect(osvUrl('dep-scan/vuln')).toBeNull()
  })
})

describe('matchSbomComponents', () => {
  it('matches by component name and keeps unmatched components neutral', () => {
    const matches = matchSbomComponents(
      [{ name: 'lodash', version: '4.17.21' }, { name: 'left-pad', version: '1.3.0' }],
      [
        finding({ title: 'lodash prototype pollution', ruleId: 'GHSA-xxxx-yyyy-zzzz', severity: 'High' }),
        finding({ title: 'lodash ReDoS', ruleId: 'CVE-2024-12345', severity: 'Critical' }),
      ],
    )
    expect(matches[0].maxSeverity).toBe('Critical')
    expect(matches[0].vulnCount).toBe(2)
    expect(matches[0].topRuleId).toBe('CVE-2024-12345')
    expect(matches[1].maxSeverity).toBeNull()
    expect(matches[1].vulnCount).toBe(0)
  })
})

describe('aggregateFindingsByFile', () => {
  it('groups files without a path under "(no file)" and ranks by count', () => {
    const entries = aggregateFindingsByFile([
      finding({ filePath: 'a.ts', severity: 'Low' }),
      finding({ filePath: 'a.ts', severity: 'High' }),
      finding({ filePath: null, severity: 'Medium' }),
    ])
    expect(entries[0]).toEqual({ filePath: 'a.ts', count: 2, maxSeverity: 'High' })
    expect(entries[1]).toEqual({ filePath: '(no file)', count: 1, maxSeverity: 'Medium' })
  })
})
