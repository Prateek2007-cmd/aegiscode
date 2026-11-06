// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import CodeQaPanel from '../CodeQaPanel.vue'
import type { AskCodeQuestionResponse } from '@/services/codeQaService'

const { askCodeQuestionMock } = vi.hoisted(() => ({
  askCodeQuestionMock: vi.fn(),
}))

vi.mock('@/services/codeQaService', () => ({
  askCodeQuestion: askCodeQuestionMock,
  isCodeQaUnavailable: (error: unknown) =>
    (error as Error | null)?.name === 'CodeQaUnavailableError',
  CodeQaUnavailableError: class CodeQaUnavailableError extends Error {
    constructor() {
      super('Ask your codebase needs the API update — not available yet.')
      this.name = 'CodeQaUnavailableError'
    }
  },
}))

function answeredResponse(overrides: Partial<AskCodeQuestionResponse> = {}): AskCodeQuestionResponse {
  return {
    answered: true,
    answer: 'The handler lives in [1].',
    citations: [
      {
        contentPath: 'src/Auth/Handler.cs',
        title: 'Auth handler',
        excerpt: 'public sealed class AuthHandler { }',
        score: 0.9,
        sourceKind: 'Repository',
        branch: 'main',
        commitSha: 'abc1234',
        freshnessStatus: 'fresh',
      },
    ],
    retrievalStatus: 'complete',
    retrievalNote: null,
    modelId: 'test-model',
    ...overrides,
  }
}

async function ask(wrapper: ReturnType<typeof mount>, question: string): Promise<void> {
  await wrapper.get('textarea').setValue(question)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('CodeQaPanel', () => {
  it('shows the grounded answer with its citations', async () => {
    askCodeQuestionMock.mockResolvedValue(answeredResponse())
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await ask(wrapper, 'Where is auth handled?')

    expect(askCodeQuestionMock).toHaveBeenCalledWith('client-1', 'Where is auth handled?')
    expect(wrapper.get('.code-qa-entry-answer').text()).toContain('The handler lives in [1].')
    expect(wrapper.get('.code-qa-citation-path').text()).toContain('src/Auth/Handler.cs')
    expect(wrapper.get('.code-qa-entry-model').text()).toContain('test-model')
    expect((wrapper.get('textarea').element as HTMLTextAreaElement).value).toBe('')
  })

  it('shows the honest unanswered response instead of inventing one', async () => {
    askCodeQuestionMock.mockResolvedValue(
      answeredResponse({
        answered: false,
        answer: "I don't know from this codebase — no indexed code matched your question.",
        citations: [],
        retrievalStatus: 'noResult',
        modelId: null,
      }),
    )
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await ask(wrapper, 'Where is the flux capacitor?')

    const answer = wrapper.get('.code-qa-entry-answer')
    expect(answer.text()).toContain("I don't know from this codebase")
    expect(answer.classes()).toContain('code-qa-entry-unanswered')
    expect(wrapper.find('.code-qa-citation-path').exists()).toBe(false)
  })

  it('asks the question again after the first answer', async () => {
    askCodeQuestionMock.mockResolvedValue(answeredResponse())
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await ask(wrapper, 'Where is auth handled?')
    await ask(wrapper, 'Where is auth called from?')

    expect(wrapper.findAll('.code-qa-entry')).toHaveLength(2)
    expect(wrapper.findAll('.code-qa-entry')[0].text()).toContain('Where is auth called from?')
  })

  it('shows a validation error for an empty question', async () => {
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('.error').text()).toBe('Ask a question first.')
    expect(askCodeQuestionMock).not.toHaveBeenCalled()
  })

  it('shows the API-update note when the backend does not have the endpoints yet', async () => {
    const { CodeQaUnavailableError } = await import('@/services/codeQaService')
    askCodeQuestionMock.mockRejectedValue(new CodeQaUnavailableError())
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await ask(wrapper, 'Where is auth handled?')

    expect(wrapper.get('.code-qa-note').text()).toContain('needs the API update')
  })

  it('shows the transport error when the question fails', async () => {
    askCodeQuestionMock.mockRejectedValue(new Error('The API is down.'))
    const wrapper = mount(CodeQaPanel, { props: { clientId: 'client-1' } })

    await ask(wrapper, 'Where is auth handled?')

    expect(wrapper.get('.code-qa-error').text()).toContain('The API is down.')
  })
})
