// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

<template>
    <div class="diff-finding-marker" :class="`diff-finding-marker--${finding.severity}`" data-testid="diff-finding-marker">
        <button
            type="button"
            class="diff-finding-marker-toggle"
            :aria-expanded="expanded"
            @click="expanded = !expanded"
        >
            <span class="diff-finding-severity">{{ finding.severity }}</span>
            <span class="diff-finding-line">L{{ finding.lineNumber }}</span>
            <span class="diff-finding-hint">{{ expanded ? 'Hide finding' : 'Show finding' }}</span>
            <span class="diff-finding-chevron" aria-hidden="true">{{ expanded ? '▾' : '▸' }}</span>
        </button>
        <p v-if="expanded" class="diff-finding-message" data-testid="diff-finding-message">{{ finding.message }}</p>
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import type { DiffFindingPayload } from '../utils/diffFindings'

const props = withDefaults(
    defineProps<{
        finding: DiffFindingPayload
        /** Open the message immediately (used for findings listed below the diff). */
        startExpanded?: boolean
    }>(),
    { startExpanded: false },
)

const expanded = ref(props.startExpanded)
</script>

<style scoped>
.diff-finding-marker {
    border: 1px solid var(--color-border);
    border-left: 3px solid var(--color-accent);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    overflow: hidden;
}

.diff-finding-marker--error {
    border-left-color: var(--color-danger);
}

.diff-finding-marker--warning {
    border-left-color: var(--color-warning, #f59e0b);
}

.diff-finding-marker--suggestion {
    border-left-color: var(--color-suggestion, #a855f7);
}

.diff-finding-marker--info {
    border-left-color: var(--color-accent);
}

.diff-finding-marker-toggle {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    width: 100%;
    padding: 0.5rem 0.75rem;
    background: transparent;
    border: none;
    cursor: pointer;
    font: inherit;
    color: var(--color-text);
    text-align: left;
}

.diff-finding-marker-toggle:hover {
    background: rgba(255, 255, 255, 0.03);
}

.diff-finding-severity {
    font-size: 0.7rem;
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    padding: 0.1rem 0.5rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
}

.diff-finding-marker--error .diff-finding-severity {
    background: rgba(239, 68, 68, 0.12);
    color: var(--color-danger);
}

.diff-finding-marker--warning .diff-finding-severity {
    background: rgba(245, 158, 11, 0.12);
    color: var(--color-warning, #f59e0b);
}

.diff-finding-marker--suggestion .diff-finding-severity {
    background: rgba(168, 85, 247, 0.14);
    color: var(--color-suggestion, #a855f7);
}

.diff-finding-line {
    font-family: var(--font-mono, monospace);
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--color-text);
}

.diff-finding-hint {
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.diff-finding-chevron {
    margin-left: auto;
    color: var(--color-text-muted);
}

.diff-finding-message {
    margin: 0;
    padding: 0.1rem 0.75rem 0.65rem;
    font-size: 0.88rem;
    line-height: 1.5;
    color: var(--color-text);
    overflow-wrap: anywhere;
}
</style>
