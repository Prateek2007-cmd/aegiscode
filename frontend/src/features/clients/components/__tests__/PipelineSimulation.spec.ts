// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import PipelineSimulation from '../PipelineSimulation.vue'

describe('PipelineSimulation', () => {
  it('starts on step 1 and labels the walkthrough simulated', () => {
    const wrapper = mount(PipelineSimulation)
    expect(wrapper.text()).toContain('Step 1 of 6')
    expect(wrapper.text()).toContain('Simulated')
    expect(wrapper.text()).toContain('Add rate limiting to the login endpoint')
    // The only real numbers are the Proven live caption (ACM PR #1).
    expect(wrapper.text()).toContain('Proven live: ACM PR #1')
    expect(wrapper.text()).toContain('22,830 tokens in / 954 out')
  })

  it('advances to the findings step and back', async () => {
    const wrapper = mount(PipelineSimulation)
    const next = wrapper.findAll('button').find((b) => b.text().includes('Next'))
    expect(next).toBeDefined()
    await next?.trigger('click')
    await next?.trigger('click')
    expect(wrapper.text()).toContain('Step 3 of 6')
    expect(wrapper.text()).toContain('Hardcoded secret in the login handler')
    const back = wrapper.findAll('button').find((b) => b.text().includes('Back'))
    await back?.trigger('click')
    expect(wrapper.text()).toContain('Step 2 of 6')
  })

  it('jumps to a step from the dots and plays through to the end', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(PipelineSimulation)
      const dots = wrapper.findAll('.pipeline-dot')
      expect(dots).toHaveLength(6)
      await dots[5].trigger('click')
      expect(wrapper.text()).toContain('Step 6 of 6')
      expect(wrapper.text()).toContain('Merged')
      const play = wrapper.findAll('button').find((b) => b.text().includes('Play'))
      await play?.trigger('click')
      // Playing from the last step restarts at step 1.
      expect(wrapper.text()).toContain('Step 1 of 6')
      await vi.advanceTimersByTimeAsync(3800)
      expect(wrapper.text()).toContain('Step 2 of 6')
    } finally {
      vi.useRealTimers()
    }
  })

  it('applies the simulated suggestion locally', async () => {
    const wrapper = mount(PipelineSimulation)
    const dots = wrapper.findAll('.pipeline-dot')
    await dots[2].trigger('click')
    const apply = wrapper.findAll('button').find((b) => b.text().includes('Apply suggestion'))
    expect(apply).toBeDefined()
    await apply?.trigger('click')
    expect(wrapper.text()).toContain('Suggestion applied (simulated)')
  })
})
