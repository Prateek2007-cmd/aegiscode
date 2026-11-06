// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  describeBlockingFindings,
  describeGuardDecision,
  guardThresholdOptions,
} from '@/features/clients/components/mergeGuardFormatters'

describe('describeGuardDecision', () => {
  it('labels success as passed', () => {
    expect(describeGuardDecision('success')).toEqual({ label: 'Passed', tone: 'success' })
  })

  it('labels failure as failed', () => {
    expect(describeGuardDecision('failure')).toEqual({ label: 'Failed — merging should wait', tone: 'failure' })
  })

  it('labels neutral as could not decide', () => {
    expect(describeGuardDecision('neutral')).toEqual({ label: 'Could not decide', tone: 'neutral' })
  })

  it('renders unknown and missing conclusions as no check reported, never as a pass', () => {
    expect(describeGuardDecision('weird')).toEqual({ label: 'No check reported', tone: 'neutral' })
    expect(describeGuardDecision(undefined)).toEqual({ label: 'No check reported', tone: 'neutral' })
    expect(describeGuardDecision(null)).toEqual({ label: 'No check reported', tone: 'neutral' })
  })
})

describe('guardThresholdOptions', () => {
  it('offers Critical, High, Medium, Low in that order', () => {
    expect(guardThresholdOptions.map((option) => option.value)).toEqual([
      'Critical',
      'High',
      'Medium',
      'Low',
    ])
  })
})

describe('describeBlockingFindings', () => {
  it('says no blocking findings when both counts are zero', () => {
    expect(describeBlockingFindings(0, 0)).toBe('No blocking findings.')
  })

  it('combines scan and review counts', () => {
    expect(describeBlockingFindings(2, 1)).toBe(
      '3 blocking findings (2 from the security scan, 1 from the review).',
    )
  })

  it('uses the singular for one finding', () => {
    expect(describeBlockingFindings(1, 0)).toBe('1 blocking finding (1 from the security scan).')
  })
})
