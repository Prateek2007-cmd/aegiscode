<template>
  <div class="aegis-logo" :style="{ width: `${size}px`, height: `${size}px` }">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" fill="none" class="aegis-logo-svg">
      <defs>
        <linearGradient :id="`shieldGrad-${instanceId}`" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stop-color="#00f2fe" />
          <stop offset="50%" stop-color="#38bdf8" />
          <stop offset="100%" stop-color="#2563eb" />
        </linearGradient>
        <linearGradient :id="`coreGrad-${instanceId}`" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stop-color="#10b981" />
          <stop offset="100%" stop-color="#06b6d4" />
        </linearGradient>
        <linearGradient :id="`glowGrad-${instanceId}`" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stop-color="#38bdf8" stop-opacity="0.25" />
          <stop offset="100%" stop-color="#0284c7" stop-opacity="0.05" />
        </linearGradient>
      </defs>

      <!-- Outer Shield Outline -->
      <path d="M50 5 L88 20 C88 56 68 84 50 95 C32 84 12 56 12 20 Z" 
            fill="#090d16" 
            :stroke="`url(#shieldGrad-${instanceId})`" 
            stroke-width="3.5" 
            stroke-linejoin="round" />

      <!-- Inner Protective Shield Layer -->
      <path d="M50 14 L80 26 C80 54 64 77 50 86 C36 77 20 54 20 26 Z" 
            :fill="`url(#glowGrad-${instanceId})`" 
            stroke="#38bdf8" 
            stroke-width="1.2" 
            stroke-dasharray="3 2"
            stroke-linejoin="round" />

      <!-- Left Code Bracket < -->
      <path d="M39 38 L30 48 L39 58" 
            :stroke="`url(#coreGrad-${instanceId})`" 
            stroke-width="3.2" 
            stroke-linecap="round" 
            stroke-linejoin="round" />

      <!-- Right Code Bracket > -->
      <path d="M61 38 L70 48 L61 58" 
            :stroke="`url(#coreGrad-${instanceId})`" 
            stroke-width="3.2" 
            stroke-linecap="round" 
            stroke-linejoin="round" />

      <!-- Center Defense Core / Shield Slash -->
      <path d="M55 35 L45 61" 
            stroke="#38bdf8" 
            stroke-width="3.2" 
            stroke-linecap="round" />

      <!-- Top Guardrail Active Status Dot -->
      <circle cx="50" cy="24" r="3.2" fill="#10b981" />
    </svg>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    size?: number
  }>(),
  {
    size: 38,
  },
)

const instanceId = computed(() => Math.random().toString(36).substring(2, 9))
</script>

<style scoped>
.aegis-logo {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  filter: drop-shadow(0 0 10px rgba(56, 189, 248, 0.35));
  transition: filter 0.3s ease, transform 0.3s ease;
}

.aegis-logo:hover {
  filter: drop-shadow(0 0 16px rgba(56, 189, 248, 0.6));
  transform: scale(1.04);
}

.aegis-logo-svg {
  width: 100%;
  height: 100%;
  display: block;
}
</style>
