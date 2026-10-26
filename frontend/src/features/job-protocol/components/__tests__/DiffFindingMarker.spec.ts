// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import DiffFindingMarker from '../DiffFindingMarker.vue'
import type { DiffFindingPayload } from '../../utils/diffFindings'

function finding(overrides: Partial<DiffFindingPayload> = {}): DiffFindingPayload {
    return {
        severity: 'warning',
        message: 'Consider a null check before dereferencing.',
        lineNumber: 42,
        filePath: 'src/Foo.cs',
        ...overrides,
    }
}

describe('DiffFindingMarker', () => {
    it('shows a collapsed marker and reveals the finding on click', async () => {
        const wrapper = mount(DiffFindingMarker, { props: { finding: finding() } })

        expect(wrapper.get('[data-testid="diff-finding-marker"]').text()).toContain('warning')
        expect(wrapper.get('[data-testid="diff-finding-marker"]').text()).toContain('L42')
        expect(wrapper.find('[data-testid="diff-finding-message"]').exists()).toBe(false)

        await wrapper.get('.diff-finding-marker-toggle').trigger('click')

        expect(wrapper.get('[data-testid="diff-finding-message"]').text()).toBe(
            'Consider a null check before dereferencing.',
        )
    })

    it('starts expanded when asked', () => {
        const wrapper = mount(DiffFindingMarker, {
            props: { finding: finding({ severity: 'error' }), startExpanded: true },
        })

        expect(wrapper.get('[data-testid="diff-finding-message"]').text()).toContain('null check')
        expect(wrapper.get('[data-testid="diff-finding-marker"]').classes()).toContain('diff-finding-marker--error')
    })
})
