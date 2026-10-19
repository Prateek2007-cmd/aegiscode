// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { Ref } from 'vue'
import { retryPerformanceEvidence, retryPerformanceModelEvidence } from '@/services/reviewerPerformanceService'

export interface ModelRecoveryTarget { providerScope: string; connectionId: string }

export function recoveryActionKey(aggregateId: string, source?: ModelRecoveryTarget): string {
  return source ? JSON.stringify([aggregateId, 'retry-model-evidence', source.providerScope, source.connectionId]) : aggregateId
}

export type PerformanceRecoveryActions = Record<string, {
  state: 'pending' | 'queued' | 'completed' | 'failed'
  requestedAt?: string
  error?: string
}>

/** Updates the owning view even when its collection dialog has closed. */
export async function requestEvidenceRecovery(actions: Ref<PerformanceRecoveryActions>, aggregateId: string, source?: ModelRecoveryTarget): Promise<void> {
  const key = recoveryActionKey(aggregateId, source)
  if (['pending', 'queued', 'completed'].includes(actions.value[key]?.state ?? '')) return
  actions.value = { ...actions.value, [key]: { state: 'pending' } }
  try {
    if (source) await retryPerformanceModelEvidence(aggregateId, source)
    else await retryPerformanceEvidence(aggregateId)
    actions.value = { ...actions.value, [key]: { state: source ? 'completed' : 'queued', requestedAt: new Date().toISOString() } }
  } catch (error) {
    actions.value = { ...actions.value, [key]: {
      state: 'failed',
      error: error instanceof Error ? error.message : 'The evidence recovery request could not be completed.',
    } }
  }
}

/** Captures completed commands before a live evidence read begins. */
export function snapshotRecoveryFeedback(actions: PerformanceRecoveryActions) {
  return Object.entries(actions).filter(([, action]) => action.state !== 'pending')
}

/** Keeps pending or replaced actions while removing feedback superseded by an accepted fresh read. */
export function reconcileRecoveryFeedback(actions: PerformanceRecoveryActions, captured: ReturnType<typeof snapshotRecoveryFeedback>): void {
  for (const [id, action] of captured) {
    if (actions[id] === action) delete actions[id]
  }
}
