// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Pure helpers for the Review Rules panel. No API calls, no invented data:
 * every helper describes a value the API returned.
 */
import type { ReviewRuleEntry, ReviewRuleSeverity } from '@/services/reviewRulesService'

export const reviewRuleSeverities: ReviewRuleSeverity[] = ['Error', 'Warning', 'Suggestion', 'Info']

/** Status badge text for a custom rule. */
export function statusLabel(entry: ReviewRuleEntry): string {
    return entry.enabled ? 'Enabled' : 'Disabled'
}

/** "Regex" or "Substring" — how the rule's pattern is matched. */
export function patternKindLabel(entry: ReviewRuleEntry): string {
    return entry.isRegex ? 'Regex' : 'Substring'
}

/** One-line summary of what a rule matches and how severe a hit is. */
export function ruleSummary(entry: ReviewRuleEntry): string {
    return `${patternKindLabel(entry)} · ${entry.severity}`
}

/** Header summary: "3 rules · 1 disabled". Empty lists report themselves honestly. */
export function describeRules(entries: ReviewRuleEntry[]): string {
    if (entries.length === 0) return 'No custom rules yet'
    const disabled = entries.filter((entry) => !entry.enabled).length
    const rules = `${entries.length} ${entries.length === 1 ? 'rule' : 'rules'}`
    if (disabled === 0) return rules
    return `${rules} · ${disabled} disabled`
}

/** Client-side guard before a save: the API still validates and rejects an invalid regex with the real error. */
export function validateRuleInput(input: {
    name: string
    pattern: string
    message: string
}): string | null {
    if (!input.name.trim()) return 'Name is required.'
    if (!input.pattern.trim()) return 'Pattern is required.'
    if (!input.message.trim()) return 'Message is required.'
    return null
}
