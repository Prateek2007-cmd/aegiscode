// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
    countFindingsByLens,
    filterFindingsByLens,
    flattenPublishedFindings,
    formatDurationMs,
    lensBucket,
    parseVerificationFunnel,
    type PublishedFinding,
    type ReviewJobProtocolDto,
} from '@/features/reviews/utils/reviewDepth'

function pass(overrides: Partial<ReviewJobProtocolDto> = {}): ReviewJobProtocolDto {
    return { ...overrides }
}

function gateEvent(outputSummary: string) {
    return { name: 'review_finding_gate_summary', outputSummary }
}

describe('parseVerificationFunnel', () => {
    it('parses the final-gate summary record from an event output', () => {
        const funnel = parseVerificationFunnel([
            pass({
                events: [
                    gateEvent(JSON.stringify({ candidateCount: 12, publishCount: 7, dropCount: 3, summaryOnlyCount: 2 })),
                ],
            }),
        ])
        expect(funnel).toEqual({ candidateCount: 12, publishCount: 7, dropCount: 3, summaryOnlyCount: 2 })
    })

    it('returns null when no final-gate event is recorded, never invented zeros', () => {
        expect(parseVerificationFunnel([pass({ events: [{ name: 'ai_call_iter_1' }] })])).toBeNull()
        expect(parseVerificationFunnel([])).toBeNull()
    })

    it('ignores malformed event payloads and partial records without any counts', () => {
        expect(parseVerificationFunnel([
            pass({ events: [gateEvent('not json')] }),
        ])).toBeNull()
        expect(parseVerificationFunnel([
            pass({ events: [gateEvent(JSON.stringify({ categoryCounts: { a: 1 } }))] }),
        ])).toBeNull()
    })

    it('accepts a record carried in the input sample with an args= prefix', () => {
        const funnel = parseVerificationFunnel([
            pass({
                events: [{
                    name: 'review_finding_gate_summary',
                    inputTextSample: 'args=' + JSON.stringify({ candidateCount: 5, publishCount: 5, dropCount: 0, summaryOnlyCount: 0 }),
                }],
            }),
        ])
        expect(funnel?.candidateCount).toBe(5)
    })
})

describe('lens buckets', () => {
    const findings: PublishedFinding[] = [
        { key: 'a', filePath: 'a.ts', lineNumber: 1, severity: 'High', message: 'sec', originPassLens: 'security', originPassLabel: 'Security', passIndex: 0 },
        { key: 'b', filePath: 'b.ts', lineNumber: 2, severity: 'Medium', message: 'prorv', originPassLens: 'prorv', originPassLabel: 'ProRV', passIndex: 1 },
        { key: 'c', filePath: 'c.ts', lineNumber: 3, severity: 'Low', message: 'plain', originPassLens: null, originPassLabel: 'Baseline', passIndex: 0 },
        { key: 'd', filePath: 'd.ts', lineNumber: 4, severity: 'Low', message: 'inv', originPassLens: 'inventory', originPassLabel: 'Inventory', passIndex: 2 },
    ]

    it('classifies security / prorv as Security and null / inventory as Quality', () => {
        expect(lensBucket('security')).toBe('security')
        expect(lensBucket('prorv')).toBe('security')
        expect(lensBucket(null)).toBe('quality')
        expect(lensBucket('inventory')).toBe('quality')
        expect(countFindingsByLens(findings)).toEqual({ all: 4, security: 2, quality: 2, other: 0 })
    })

    it('filters findings by lens without dropping All', () => {
        expect(filterFindingsByLens(findings, 'security').map((f) => f.key)).toEqual(['a', 'b'])
        expect(filterFindingsByLens(findings, 'quality').map((f) => f.key)).toEqual(['c', 'd'])
        expect(filterFindingsByLens(findings, 'all')).toHaveLength(4)
    })
})

describe('flattenPublishedFindings', () => {
    it('flattens final comments in pass order and skips empty messages', () => {
        const findings = flattenPublishedFindings([
            pass({
                label: 'Baseline',
                finalComments: [
                    { filePath: 'a.ts', lineNumber: 10, severity: 'error', message: '  Real finding  ', originPassLens: null },
                    { filePath: 'a.ts', lineNumber: 11, severity: 'info', message: '   ', originPassLens: null },
                ],
            }),
        ])
        expect(findings).toHaveLength(1)
        expect(findings[0]?.message).toBe('Real finding')
        expect(findings[0]?.originPassLabel).toBe('Baseline')
    })
})

describe('formatDurationMs', () => {
    it('formats seconds and minutes', () => {
        expect(formatDurationMs('2026-10-25T10:00:00Z', '2026-10-25T10:00:26Z')).toBe('26s')
        expect(formatDurationMs('2026-10-25T10:00:00Z', '2026-10-25T10:01:12Z')).toBe('1m 12s')
        expect(formatDurationMs(null, '2026-10-25T10:01:12Z')).toBe('—')
    })
})
