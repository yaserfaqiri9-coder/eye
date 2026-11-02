<script setup lang="ts">
import { quantity } from '@/utils/money'

withDefaults(
  defineProps<{
    label: string
    value: string | number
    icon: string
    tone?: 'primary' | 'orange' | 'red' | 'cyan'
  }>(),
  { tone: 'primary' },
)
</script>

<template>
  <div class="stat">
    <div class="stat__tile" :class="`stat__tile--${tone}`">
      <div class="stat__icon"><i :class="icon" aria-hidden="true"></i></div>
      <div class="stat__text">
        <div class="stat__value">{{ typeof value === 'number' ? quantity(value) : value }}</div>
        <div class="stat__label">{{ label }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* A white card behind a tinted tile with a tighter radius, so white shows at the corners. */
.stat {
  border-radius: var(--xr-radius-card);
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.stat__tile {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 113px;
  padding: 20px;
  border-radius: var(--xr-radius-tile);
}

.stat__icon {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 60px;
  height: 60px;
  border-radius: 50%;
  color: #fff;
  font-size: 22px;
}

.stat__text {
  min-width: 0;
  text-align: end;
}

.stat__value {
  color: var(--xr-text);
  font-size: var(--xr-fs-stat);
  line-height: 48.3px;
  overflow-wrap: anywhere;
}

.stat__label {
  color: var(--xr-text);
  font-size: 16.8px;
  line-height: 25.2px;
}

.stat__tile--primary { background: var(--xr-primary-light); }
.stat__tile--primary .stat__icon { background-image: var(--xr-gradient-primary); }
.stat__tile--orange { background: var(--xr-orange-light); }
.stat__tile--orange .stat__icon { background-image: var(--xr-gradient-orange); }
.stat__tile--red { background: var(--xr-red-light); }
.stat__tile--red .stat__icon { background-image: var(--xr-gradient-red); }
.stat__tile--cyan { background: var(--xr-cyan-light); }
.stat__tile--cyan .stat__icon { background-image: var(--xr-gradient-cyan); }
</style>
