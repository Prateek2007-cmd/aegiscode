<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <span class="count-up">{{ display }}</span>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'

const props = withDefaults(defineProps<{ value: number; durationMs?: number }>(), {
  durationMs: 800,
})

const display = ref('0')
let rafId = 0
let startValue = 0

function reducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

function animateTo(target: number): void {
  cancelAnimationFrame(rafId)
  if (reducedMotion() || props.durationMs <= 0) {
    display.value = String(target)
    startValue = target
    return
  }
  const from = startValue
  const start = performance.now()
  const tick = (now: number): void => {
    const t = Math.min(1, (now - start) / props.durationMs)
    // Ease-out cubic: fast start, gentle landing.
    const eased = 1 - Math.pow(1 - t, 3)
    const current = Math.round(from + (target - from) * eased)
    display.value = String(current)
    if (t < 1) {
      rafId = requestAnimationFrame(tick)
    } else {
      startValue = target
    }
  }
  rafId = requestAnimationFrame(tick)
}

watch(
  () => props.value,
  (v) => animateTo(v),
)

onMounted(() => animateTo(props.value))
onUnmounted(() => cancelAnimationFrame(rafId))
</script>

<style scoped>
.count-up { font-variant-numeric: tabular-nums; }
</style>
