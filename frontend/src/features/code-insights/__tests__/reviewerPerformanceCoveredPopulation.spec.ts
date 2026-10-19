// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, describe, expect, it, vi } from 'vitest'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'
import { connectedSegments, metricRange, metricCounts, metricScenarios, pointAvailability, pairedDelta } from '../reviewerPerformanceRanges'
import { prepareEvidence } from '../reviewerPerformanceEvidence'
import { prepareExplorerMatrix } from '../reviewerPerformanceExplorer'
import { mockPerformanceResponse } from '@/mocks/reviewerPerformance'

const range = (median: number) => ({ minimum: median, firstQuartile: median, median, thirdQuartile: median, maximum: median })
const point: PerformancePoint = {
  date: '2026-10-17',
  score: {
    counts: { outcomes: { positive: 4, wrong: 0, total: 4 } },
    summary: { precision: range(1), f1: range(0.8) },
    scenarios: [{ id: 'premise', precision: 1, f1: 0.8 }],
  },
  overallPrecision: {
    counts: { outcomes: { positive: 4, wrong: 1, total: 5 } },
    summary: range(0.8),
    scenarios: [{ id: 'premise', precision: 0.8 }],
  },
  coverage: {
    candidatePullRequests: 5, coveredPullRequests: 4, excludedPullRequests: 1,
    percentage: 80, meetsMinimum: true, recallSupported: true,
    reasons: [{ reason: 'miss-collection-coverage-unavailable', pullRequests: 1 }], exclusionIds: [0],
  },
  unavailableReasons: ['miss-collection-coverage-unavailable'],
}

