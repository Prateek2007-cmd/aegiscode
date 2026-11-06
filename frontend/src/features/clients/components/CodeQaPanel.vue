<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Ask your codebase panel. Sends a question to the code-qa endpoint and shows the
  grounded answer with its file citations. Answers come only from code ProCursor
  retrieved for this client: when nothing matched, ProCursor is unavailable, or the
  model answered without citing the retrieved snippets, the API answers honestly
  that it does not know, and this panel shows that answer plus the retrieved
  snippets instead of inventing anything. History is this session only.
-->
<template>
  <section class="code-qa-panel" aria-label="Ask your codebase">
    <h3 class="code-qa-title">Ask your codebase</h3>
    <p class="code-qa-subtitle">
      Ask a question about this client's code. Answers come only from the indexed code, with a
      citation for every claim — never from guessing.
    </p>

    <form @submit.prevent="handleAsk" novalidate>
      <div class="code-qa-field">
        <label for="code-qa-question">Question</label>
        <textarea
          id="code-qa-question"
          v-model="question"
          rows="2"
          placeholder="Where is authentication handled?"
          autocomplete="off"
          :disabled="asking"
        ></textarea>
        <p v-if="questionError" class="error" role="alert">{{ questionError }}</p>
      </div>

      <button class="btn-primary" type="submit" :disabled="asking || !question.trim()">
        {{ asking ? 'Asking…' : 'Ask' }}
      </button>
    </form>

    <p v-if="transportError" class="code-qa-error" role="alert">{{ transportError }}</p>
    <p v-if="unavailable" class="code-qa-note" role="status">
      Ask your codebase needs the API update — not available yet.
    </p>

    <ol v-if="history.length > 0" class="code-qa-history">
      <li v-for="(entry, index) in history" :key="entry.id" class="code-qa-entry">
        <p class="code-qa-entry-question">{{ entry.question }}</p>
        <p
          class="code-qa-entry-answer"
          :class="{ 'code-qa-entry-unanswered': !entry.response.answered }"
        >
          {{ entry.response.answer }}
        </p>
        <p v-if="entry.response.modelId" class="code-qa-entry-model">
          Answered by {{ entry.response.modelId }}
          <span v-if="entry.response.retrievalStatus">· retrieval {{ entry.response.retrievalStatus }}</span>
        </p>
        <p v-else-if="entry.response.retrievalStatus" class="code-qa-entry-model">
          Retrieval {{ entry.response.retrievalStatus }}
        </p>
        <ol v-if="entry.response.citations.length > 0" class="code-qa-citations">
          <li
            v-for="(citation, citationIndex) in entry.response.citations"
            :key="`${entry.id}-${citationIndex}`"
            class="code-qa-citation"
          >
            <p class="code-qa-citation-path">
              [{{ citationIndex + 1 }}] <code>{{ citation.contentPath }}</code>
              <span class="code-qa-citation-meta">
                {{ citation.branch }} · {{ citation.commitSha.slice(0, 7) }} · {{ citation.freshnessStatus }}
              </span>
            </p>
            <p v-if="citation.title" class="code-qa-citation-title">{{ citation.title }}</p>
            <pre class="code-qa-citation-excerpt">{{ citation.excerpt }}</pre>
          </li>
        </ol>
        <p v-else class="code-qa-no-citations">No code snippets were retrieved for this question.</p>
      </li>
    </ol>
    <p v-else-if="!asking" class="code-qa-empty">No questions yet this session.</p>
  </section>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import {
  askCodeQuestion,
  isCodeQaUnavailable,
} from '@/services/codeQaService'
import type { AskCodeQuestionResponse } from '@/services/codeQaService'

const props = defineProps<{
  clientId: string
}>()

interface CodeQaHistoryEntry {
  id: number
  question: string
  response: AskCodeQuestionResponse
}

const question = ref('')
const questionError = ref<string | null>(null)
const asking = ref(false)
const transportError = ref<string | null>(null)
const unavailable = ref(false)
const history = ref<CodeQaHistoryEntry[]>([])
let nextEntryId = 1

async function handleAsk(): Promise<void> {
  if (!question.value.trim()) {
    questionError.value = 'Ask a question first.'
    return
  }
  questionError.value = null

  const asked = question.value.trim()
  asking.value = true
  transportError.value = null
  unavailable.value = false
  try {
    const response = await askCodeQuestion(props.clientId, asked)
    history.value.unshift({ id: nextEntryId++, question: asked, response })
    question.value = ''
  } catch (error) {
    if (isCodeQaUnavailable(error)) {
      unavailable.value = true
    } else {
      transportError.value =
        error instanceof Error ? error.message : 'The question could not be asked.'
    }
  } finally {
    asking.value = false
  }
}
</script>

<style scoped>
.code-qa-panel {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.25rem 1.4rem;
  color: #e2e8f0;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.code-qa-title {
  margin: 0;
  font-size: 1.05rem;
}

.code-qa-subtitle {
  margin: 0;
  color: #94a3b8;
  font-size: 0.9rem;
}

.code-qa-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.code-qa-field label {
  font-size: 0.85rem;
  font-weight: 600;
}

.code-qa-field textarea {
  background: rgba(148, 163, 184, 0.08);
  border: 1px solid rgba(56, 189, 248, 0.25);
  border-radius: 8px;
  color: #e2e8f0;
  padding: 0.55rem 0.75rem;
  font-size: 0.95rem;
  font-family: inherit;
  resize: vertical;
}

.code-qa-field textarea:disabled {
  opacity: 0.6;
}

.code-qa-field textarea::placeholder {
  color: #64748b;
}

.error {
  color: var(--color-danger);
  margin: 0;
  font-size: 0.85rem;
}

.code-qa-error {
  margin: 0;
  color: #f87171;
  font-size: 0.9rem;
}

.code-qa-note {
  margin: 0;
  color: #fbbf24;
  font-size: 0.9rem;
}

.code-qa-empty {
  margin: 0;
  color: #94a3b8;
  font-size: 0.9rem;
}

.code-qa-history {
  margin: 0.5rem 0 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.code-qa-entry {
  border-top: 1px solid rgba(56, 189, 248, 0.18);
  padding-top: 0.9rem;
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
}

.code-qa-entry-question {
  margin: 0;
  font-weight: 700;
  font-size: 0.95rem;
}

.code-qa-entry-answer {
  margin: 0;
  color: #e2e8f0;
  font-size: 0.92rem;
  white-space: pre-wrap;
}

.code-qa-entry-unanswered {
  color: #fbbf24;
}

.code-qa-entry-model {
  margin: 0;
  color: #94a3b8;
  font-size: 0.8rem;
}

.code-qa-citations {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.code-qa-citation-path {
  margin: 0;
  font-size: 0.85rem;
}

.code-qa-citation-path code {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  color: #7dd3fc;
  word-break: break-all;
}

.code-qa-citation-meta {
  color: #94a3b8;
}

.code-qa-citation-title {
  margin: 0.2rem 0 0;
  color: #cbd5e1;
  font-size: 0.85rem;
}

.code-qa-citation-excerpt {
  margin: 0.3rem 0 0;
  background: rgba(148, 163, 184, 0.08);
  border-radius: 8px;
  padding: 0.55rem 0.75rem;
  font-size: 0.82rem;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  color: #cbd5e1;
  white-space: pre-wrap;
  word-break: break-word;
}

.code-qa-no-citations {
  margin: 0;
  color: #94a3b8;
  font-size: 0.85rem;
}

</style>
