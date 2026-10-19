// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { ref } from 'vue'
import ReviewerPerformanceExclusionsDialog from '../components/ReviewerPerformanceExclusionsDialog.vue'
import type { PerformanceExclusion } from '@/services/reviewerPerformanceService'

const retry = vi.fn()
const retryModel = vi.fn()
const licensed = ref(true)
vi.mock('@/services/reviewerPerformanceService', () => ({ retryPerformanceEvidence: (...args: unknown[]) => retry(...args), retryPerformanceModelEvidence: (...args: unknown[]) => retryModel(...args) }))
vi.mock('@/composables/useSession', () => ({ useSession: () => ({ isCapabilityAvailable: () => licensed.value, isAdmin: ref(true), tenantRoles: ref({}) }) }))
const rows: PerformanceExclusion[] = Array.from({ length: 13 }, (_, id) => ({
  id, aggregateId: `aggregate-${id}`, clientId: 'client-a', clientName: 'Payments', repositoryId: 'payments-api', repositoryName: 'Payments API', pullRequestId: id + 1,
  reasons: ['miss-collection-coverage-unavailable'], recoveryDisposition: 'repair-required', collectionEnabled: true,
  providerScope: 'AzureDevOps:https://dev.azure.com/example',
  lastAttemptAt: '2026-10-16T12:00:00Z', notBefore: '2026-10-17T12:00:00Z', lastRefreshReason: 'SourceCoordinatesUnavailable', sourceUrl: id === 0 ? 'https://dev.azure.com/example/project/_git/payments-api/pullrequest/1' : undefined,
}))

