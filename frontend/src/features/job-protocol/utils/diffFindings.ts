// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import type { InlineDiffThread } from '../components/JobProtocolDiffViewer.vue'
import type { ProtocolReviewComment } from '../types'

/**
 * The finding payload pinned to a diff line in the job-level Diff tab.
 * The review publishes findings against the NEW side of the file, so the
 * marker anchors to the finding's line number as a new-side line — the same
 * anchoring the retained browser uses for its inline threads.
 */
export interface DiffFindingPayload {
    /** Severity as published, lower-cased for styling (`error`, `warning`, `info`, `suggestion`). */
    severity: string
    /** The finding text shown when the marker is opened. */
    message: string
    /** NEW-side line number the marker anchors to. */
    lineNumber: number
    /** Repository-relative file path the finding belongs to. */
    filePath: string
}

export type DiffFindingThread = InlineDiffThread<DiffFindingPayload>

/** Normalizes a file path for comparison: backslashes become slashes. */
export function normalizeDiffFindingPath(path: string | null | undefined): string {
    return (path ?? '').replaceAll('\\', '/')
}

function findingLine(comment: ProtocolReviewComment): number {
    return comment.lineNumber ?? comment.line_number ?? 0
}

function findingPath(comment: ProtocolReviewComment): string {
    return normalizeDiffFindingPath(comment.filePath ?? comment.file_path ?? '')
}

function findingId(comment: ProtocolReviewComment, index: number): string {
    const message = (comment.message ?? '').slice(0, 80)
    return `${findingPath(comment)}:${findingLine(comment)}:${index}:${message}`
}

/**
 * Builds the inline-thread markers for one file's diff from the job's published
 * findings. Only findings with a NEW-side line number anchor to a diff line;
 * file-level findings (no line) are left out here and listed as unanchored by
 * the caller. A finding anchors only when its path matches the file exactly.
 */
export function findingsToInlineThreads(
    comments: readonly ProtocolReviewComment[],
    filePath: string,
): DiffFindingThread[] {
    const target = normalizeDiffFindingPath(filePath)
    const threads: DiffFindingThread[] = []

    comments.forEach((comment, index) => {
        const line = findingLine(comment)
        if (line <= 0) return
        if (findingPath(comment) !== target) return

        threads.push({
            id: findingId(comment, index),
            line,
            payload: {
                severity: (comment.severity ?? 'info').toLowerCase(),
                message: comment.message ?? '',
                lineNumber: line,
                filePath: findingPath(comment),
            },
        })
    })

    return threads
}

/**
 * Counts published findings per file path, including file-level findings with
 * no line number. Drives the finding-count badges on the Diff tab's file list.
 */
export function countFindingsByFile(
    comments: readonly ProtocolReviewComment[],
): Map<string, number> {
    const counts = new Map<string, number>()
    for (const comment of comments) {
        const path = findingPath(comment)
        if (!path) continue
        counts.set(path, (counts.get(path) ?? 0) + 1)
    }
    return counts
}

/**
 * Findings for a file that could NOT be pinned to a diff line: either the
 * finding has no line number, or its line never anchored in the rendered diff
 * (the viewer reports which thread ids anchored). They are shown below the
 * diff instead of being silently dropped.
 */
export function unanchoredFindingsForFile(
    comments: readonly ProtocolReviewComment[],
    filePath: string,
    anchoredIds: readonly string[],
): ProtocolReviewComment[] {
    const target = normalizeDiffFindingPath(filePath)
    const anchored = new Set(anchoredIds)
    const unanchored: ProtocolReviewComment[] = []

    comments.forEach((comment, index) => {
        if (findingPath(comment) !== target) return
        const line = findingLine(comment)
        if (line <= 0) {
            unanchored.push(comment)
            return
        }
        if (!anchored.has(findingId(comment, index))) {
            unanchored.push(comment)
        }
    })

    return unanchored
}
