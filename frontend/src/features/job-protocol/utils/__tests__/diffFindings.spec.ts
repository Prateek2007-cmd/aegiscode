// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { describe, expect, it } from 'vitest'
import {
    countFindingsByFile,
    findingsToInlineThreads,
    normalizeDiffFindingPath,
    unanchoredFindingsForFile,
    type DiffFindingPayload,
} from '../diffFindings'
import type { ProtocolReviewComment } from '../../types'

function comment(overrides: Partial<ProtocolReviewComment> = {}): ProtocolReviewComment {
    return {
        filePath: 'src/Foo.cs',
        lineNumber: 10,
        severity: 'warning',
        message: 'Consider a null check.',
        ...overrides,
    } as ProtocolReviewComment
}

describe('normalizeDiffFindingPath', () => {
    it('converts backslashes to slashes and tolerates null', () => {
        expect(normalizeDiffFindingPath('src\\Foo.cs')).toBe('src/Foo.cs')
        expect(normalizeDiffFindingPath(null)).toBe('')
        expect(normalizeDiffFindingPath(undefined)).toBe('')
    })
})

describe('findingsToInlineThreads', () => {
    it('anchors findings on their NEW-side line for the matching file only', () => {
        const threads = findingsToInlineThreads(
            [
                comment({ lineNumber: 10 }),
                comment({ filePath: 'src/Bar.cs', lineNumber: 3 }),
                comment({ lineNumber: 42, severity: 'error', message: 'Null dereference.' }),
            ],
            'src/Foo.cs',
        )

        expect(threads).toHaveLength(2)
        expect(threads[0].line).toBe(10)
        expect(threads[0].payload.severity).toBe('warning')
        expect(threads[0].payload.message).toBe('Consider a null check.')
        expect(threads[1].line).toBe(42)
        expect(threads[1].payload.severity).toBe('error')
    })

    it('skips file-level findings with no line number', () => {
        const threads = findingsToInlineThreads(
            [comment({ lineNumber: null }), comment({ lineNumber: 0 }), comment({ lineNumber: 5 })],
            'src/Foo.cs',
        )
        expect(threads).toHaveLength(1)
        expect(threads[0].line).toBe(5)
    })

    it('matches paths with backslashes and reads snake_case aliases', () => {
        const threads = findingsToInlineThreads(
            [comment({ filePath: undefined, file_path: 'src\\Foo.cs', lineNumber: undefined, line_number: 7 })],
            'src/Foo.cs',
        )
        expect(threads).toHaveLength(1)
        expect(threads[0].line).toBe(7)
    })

    it('gives every thread a stable, distinct id', () => {
        const threads = findingsToInlineThreads(
            [comment({ lineNumber: 10 }), comment({ lineNumber: 10 })],
            'src/Foo.cs',
        )
        expect(threads).toHaveLength(2)
        expect(threads[0].id).not.toBe(threads[1].id)
    })
})

describe('countFindingsByFile', () => {
    it('counts findings per file including file-level ones', () => {
        const counts = countFindingsByFile([
            comment({ filePath: 'src/Foo.cs' }),
            comment({ filePath: 'src/Foo.cs', lineNumber: null }),
            comment({ filePath: 'src/Bar.cs' }),
            comment({ filePath: '' }),
        ])
        expect(counts.get('src/Foo.cs')).toBe(2)
        expect(counts.get('src/Bar.cs')).toBe(1)
        expect(counts.has('')).toBe(false)
    })
})

describe('unanchoredFindingsForFile', () => {
    it('returns file-level findings and findings whose line never anchored', () => {
        const comments = [
            comment({ lineNumber: 10, message: 'anchored' }),
            comment({ lineNumber: 99, message: 'line not in diff' }),
            comment({ lineNumber: null, message: 'file-level' }),
            comment({ filePath: 'src/Other.cs', lineNumber: 1, message: 'other file' }),
        ]
        const threads = findingsToInlineThreads(comments, 'src/Foo.cs')
        // Only the first finding's thread anchored in the rendered diff.
        const anchoredIds = [threads[0].id]
        const unanchored = unanchoredFindingsForFile(comments, 'src/Foo.cs', anchoredIds)

        expect(unanchored.map(c => c.message)).toEqual(['line not in diff', 'file-level'])
    })

    it('returns nothing when every finding anchored', () => {
        const comments = [comment({ lineNumber: 10 })]
        const threads = findingsToInlineThreads(comments, 'src/Foo.cs')
        expect(unanchoredFindingsForFile(comments, 'src/Foo.cs', threads.map(t => t.id))).toEqual([])
    })
})

describe('DiffFindingPayload', () => {
    it('carries the fields the marker renders', () => {
        const payload: DiffFindingPayload = {
            severity: 'error',
            message: 'Null dereference.',
            lineNumber: 42,
            filePath: 'src/Foo.cs',
        }
        expect(payload.severity).toBe('error')
        expect(payload.lineNumber).toBe(42)
    })
})
