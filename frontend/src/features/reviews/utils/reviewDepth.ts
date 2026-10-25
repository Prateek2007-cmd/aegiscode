// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Pure helpers for the review depth panel: parsing the verification funnel out of protocol events,
 * classifying published findings by the pass lens that produced them, and formatting the numbers the
 * panel shows. Nothing here touches the network or the DOM, so it is unit-tested directly.
 */

import type { components } from '@/types'

export type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']
export type ProtocolEventDto = components['schemas']['ProtocolEventDto']
export type ProtocolReviewCommentDto = components['schemas']['ProtocolReviewCommentDto']

/** The verification funnel, as recorded by the review's final gate. All counts are optional: a record
 *  with none of them is not a record at all, and the panel says so instead of inventing zeros. */
export interface VerificationFunnel {
    candidateCount?: number
    publishCount?: number
    dropCount?: number
    summaryOnlyCount?: number
}

const FINAL_GATE_SUMMARY_EVENT = 'review_finding_gate_summary'

function isFiniteNumber(value: unknown): value is number {
    return typeof value === 'number' && Number.isFinite(value)
}

function parseJsonLeniently(text: string | null | undefined): Record<string, unknown> | null {
    if (!text) return null
    try {
        const parsed: unknown = JSON.parse(text.startsWith('args=') ? text.slice(5) : text)
        return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
            ? (parsed as Record<string, unknown>)
            : null
    } catch {
        return null
    }
}

function funnelFromRecord(record: Record<string, unknown>): VerificationFunnel | null {
    const funnel: VerificationFunnel = {
        candidateCount: isFiniteNumber(record.candidateCount) ? record.candidateCount : undefined,
        publishCount: isFiniteNumber(record.publishCount) ? record.publishCount : undefined,
        dropCount: isFiniteNumber(record.dropCount) ? record.dropCount : undefined,
        summaryOnlyCount: isFiniteNumber(record.summaryOnlyCount) ? record.summaryOnlyCount : undefined,
    }
    return funnel.candidateCount !== undefined
        || funnel.publishCount !== undefined
        || funnel.dropCount !== undefined
        || funnel.summaryOnlyCount !== undefined
        ? funnel
        : null
}

/**
 * Scans every pass's events for the final-gate summary record. The record may sit in the event's
 * output summary or its input sample; either is accepted, output first. Returns null when no event
 * carries a usable record — the caller shows "verification data not recorded for this job".
 */
export function parseVerificationFunnel(passes: ReviewJobProtocolDto[]): VerificationFunnel | null {
    for (const pass of passes) {
        for (const event of pass.events ?? []) {
            if (event?.name !== FINAL_GATE_SUMMARY_EVENT) continue
            const fromOutput = parseJsonLeniently(event.outputSummary)
            const fromInput = fromOutput ? null : parseJsonLeniently(event.inputTextSample)
            const funnel = (fromOutput && funnelFromRecord(fromOutput))
                ?? (fromInput && funnelFromRecord(fromInput))
            if (funnel) return funnel
        }
    }
    return null
}

/** One published finding, flattened out of its pass with the lens that produced it. */
export interface PublishedFinding {
    key: string
    filePath: string | null
    lineNumber: number | null
    severity: string | null
    message: string
    originPassLens: string | null
    originPassLabel: string | null
    passIndex: number
}

/** Flattens every pass's final comments into published findings, in pass order. */
export function flattenPublishedFindings(passes: ReviewJobProtocolDto[]): PublishedFinding[] {
    const findings: PublishedFinding[] = []
    passes.forEach((pass, passIndex) => {
        for (const comment of pass.finalComments ?? []) {
            const message = comment?.message?.trim()
            if (!message) continue
            findings.push({
                key: `${passIndex}:${comment.filePath ?? ''}:${comment.lineNumber ?? ''}:${message}`,
                filePath: comment.filePath ?? null,
                lineNumber: comment.lineNumber ?? null,
                severity: comment.severity ?? null,
                message,
                originPassLens: comment.originPassLens ?? null,
                originPassLabel: pass.label ?? null,
                passIndex,
            })
        }
    })
    return findings
}

export type LensFilter = 'all' | 'security' | 'quality'

/**
 * The lens buckets the toggle filters by. Lenses are assigned by the runtime (triage / security floor),
 * never by the submit request — the toggle only filters what this review already published.
 * Security = the security-specialist and knowledge-guided passes; Quality = ordinary passes and the
 * defect inventory. A lens the backend adds later lands in `other` and still shows under All.
 */
export function lensBucket(lens: string | null | undefined): 'security' | 'quality' | 'other' {
    if (lens === 'security' || lens === 'prorv') return 'security'
    if (lens === null || lens === undefined || lens === 'inventory') return 'quality'
    return 'other'
}

export interface LensCounts {
    all: number
    security: number
    quality: number
    other: number
}

export function countFindingsByLens(findings: PublishedFinding[]): LensCounts {
    const counts: LensCounts = { all: findings.length, security: 0, quality: 0, other: 0 }
    for (const finding of findings) {
        counts[lensBucket(finding.originPassLens)] += 1
    }
    return counts
}

export function filterFindingsByLens(findings: PublishedFinding[], filter: LensFilter): PublishedFinding[] {
    if (filter === 'all') return findings
    return findings.filter((finding) => lensBucket(finding.originPassLens) === filter)
}

export function lensLabel(lens: string | null | undefined): string {
    switch (lens) {
        case 'security': return 'Security lens'
        case 'prorv': return 'Knowledge-guided'
        case 'inventory': return 'Defect inventory'
        case null:
        case undefined: return 'Ordinary pass'
        default: return lens
    }
}

export function formatDurationMs(startedAt: string | null | undefined, completedAt: string | null | undefined): string {
    if (!startedAt || !completedAt) return '—'
    const ms = new Date(completedAt).getTime() - new Date(startedAt).getTime()
    if (!Number.isFinite(ms) || ms < 0) return '—'
    const seconds = Math.round(ms / 1000)
    if (seconds < 60) return `${seconds}s`
    const minutes = Math.floor(seconds / 60)
    if (minutes < 60) return `${minutes}m ${seconds % 60}s`
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

export function formatTokenCount(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString('en-US')
}

export function formatUsdAmount(value: number | null | undefined, approximate?: boolean | null): string {
    if (value == null) return '—'
    const formatted = new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: 'USD',
        minimumFractionDigits: 2,
        maximumFractionDigits: 4,
    }).format(value)
    return approximate ? `≈ ${formatted}` : formatted
}