describe('covered and overall measurement presentation', () => {
  afterEach(() => vi.useRealTimers())
  it('uses overall precision in ranges, scenarios, counts and matrix cells', () => {
    expect(metricRange(point, 'precision')?.median).toBe(0.8)
    expect(metricRange(point, 'f1')?.median).toBe(0.8)
    expect(metricCounts(point, 'precision')?.outcomes?.total).toBe(5)
    expect(metricCounts(point, 'f1')?.outcomes?.total).toBe(4)
    expect(metricScenarios(point, 'precision')[0]?.precision).toBe(0.8)
    const matrix = prepareExplorerMatrix([{ rowId: 'a', columnId: 'b', measurement: point }], 'precision')
    expect(matrix.rows[0]?.cells[0]?.median).toBe('80.0%')
    expect(matrix.rows[0]?.cells[0]?.publications).toBe(5)
    const compared = { ...point, overallPrecision: { ...point.overallPrecision, scenarios: [{ id: 'premise', precision: 0.9 }] } }
    expect(pairedDelta(metricScenarios(point, 'precision'), metricScenarios(compared, 'precision'), 'precision')?.median).toBeCloseTo(0.1)
  })

  it('does not fall back to covered precision when v2 overall precision is unavailable', () => {
    const missing = { ...point, overallPrecision: { counts: {}, scenarios: [], summary: undefined } }
    expect(metricRange(missing, 'precision')).toBeUndefined()
    expect(connectedSegments([missing], 'precision')).toEqual([])
    expect(metricScenarios(missing, 'precision')).toEqual([])
    expect(metricRange({ ...point, overallPrecision: undefined }, 'precision')).toBeUndefined()
  })

  it('retains stored v1 precision when independent precision is absent', () => {
    const legacy = { ...point, overallPrecision: undefined, coverage: undefined }
    expect(metricRange(legacy, 'precision')?.median).toBe(1)
    expect(metricCounts(legacy, 'precision')?.outcomes?.total).toBe(4)
  })

  it('explains v2 exclusions without calling a supported F1 unavailable', () => {
    const evidence = prepareEvidence(point)!
    expect(evidence.metrics[0]?.range).toBe('80.0%–80.0%')
    expect(evidence.unavailableReasons[0]?.label).toContain('Excluded')
    expect(evidence.unavailableReasons[0]?.label).not.toContain('F1 unavailable')
  })

  it('distinguishes unsupported evidence from an inactive date and treats zero as available', () => {
    const blocked: PerformancePoint = { ...point, score: { summary: {} }, overallPrecision: {}, unavailableReasons: ['no-series-observation-in-period'], coverage: { ...point.coverage, meetsMinimum: false } }
    expect(pointAvailability(blocked, 'f1')).toBe('unavailable')
    expect(pointAvailability(blocked, 'precision')).toBe('inactive')
    expect(pointAvailability({ ...blocked, coverage: { candidatePullRequests: 0 }, unavailableReasons: ['no-series-observation-in-period'] }, 'f1')).toBe('inactive')
    expect(pointAvailability({ ...point, score: { summary: { f1: range(0) } } }, 'f1')).toBe('available')
  })

  it('keeps empty denominators distinct when all published outcomes are undecided', () => {
    const undecided: PerformancePoint = {
      ...point,
      score: { counts: { outcomes: { unknown: 3, total: 3 } }, summary: {}, scenarios: [{ id: 'measured', falseNegatives: 0, recall: null, f1: null }] },
      overallPrecision: { counts: { outcomes: { unknown: 5, total: 5 } }, scenarios: [] },
      coverage: { ...point.coverage, meetsMinimum: false },
      unavailableReasons: [],
    }
    expect(pointAvailability(undecided, 'precision')).toBe('inactive')
    expect(pointAvailability(undecided, 'f1')).toBe('inactive')
    const unavailableInputs = { ...undecided, score: { ...undecided.score, scenarios: [{ id: 'unavailable', falseNegatives: null, recall: null, f1: null }] } }
    expect(pointAvailability(unavailableInputs, 'f1')).toBe('unavailable')
    expect(prepareEvidence({ ...undecided, unavailableReasons: ['coverage-below-minimum'] })!.unavailableReasons[0]!.label).toBe('Coverage is below the captured percentage minimum.')
    expect(pointAvailability({ ...undecided, coverage: point.coverage }, 'f1')).toBe('inactive')
    expect(pointAvailability({ ...undecided, coverage: { ...point.coverage, excludedPullRequests: 0, meetsMinimum: true } }, 'f1')).toBe('inactive')
    expect(pointAvailability({ ...undecided, coverage: { ...point.coverage, recallSupported: false } }, 'f1')).toBe('unavailable')
  })

  it('retains realistic mock exclusions and cumulative covered F1 independently of overall precision', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const to = new Date().toISOString().slice(0, 10)
    const from = new Date(Date.now() - 29 * 86400000).toISOString().slice(0, 10)
    const query = { views: [{ from, to }], grouping: 'none', aggregation: 'cumulative' }
    const response = mockPerformanceResponse(query)
    const view = response.views![0]!
    const latest = view.series![0]!.points!.at(-1)!
    expect(response.calculationVersion).toBe('reviewer-performance-v2')
    expect(response.premiseIds!.every((id) => id.startsWith('reviewer-performance-v2/'))).toBe(true)
    expect(latest.score!.scenarios!.every((scenario) => scenario.id!.startsWith('reviewer-performance-v2/'))).toBe(true)
    expect(latest.overallPrecision!.scenarios!.every((scenario) => scenario.id!.startsWith('reviewer-performance-v2/'))).toBe(true)
    expect(latest.coverage?.excludedPullRequests).toBeGreaterThan(0)
    expect(latest.coverage?.meetsMinimum).toBe(true)
    expect(metricRange(latest, 'f1')).toBeDefined()
    expect(metricCounts(latest, 'precision')!.outcomes!.total!).toBeGreaterThan(metricCounts(latest, 'f1')!.outcomes!.total!)
    const allIds = new Set(view.exclusionCatalog?.map((entry) => entry.id))
    expect(latest.coverage?.exclusionIds?.every((id) => allIds.has(id))).toBe(true)
    const repeated = mockPerformanceResponse(query)
    expect(repeated.views![0]!.exclusionCatalog).toEqual(view.exclusionCatalog)
    expect(new Set(view.exclusionCatalog!.map((entry) => `${entry.aggregateId}|${entry.providerScope}`)).size).toBe(view.exclusionCatalog!.length)
    for (const reason of latest.coverage!.reasons ?? []) {
      expect(reason.pullRequests).toBe(view.exclusionCatalog!.filter((entry) => latest.coverage!.exclusionIds!.includes(entry.id!) && entry.reasons!.includes(reason.reason!)).length)
    }
    const period = mockPerformanceResponse({ ...query, aggregation: 'period' }).views![0]!.series![0]!.points!
    expect(period.some((point) => (point.coverage?.excludedPullRequests ?? 0) > 0 && !point.coverage?.meetsMinimum && metricRange(point, 'f1') != null)).toBe(true)
  })
})
