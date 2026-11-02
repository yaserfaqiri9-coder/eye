<script setup lang="ts">
import type { ChartTip } from './chart'

defineProps<{ tip: ChartTip | null }>()
</script>

<template>
  <div v-if="tip" class="chart-tip" :style="{ left: `${tip.x}px`, top: `${tip.y}px` }" role="status">
    <div class="chart-tip__title">{{ tip.title }}</div>
    <div v-for="line in tip.lines" :key="line">{{ line }}</div>
  </div>
</template>

<style scoped>
.chart-tip {
  position: absolute;
  z-index: 5;
  padding: 8px 12px;
  border-radius: var(--xr-radius-control);
  background: var(--xr-text);
  color: #fff;
  font-size: 12px;
  line-height: 1.7;
  white-space: nowrap;
  pointer-events: none;
  transform: translate(-50%, calc(-100% - 12px));
}

.chart-tip__title {
  font-weight: 600;
}
</style>
