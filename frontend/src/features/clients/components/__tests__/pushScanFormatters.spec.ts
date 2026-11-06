// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  describePushScanEntry,
  describePushScanOutcome,
  formatBranchFilter,
  parseBranchFilter,
  shortPushSha,
} from '@/features/clients/components/pushScanFormatters'

describe('describePushScanOutcome', () => {
  it('labels started as a scan started', () => {
    expect(describePushScanOutcome('started')).toEqual({ label: 'Scan started', tone: 'success' })
  })

  it('labels pending as starting', () => {
    expect(describePushScanOutcome('pending')).toEqual({ label: 'Scan starting…', tone: 'neutral' })
  })

  it('labels failed as could not start', () => {
    expect(describePushScanOutcome('failed')).toEqual({ label: 'Could not start scan', tone: 'failure' })
  })

  it('renders unknown and missing outcomes as no scan recorded, never as a scan', () => {
    expect(describePushScanOutcome('weird')).toEqual({ label: 'No scan recorded', tone: 'neutral' })
    expect(describePushScanOutcome(undefined)).toEqual({ label: 'No scan recorded', tone: 'neutral' })
    expect(describePushScanOutcome(null)).toEqual({ label: 'No scan recorded', tone: 'neutral' })
  })
})

describe('shortPushSha', () => {
  it('shortens a full SHA to seven characters', () => {
    expect(shortPushSha('abc1234567890abcdef')).toBe('abc1234')
  })

  it('leaves short values untouched', () => {
    expect(shortPushSha('abc1234')).toBe('abc1234')
  })
})

describe('parseBranchFilter', () => {
  it('splits comma-separated branches and trims them', () => {
    expect(parseBranchFilter('main, release/* , develop')).toEqual(['main', 'release/*', 'develop'])
  })

  it('resets empty input to main only', () => {
    expect(parseBranchFilter('')).toEqual(['main'])
    expect(parseBranchFilter('  ,  ')).toEqual(['main'])
  })
})

describe('formatBranchFilter', () => {
  it('joins branches with a comma and a space', () => {
    expect(formatBranchFilter(['main', 'release/*'])).toBe('main, release/*')
  })

  it('renders missing branches as main only', () => {
    expect(formatBranchFilter(null)).toBe('main')
    expect(formatBranchFilter([])).toBe('main')
  })
})

describe('describePushScanEntry', () => {
  it('names the repo, branch, short SHA, and pusher', () => {
    expect(
      describePushScanEntry({
        repositoryUrl: 'https://github.com/acme/web',
        branch: 'main',
        headSha: 'abc1234567890abcdef',
        pusher: 'prateek',
      }),
    ).toBe('https://github.com/acme/web — main at abc1234 by prateek')
  })

  it('omits the pusher when unknown', () => {
    expect(
      describePushScanEntry({
        repositoryUrl: 'https://github.com/acme/web',
        branch: 'main',
        headSha: 'abc1234567890abcdef',
      }),
    ).toBe('https://github.com/acme/web — main at abc1234')
  })
})