describe('captured exclusions and authorized recovery', () => {
  let wrapper: VueWrapper | undefined
  beforeEach(() => { licensed.value = true; retry.mockReset(); retry.mockResolvedValue(undefined); retryModel.mockReset(); retryModel.mockResolvedValue(undefined) })
  afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })
  function render(frozen = false, entries = rows) {
    wrapper = mount(ReviewerPerformanceExclusionsDialog, { attachTo: document.body, props: { entries, frozen, label: 'View A · All selected evidence', windowFrom: '2026-09-18', windowTo: '2026-10-17' } })
    return wrapper
  }

  it('uses only captured source-scoped classification recovery and leaves the measurement unchanged', async () => {
    const entry = { ...rows[0], recoveryAction: 'retry-model-evidence', sourceConnectionId: 'connection-a', recoveryDisposition: 'ready',
      reasons: ['model-concern-match-unavailable'], lastModelRetryAt: '2026-10-16T12:00:00Z', modelRetryNotBefore: '2026-10-16T12:05:00Z' }
    const original = structuredClone(entry)
    const view = render(false, [entry])
    expect(view.get('[data-test="retry-evidence"]').text()).toBe('Retry retained classification')
    await view.get('[data-test="retry-evidence"]').trigger('click')
    await flushPromises()
    expect(retryModel).toHaveBeenCalledWith(entry.aggregateId, { providerScope: entry.providerScope, connectionId: entry.sourceConnectionId })
    expect(retry).not.toHaveBeenCalled()
    expect(view.text()).toContain('Classification attempt completed')
    expect(view.text()).not.toContain('Queued for a background sweep')
    expect(entry).toEqual(original)
  })

  it('keeps commands independent for different captured sources on the same aggregate', async () => {
    let complete!: () => void
    retryModel.mockReturnValueOnce(new Promise<void>((resolve) => { complete = resolve }))
    const first = { ...rows[0], recoveryAction: 'retry-model-evidence', recoveryDisposition: 'ready', sourceConnectionId: 'connection-a' }
    const second = { ...first, id: 1, providerScope: 'GitHub:https://github.com', sourceConnectionId: 'connection-b' }
    const view = render(false, [first, second])
    const buttons = view.findAll('[data-test="retry-evidence"]')
    await buttons[0]!.trigger('click')
    expect(buttons[0]!.attributes('disabled')).toBeDefined()
    expect(buttons[1]!.attributes('disabled')).toBeUndefined()
    await buttons[1]!.trigger('click')
    await flushPromises()
    expect(retryModel).toHaveBeenNthCalledWith(2, second.aggregateId, { providerScope: second.providerScope, connectionId: second.sourceConnectionId })
    complete()
    await flushPromises()
  })

  it('explains unreconstructable history without offering a collection command', () => {
    const view = render(false, [{ ...rows[0], recoveryAction: 'none', recoveryDisposition: 'unreconstructable-history', reasons: ['model-proof-unavailable'] }])
    expect(view.find('[data-test="retry-evidence"]').exists()).toBe(false)
    expect(view.text()).toContain('Historical model evidence cannot be reconstructed')
  })

  it.each([
    ['model-concern-input-limit-exceeded', 'The retained concern or model candidate set exceeds the bounded classification input.'],
    ['model-concern-equivalence-limit-exceeded', 'The source exceeds the bounded concern-equivalence evaluation.'],
  ])('explains permanent %s limits without a retry action', (reason, explanation) => {
    const view = render(false, [{ ...rows[0], recoveryAction: 'none', recoveryDisposition: 'unreconstructable-history', reasons: [reason!] }])
    expect(view.text()).toContain(explanation)
    expect(view.find('[data-test="retry-evidence"]').exists()).toBe(false)
  })

  it('displays captured model cooldown without offering an early retry', () => {
    const view = render(false, [{ ...rows[0], recoveryAction: 'retry-model-evidence', sourceConnectionId: 'connection-a', recoveryDisposition: 'cooldown',
      modelRetryNotBefore: '2026-10-18T18:05:00Z' }])
    expect(view.text()).toContain('Classification retry eligible after')
    expect(view.get('time[datetime="2026-10-18T18:05:00Z"]').attributes('datetime')).toBe('2026-10-18T18:05:00Z')
    expect(view.find('[data-test="retry-evidence"]').exists()).toBe(false)
  })

  it('pages locally and displays captured source, reason, attempt and recovery data including ID zero', async () => {
    const view = render()
    expect(view.findAll('tbody tr')).toHaveLength(10)
    expect(view.text()).toContain('Payments API')
    expect(view.text()).toContain('Complete human-thread collection is missing')
    expect(view.text()).toContain('Source coordinates unavailable')
    expect(view.text()).toContain('Repair required')
    expect(view.text()).toContain('AzureDevOps:https://dev.azure.com/example')
    expect(view.text()).not.toContain('Automatic retry eligible after')
    expect(view.get('a').attributes('href')).toBe(rows[0]!.sourceUrl)
    await view.get('[aria-label="Next exclusions page"]').trigger('click')
    expect(view.findAll('tbody tr')).toHaveLength(3)
    expect(retry).not.toHaveBeenCalled()
  })

  it('queues a live retry and keeps captured metadata unchanged', async () => {
    const original = structuredClone(rows[0])
    const view = render(false, [rows[0]!])
    await view.get('[data-test="retry-evidence"]').trigger('click')
    await flushPromises()
    expect(retry).toHaveBeenCalledWith('aggregate-0')
    expect(view.get('[role="status"]').text()).toContain('Queued for a background sweep')
    expect(rows[0]).toEqual(original)
    expect(view.text()).toContain('Collection queued')
    expect(view.text()).toContain('Request acknowledged')
    const button = view.find('[data-test="retry-evidence"]')
    expect(!button.exists() || button.attributes('disabled') != null).toBe(true)
  })

  it('renders durable queued and lease timing and permits refreshing without a local command', async () => {
    const view = render(false, [{ ...rows[0], recoveryDisposition: 'queued', lastRefreshReason: null }])
    expect(view.text()).toContain('Collection queued')
    expect(view.text()).toContain('Queued / eligible for a background sweep from')
    await view.get('footer > button').trigger('click')
    expect(view.emitted('refresh')).toHaveLength(1)
    await view.setProps({ entries: [{ ...rows[0], recoveryDisposition: 'in-progress', lastRefreshReason: null }] })
    expect(view.text()).toContain('Observation lease expires')
    expect(view.text()).toContain('Last attempted')
  })

  it('shows actionable conflicts and allows retrying after the condition changes', async () => {
    retry.mockRejectedValueOnce(new Error('Wait for the current evidence observation to finish, then retry.'))
    const view = render(false, [rows[0]!])
    await view.get('[data-test="retry-evidence"]').trigger('click')
    await flushPromises()
    expect(view.get('[role="alert"]').text()).toContain('Wait for the current evidence observation')
    expect(view.get('[data-test="retry-evidence"]').attributes('disabled')).toBeUndefined()
  })

  it.each(['frozen', 'unlicensed', 'disabled', 'in-progress', 'queued'])('does not offer a command for %s evidence', (state) => {
    if (state === 'unlicensed') licensed.value = false
    const entry = { ...rows[0], ...(state === 'disabled' ? { collectionEnabled: false, recoveryDisposition: 'collection-disabled' } : {}), ...(['in-progress', 'queued'].includes(state) ? { recoveryDisposition: state } : {}) }
    const view = render(state === 'frozen', [entry])
    const button = view.find('[data-test="retry-evidence"]')
    expect(!button.exists() || button.attributes('disabled') != null).toBe(true)
    expect(retry).not.toHaveBeenCalled()
  })

  it('omits unsafe or unavailable links and makes absent attempts explicit', () => {
    const view = render(true, [{ ...rows[0], sourceUrl: 'javascript:alert(1)', lastAttemptAt: undefined, lastRefreshReason: undefined, notBefore: undefined }])
    expect(view.find('a').exists()).toBe(false)
    expect(view.text()).toContain('No attempt recorded')
    expect(view.text()).toContain('Source link unavailable')
    expect(view.text()).toContain('Captured details')
  })

  it.each([
    ['HistoricalCapabilityUnsupported', 'Historical collection is unsupported by this provider'],
    ['ObserverUnavailable', 'Historical collection is unavailable for this source'],
  ])('explains %s without presenting paused automatic retries as scheduled', (reason, message) => {
    const view = render(false, [{ ...rows[0], lastRefreshReason: reason }])
    expect(view.text()).toContain(message)
    expect(view.text()).toContain('automatic retries paused')
    expect(view.text()).not.toContain('Automatic retry eligible after')
  })
})
