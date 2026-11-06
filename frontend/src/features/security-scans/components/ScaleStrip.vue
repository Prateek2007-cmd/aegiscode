<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="scale-strip" aria-label="Repository scale">
    <div class="scale-stats">
      <div class="scale-stat">
        <span class="scale-num"><CountUp :value="filesScanned" /></span>
        <span class="scale-cap">files scanned</span>
      </div>
      <div class="scale-stat">
        <span class="scale-num"><CountUp :value="manifestCount" /></span>
        <span class="scale-cap">manifests found</span>
      </div>
      <div class="scale-stat">
        <span v-if="sbomMissing" class="scale-num scale-num-muted">—</span>
        <span v-else class="scale-num"><CountUp :value="sbomComponentCount ?? 0" /></span>
        <span class="scale-cap">SBOM components</span>
        <span v-if="sbomMissing" class="scale-note">no SBOM produced</span>
      </div>
    </div>

    <div class="lang-block">
      <p class="lang-title">Languages inferred from manifests</p>
      <div v-if="languages.length" class="lang-bar" role="img" :aria-label="langAriaLabel">
        <span
          v-for="(lang, i) in languages"
          :key="lang.language"
          class="lang-seg"
          :style="{ width: `${lang.share * 100}%`, background: langPalette[i % langPalette.length] }"
          :title="`${lang.language}: ${lang.manifests.join(', ')}`"
        ></span>
      </div>
      <ul v-if="languages.length" class="lang-legend">
        <li v-for="(lang, i) in languages" :key="lang.language">
          <span class="lang-dot" :style="{ background: langPalette[i % langPalette.length] }"></span>
          {{ lang.language }}
          <span class="lang-manifests">{{ lang.manifests.join(', ') }}</span>
        </li>
      </ul>
      <p v-else class="lang-empty">No manifests found — languages can't be inferred for this scan.</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { SecurityScan } from '../types'
import { inferLanguages } from '../types'
import CountUp from './CountUp.vue'

const props = defineProps<{
  scan: SecurityScan
  sbomComponentCount: number | null
  sbomMissing: boolean
}>()

const filesScanned = computed(() => props.scan.coverage?.filesScanned ?? 0)
const manifestCount = computed(() => props.scan.coverage?.manifestsFound.length ?? 0)
const languages = computed(() => inferLanguages(props.scan.coverage?.manifestsFound ?? []))
const langPalette = ['var(--color-accent)', 'var(--color-warning)', 'var(--color-success)', 'var(--color-danger)', 'var(--color-suggestion)', 'var(--color-warning)']
const langAriaLabel = computed(() =>
  languages.value.map((l) => `${l.language} ${Math.round(l.share * 100)}%`).join(', '),
)
</script>

<style scoped>
.scale-strip {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-xl);
  padding: 1.1rem 1.4rem;
  margin-bottom: 1rem;
  color: var(--color-text);
}
.scale-stats { display: grid; grid-template-columns: repeat(3, 1fr); gap: 1rem; }
@media (max-width: 640px) { .scale-stats { grid-template-columns: 1fr; } }
.scale-stat { display: flex; flex-direction: column; gap: 0.15rem; border-left: 1px solid var(--color-warning); padding-left: 0.8rem; }
.scale-num {
  font-family: var(--font-mono);
  font-size: 1.9rem; font-weight: 700; color: var(--color-text); line-height: 1.1;
}
.scale-num-muted { color: var(--color-text-faint); }
.scale-cap { font-size: 0.78rem; color: var(--color-text-muted); }
.scale-note { font-size: 0.72rem; color: var(--color-warning); }
.lang-block { margin-top: 1.1rem; }
.lang-title { margin: 0 0 0.5rem; font-size: 0.8rem; font-weight: 600; color: var(--color-text); }
.lang-bar { display: flex; height: 10px; border-radius: 9999px; overflow: hidden; background: var(--color-muted-soft); }
.lang-seg { height: 100%; }
.lang-legend { list-style: none; margin: 0.6rem 0 0; padding: 0; display: flex; flex-wrap: wrap; gap: 0.4rem 1.2rem; font-size: 0.8rem; }
.lang-dot { display: inline-block; width: 9px; height: 9px; border-radius: 3px; margin-right: 0.35rem; }
.lang-manifests { color: var(--color-text-muted); font-size: 0.74rem; margin-left: 0.3rem; }
.lang-empty { margin: 0; font-size: 0.8rem; color: var(--color-text-muted); }
</style>
