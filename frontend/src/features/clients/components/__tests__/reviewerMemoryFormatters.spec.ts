// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
    countsText,
    describeMemory,
    mutedDetail,
    sourceLabel,
    statusLabel,
} from '../reviewerMemoryFormatters'
import type { ReviewerMemoryEntry } from '@/services/reviewerMemoryService'

function entry(overrides: Partial<ReviewerMemoryEntry> = {}): ReviewerMemoryEntry {
    return {
        ruleKey: 'review:avoid console.writeline',
        label: 'Avoid Console.WriteLine.',
        source: 'review',
        accepts: 0,
        dismissals: 5,
        muted: true,
        mutedAt: '2026-11-06T00:00:00Z',
        mutedReason: 'Dismissed 5 times with no accepts.',
        firstSeenAt: '2026-11-05T00:00:00Z',
        lastOutcomeAt: '2026-11-06T00:00:00Z',
        ...overrides,
    }
}

describe('sourceLabel', () => {
    it('labels known sources', () => {
        expect(sourceLabel('review')).toBe('Review')
        expect(sourceLabel('scan')).toBe('Scan')
    })

    it('passes unknown sources through unchanged', () => {
        expect(sourceLabel('other')).toBe('other')
    })
})

describe('statusLabel', () => {
    it('reports muted and active rules', () => {
        expect(statusLabel(entry())).toBe('Muted')
        expect(statusLabel(entry({ muted: false }))).toBe('Active')
    })
})

describe('countsText', () => {
    it('handles singular and plural', () => {
        expect(countsText(entry({ accepts: 1, dismissals: 1 }))).toBe('1 accept · 1 dismissal')
        expect(countsText(entry({ accepts: 0, dismissals: 5 }))).toBe('0 accepts · 5 dismissals')
    })
})

describe('mutedDetail', () => {
    it('returns the mute reason for muted rules', () => {
        expect(mutedDetail(entry())).toBe('Dismissed 5 times with no accepts.')
    })

    it('falls back when a muted rule has no recorded reason', () => {
        expect(mutedDetail(entry({ mutedReason: null }))).toBe('Muted by reviewer memory.')
        expect(mutedDetail(entry({ mutedReason: '   ' }))).toBe('Muted by reviewer memory.')
    })

    it('returns null for active rules', () => {
        expect(mutedDetail(entry({ muted: false }))).toBeNull()
    })
})

describe('describeMemory', () => {
    it('reports empty memory honestly', () => {
        expect(describeMemory([])).toBe('Nothing learned yet')
    })

    it('counts learned rules and muted rules', () => {
        expect(describeMemory([entry(), entry({ ruleKey: 'review:other', muted: false })])).toBe(
            '2 rules learned · 1 muted',
        )
    })

    it('omits the muted count when nothing is muted', () => {
        expect(describeMemory([entry({ muted: false })])).toBe('1 rule learned')
    })
})
