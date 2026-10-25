// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  activityKindIcon,
  activityKindLabel,
  formatDashboardDate,
  formatNullableCount,
  isScanActive,
  repoShortName,
  scanSummaryChips,
} from '../clientDashboardFormatters'

describe('repoShortName', () => {
  it('keeps owner/repo for a GitHub URL', () => {
    expect(repoShortName('https://github.com/Prateek2007-cmd/vulnveto')).toBe('Prateek2007-cmd/vulnveto')
  })

  it('strips a trailing .git', () => {
    expect(repoShortName('https://github.com/Prateek2007-cmd/ACM.git')).toBe('Prateek2007-cmd/ACM')
  })

  it('keeps owner/repo for an SSH URL', () => {
    expect(repoShortName('git@github.com:Prateek2007-cmd/aegiscode.git')).toBe('Prateek2007-cmd/aegiscode')
  })

  it('falls back to the URL when it carries no owner/repo path', () => {
    expect(repoShortName('https://example.com')).toBe('example.com')
  })

  it('says unknown repository for a blank URL', () => {
    expect(repoShortName('   ')).toBe('unknown repository')
  })
})

describe('formatDashboardDate', () => {
  it('renders a valid ISO timestamp', () => {
    const rendered = formatDashboardDate('2026-10-25T21:17:00.000Z')
    expect(rendered).not.toBe('—')
    expect(rendered).toContain('2026')
  })

  it('renders a dash for missing or unparseable input', () => {
    expect(formatDashboardDate(null)).toBe('—')
    expect(formatDashboardDate(undefined)).toBe('—')
    expect(formatDashboardDate('not-a-date')).toBe('—')
  })
})

describe('formatNullableCount', () => {
  it('renders a real count, including zero', () => {
    expect(formatNullableCount(3)).toBe('3')
    expect(formatNullableCount(0)).toBe('0')
  })

  it('renders a dash when the count is unknown', () => {
    expect(formatNullableCount(null)).toBe('—')
    expect(formatNullableCount(undefined)).toBe('—')
  })
})

describe('isScanActive', () => {
  it('is true for queued and running scans', () => {
    expect(isScanActive('Queued')).toBe(true)
    expect(isScanActive('Running')).toBe(true)
  })

  it('is false for finished scans', () => {
    expect(isScanActive('Succeeded')).toBe(false)
    expect(isScanActive('Failed')).toBe(false)
    expect(isScanActive('Cancelled')).toBe(false)
    expect(isScanActive('PartiallySucceeded')).toBe(false)
  })
})

describe('scanSummaryChips', () => {
  it('returns one chip per severity in severity order', () => {
    const chips = scanSummaryChips({ critical: 1, high: 2, medium: 0, low: 3, info: 4 })
    expect(chips.map((chip) => chip.label)).toEqual(['C', 'H', 'M', 'L', 'I'])
    expect(chips.map((chip) => chip.count)).toEqual([1, 2, 0, 3, 4])
  })
})

describe('activity kind helpers', () => {
  it('labels scans and reviews distinctly', () => {
    expect(activityKindLabel('scan')).toBe('Scan')
    expect(activityKindLabel('review')).toBe('Review')
    expect(activityKindIcon('scan')).toContain('shield')
    expect(activityKindIcon('review')).toContain('search')
  })
})
