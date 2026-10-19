// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { retryPerformanceEvidence, retryPerformanceModelEvidence } from '../reviewerPerformanceService'

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock('../api', async (original) => {
  const actual = await original<typeof import('../api')>()
  return { ...actual, createAdminClient: () => ({ POST: post }) }
})
describe('reviewer performance recovery requests', () => {
  beforeEach(() => post.mockReset())
  it('retries retained classification using the captured source and accepts an empty 200', async () => {
    post.mockResolvedValue({ response: new Response(null, { status: 200 }) })
    const source = { providerScope: 'GitHub:https://github.com', connectionId: 'connection-a' }
    await retryPerformanceModelEvidence('aggregate', source)
    expect(post).toHaveBeenCalledWith('/reviewer-performance/evidence/{aggregateId}/retry-model', { params: { path: { aggregateId: 'aggregate' } }, body: source })
  })
  it('sends the typed bodyless retry and accepts an empty 202 response', async () => {
    post.mockResolvedValue({ response: new Response(null, { status: 202 }) })
    await retryPerformanceEvidence('aggregate')
    expect(post).toHaveBeenCalledWith('/reviewer-performance/evidence/{aggregateId}/retry', { params: { path: { aggregateId: 'aggregate' } } })
  })
  it('preserves actionable conflict details', async () => {
    post.mockResolvedValue({ response: new Response(null, { status: 409 }), error: { title: 'Collection disabled', detail: 'Enable collection for this client, then retry.' } })
    await expect(retryPerformanceEvidence('aggregate')).rejects.toThrow('Enable collection for this client, then retry.')
  })
  it('handles empty missing-or-foreign responses without an invented success', async () => {
    post.mockResolvedValue({ response: new Response(null, { status: 404 }) })
    await expect(retryPerformanceEvidence('aggregate')).rejects.toThrow('unavailable or outside your administered clients')
  })
})
