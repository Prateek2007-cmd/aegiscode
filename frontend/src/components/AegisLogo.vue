<template>
  <div class="aegis-logo" :style="{ width: `${size}px`, height: `${size}px` }">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" fill="none" class="aegis-logo-svg">
      <!-- AegisCode mark — flat black / white / pink. No gradients, no glow:
           a white shield outline with the pink code brackets at its core. -->

      <!-- Outer Shield Outline -->
      <path d="M50 5 L88 20 C88 56 68 84 50 95 C32 84 12 56 12 20 Z"
            fill="#111111"
            stroke="#ffffff"
            stroke-width="3.5"
            stroke-linejoin="round" />

      <!-- Inner Protective Shield Layer -->
      <path d="M50 14 L80 26 C80 54 64 77 50 86 C36 77 20 54 20 26 Z"
            fill="none"
            stroke="#ffffff"
            stroke-opacity="0.35"
            stroke-width="1.2"
            stroke-dasharray="3 2"
            stroke-linejoin="round" />

      <!-- Left Code Bracket < -->
      <path d="M39 38 L30 48 L39 58"
            stroke="#ec4899"
            stroke-width="3.2"
            stroke-linecap="round"
            stroke-linejoin="round" />

      <!-- Right Code Bracket > -->
      <path d="M61 38 L70 48 L61 58"
            stroke="#ec4899"
            stroke-width="3.2"
            stroke-linecap="round"
            stroke-linejoin="round" />

      <!-- Center Defense Core / Shield Slash -->
      <path d="M55 35 L45 61"
            stroke="#ec4899"
            stroke-width="3.2"
            stroke-linecap="round" />

      <!-- Top Guardrail Active Status Dot -->
      <circle cx="50" cy="24" r="3.2" fill="#ec4899" />
      <circle cx="50" cy="24" r="5" stroke="#ec4899" stroke-width="1" stroke-opacity="0.6" fill="none" />
    </svg>
  </div>
</template>

<script setup lang="ts">
const props = withDefaults(
  defineProps<{
    size?: number
  }>(),
  {
    size: 38,
  },
)
</script>

<style scoped>
.aegis-logo {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.aegis-logo-svg {
  width: 100%;
  height: 100%;
  display: block;
}
</style>
