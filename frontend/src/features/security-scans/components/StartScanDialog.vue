<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="dialog-overlay" @click.self="emit('close')" role="dialog" aria-modal="true" aria-label="Start security scan">
    <div class="dialog">
      <h3>Start security scan</h3>
      <p class="dialog-sub">
        Clones the repository shallowly (no hooks, no submodules) and runs deterministic
        scanners: dependency vulnerabilities via OSV.dev, secret detection, and SBOM generation.
      </p>

      <label class="field">
        <span>Repository URL <span class="required">*</span></span>
        <input
          v-model="repositoryUrl"
          type="url"
          placeholder="https://github.com/owner/repo"
          :disabled="starting"
          @keyup.enter="start"
        />
        <span class="hint">https GitHub URLs only. Private hosts and non-https schemes are rejected.</span>
      </label>

      <label class="field">
        <span>Branch, tag, or commit <span class="optional">(optional)</span></span>
        <input v-model="refName" type="text" placeholder="main" :disabled="starting" />
      </label>

      <fieldset class="field">
        <legend>Scanners</legend>
        <label v-for="s in SECURITY_SCANNERS" :key="s" class="check">
          <input v-model="scanners" type="checkbox" :value="s" :disabled="starting" />
          {{ s }}
        </label>
      </fieldset>

      <div v-if="error" class="banner banner-error" role="alert">{{ error }}</div>

      <div class="dialog-actions">
        <button class="btn-ghost" :disabled="starting" @click="emit('close')">Cancel</button>
        <button class="btn-primary" :disabled="!canStart || starting" @click="start">
          {{ starting ? 'Starting…' : 'Start scan' }}
        </button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { startSecurityScan } from '../securityScansService'
import { SECURITY_SCANNERS } from '../types'

const props = defineProps<{ clientId: string }>()
const emit = defineEmits<{ close: []; started: [scanId: string] }>()

const repositoryUrl = ref('')
const refName = ref('')
const scanners = ref<string[]>([...SECURITY_SCANNERS])
const starting = ref(false)
const error = ref<string | null>(null)

const canStart = computed(() => repositoryUrl.value.trim().length > 0 && scanners.value.length > 0)

async function start(): Promise<void> {
  if (!canStart.value || starting.value) return
  starting.value = true
  error.value = null
  try {
    const res = await startSecurityScan(props.clientId, {
      repositoryUrl: repositoryUrl.value.trim(),
      ref: refName.value.trim() || undefined,
      scanners: scanners.value,
    })
    emit('started', res.scanId)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to start scan.'
  } finally {
    starting.value = false
  }
}
</script>

<style scoped>
.dialog-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.6); display: flex; align-items: center; justify-content: center; z-index: 100; padding: 1rem; }
.dialog { background: #111827; border: 1px solid rgba(56,189,248,0.25); border-radius: 14px; padding: 1.5rem; width: 100%; max-width: 480px; color: var(--color-text); }
.dialog h3 { margin: 0 0 0.4rem; font-size: 1.15rem; }
.dialog-sub { font-size: 0.84rem; color: var(--color-text-muted); margin: 0 0 1.1rem; line-height: 1.45; }
.field { display: block; margin-bottom: 1rem; font-size: 0.88rem; }
.field > span:first-child { display: block; margin-bottom: 0.35rem; font-weight: 600; }
.field input[type="url"], .field input[type="text"] { width: 100%; padding: 0.55rem 0.7rem; border-radius: 8px; border: 1px solid rgba(255,255,255,0.14); background: rgba(255,255,255,0.04); color: var(--color-text); font-size: 0.9rem; box-sizing: border-box; }
.hint { display: block; margin-top: 0.3rem; font-size: 0.75rem; color: var(--color-text-muted); font-weight: 400; }
.required { color: #fb7185; }
.optional { color: var(--color-text-muted); font-weight: 400; }
fieldset.field { border: 1px solid rgba(255,255,255,0.1); border-radius: 8px; padding: 0.6rem 0.8rem; }
fieldset.field legend { padding: 0 0.4rem; font-weight: 600; }
.check { display: inline-flex; align-items: center; gap: 0.4rem; margin-right: 1rem; font-size: 0.85rem; cursor: pointer; }
.banner-error { background: rgba(244,63,94,0.1); border: 1px solid rgba(244,63,94,0.3); color: #fda4af; padding: 0.6rem 0.8rem; border-radius: 8px; font-size: 0.84rem; margin-bottom: 1rem; }
.dialog-actions { display: flex; justify-content: flex-end; gap: 0.6rem; margin-top: 0.5rem; }
.btn-primary { display: inline-flex; align-items: center; gap: 0.5rem; }
.btn-ghost { background: transparent; border: 1px solid rgba(255,255,255,0.16); color: var(--color-text); border-radius: 8px; padding: 0.5rem 1rem; cursor: pointer; }
.btn-ghost:hover { border-color: rgba(56,189,248,0.4); }
</style>
