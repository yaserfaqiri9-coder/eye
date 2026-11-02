<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    /** Share of the ring drawn in grey, from 0 to 1; the rest stays in the pale tone. */
    fraction: number
    value: string
    label: string
    tone?: 'muted' | 'yellow'
  }>(),
  { tone: 'muted' },
)

const RADIUS = 47.5
const LENGTH = 2 * Math.PI * RADIUS

const dash = computed(() => `${Math.min(1, Math.max(0, props.fraction)) * LENGTH} ${LENGTH}`)
</script>

<template>
  <div class="ring" :class="`ring--${tone}`">
    <svg viewBox="0 0 100 100" aria-hidden="true">
      <circle class="ring__rest" cx="50" cy="50" :r="RADIUS" />
      <!-- Mirrored for right-to-left: starts at 12 o'clock and runs counter-clockwise. -->
      <circle class="ring__part" cx="50" cy="50" :r="RADIUS" :stroke-dasharray="dash" transform="rotate(-90 50 50) scale(1 -1) translate(0 -100)" />
    </svg>
    <div class="ring__center">
      <div class="ring__value">{{ value }}</div>
      <div class="ring__label">{{ label }}</div>
    </div>
  </div>
</template>

<style scoped>
.ring {
  position: relative;
  flex: none;
  width: 100px;
  height: 100px;
}

svg {
  display: block;
  width: 100%;
  height: 100%;
}

circle {
  fill: none;
  stroke-width: 5;
}

.ring__part { stroke: #d6d8d5; }
.ring--muted .ring__rest { stroke: var(--xr-primary-light); }
.ring--yellow .ring__rest { stroke: #fff9e6; }

.ring__center {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
}

.ring__value {
  color: var(--xr-text-muted);
  font-size: 24px;
  font-weight: 500;
  line-height: 28.8px;
}

.ring--yellow .ring__value {
  color: var(--xr-yellow);
}

.ring__label {
  line-height: 25.2px;
}
</style>
