// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ReviewerPerformanceCoverage from '../components/ReviewerPerformanceCoverage.vue'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'

const point: PerformancePoint = {
  date: '2026-10-17', windowFrom: '2026-09-18', windowTo: '2026-10-17',
  coverage: { candidatePullRequests: 5, coveredPullRequests: 4, excludedPullRequests: 1, percentage: 80, meetsMinimum: true, recallSupported: true, exclusionIds: [0] },
  score: { summary: { f1: { median: 0.8 } } },
}

describe('measurement coverage summary', () => {
  it('shows captured coverage percentage, minimums and exclusion access outside tooltips', async () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: { point, policy: { minimumCoveragePercentage: 80, minimumCoveredPullRequests: 1 }, label: 'Window end', metric: 'f1' } })
    expect(wrapper.text()).toContain('4 of 5 PRs covered')
    expect(wrapper.text()).toContain('80.0%')
    expect(wrapper.text()).toContain('Advisory minimum: 80% and 1 covered PR')
    expect(wrapper.text()).toContain('2026-09-18 → 2026-10-17')
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('exclusions')).toHaveLength(1)
  })

  it('identifies missing legacy coverage without inventing eligibility', () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: { point: { date: '2026-10-17' }, label: 'Window end', metric: 'f1' } })
    expect(wrapper.text()).toContain('Coverage details were not captured')
    expect(wrapper.find('button').exists()).toBe(false)
  })

  it('shows captured v2 scores and policy as advisory below both minimums', () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: {
      point: { ...point, coverage: { ...point.coverage, meetsMinimum: false, percentage: 60 } },
      policy: { minimumCoveragePercentage: 80, minimumCoveredPullRequests: 10 },
      label: 'Window end', metric: 'f1',
    } })
    expect(wrapper.text()).toContain('F1 is available')
    expect(wrapper.text()).toContain('Advisory minimum: 80% and 10 covered PRs')
    expect(wrapper.text()).toContain('Coverage is below the advisory minimum')
    expect(wrapper.text()).not.toContain('Captured scoring minimum')
  })

  it('shows available F1 below advisory minimums and distinguishes its covered precision', () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: {
      point: { ...point, coverage: { ...point.coverage, meetsMinimum: false, percentage: 20 } },
      policy: { minimumCoveragePercentage: 80, minimumCoveredPullRequests: 10 },
      label: 'Window end', metric: 'f1',
    } })
    expect(wrapper.text()).toContain('F1 is available')
    expect(wrapper.text()).toContain('Coverage is below the advisory minimum')
    expect(wrapper.text()).toContain('F1 uses precision and recall from the same covered PRs')
    expect(wrapper.text()).toContain('The Precision metric uses all supported published outcomes')
    expect(wrapper.text()).not.toContain('F1 unavailable')
  })

  it('keeps available overall precision independent when no sources are covered', () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: {
      point: { ...point,
        score: { counts: { outcomes: { total: 0 } }, summary: {} },
        overallPrecision: { counts: { outcomes: { positive: 2, wrong: 1, total: 3 } }, summary: { median: 2 / 3 } },
        coverage: { ...point.coverage, candidatePullRequests: 5, coveredPullRequests: 0, excludedPullRequests: 5, percentage: 0, meetsMinimum: false },
      }, label: 'Window end', metric: 'precision',
    } })
    expect(wrapper.text()).toContain('Precision is available')
    expect(wrapper.text()).toContain('The Precision metric uses all supported published outcomes')
    expect(wrapper.text()).not.toContain('Scores use the covered PRs')
  })

  it('explains absent scored evidence even when covered PRs fall below advisory minimums', () => {
    const wrapper = mount(ReviewerPerformanceCoverage, { props: {
      point: { ...point, score: { counts: { outcomes: { unknown: 3, total: 3 } }, summary: {}, scenarios: [{ id: 'reviewer-performance-v2/measured', falseNegatives: 0, recall: null, f1: null }] }, coverage: { ...point.coverage, meetsMinimum: false } },
      label: 'Window end', metric: 'f1',
    } })
    expect(wrapper.text()).toContain('no scored outcomes or settled human misses')
    expect(wrapper.text()).not.toContain('Inspect the evidence limitations')
  })

  it.each(['f1', 'recall'] as const)('describes an empty period as an inactive %s denominator', (metric) => {
    const empty: PerformancePoint = {
      date: '2026-10-17',
      score: { summary: {} },
      coverage: {
        candidatePullRequests: 0, coveredPullRequests: 0, excludedPullRequests: 0,
        percentage: null, meetsMinimum: false, recallSupported: true, exclusionIds: [],
      },
      unavailableReasons: ['no-series-observation-in-period'],
    }
    const wrapper = mount(ReviewerPerformanceCoverage, {
      props: { point: empty, label: 'Window end', metric },
    })
    expect(wrapper.text()).toContain(metric === 'f1' ? 'no scored outcomes or settled human misses' : 'no positive scored outcomes or settled human misses')
    expect(wrapper.text()).not.toContain('This report captured coverage below its scoring minimum')
    expect(wrapper.find('button').exists()).toBe(false)
  })

})
