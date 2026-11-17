<script setup lang="ts">
import { computed } from 'vue'
import { quantity } from '@/utils/money'

const props = withDefaults(
  defineProps<{
    label: string
    value: string | number
    icon: string
    tone?: 'primary' | 'orange' | 'red' | 'cyan'
    /** A short line under the label, such as the change against the previous period. */
    note?: string
  }>(),
  { tone: 'primary' },
)

const display = computed(() => (typeof props.value === 'number' ? quantity(props.value) : props.value))
</script>

<template>
  <div class="stat">
    <div class="stat__tile" :class="`stat__tile--${tone}`">
      <div class="stat__icon"><i :class="icon" aria-hidden="true"></i></div>
      <div class="stat__text" :title="[display, label, note].filter(Boolean).join(' — ')">
        <div class="stat__value">{{ display }}</div>
        <div class="stat__label">{{ label }}</div>
        <div v-if="note" class="stat__note">{{ note }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/*
 * A white card behind a tinted tile with a tighter radius, so white shows at the corners.
 * Every card is the same fixed height on every page: long text never adds a line, it shrinks with
 * the card's width and is cut with an ellipsis as a last resort (the full text is in the tooltip).
 */
.stat {
  display: flex;
  flex-direction: column;
  min-width: 0;
  container-type: inline-size;
  border-radius: var(--xr-radius-card);
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.stat__tile {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  height: 113px;
  padding: 16px 20px;
  overflow: hidden;
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
  flex: 1;
  min-width: 0;
  text-align: end;
}

.stat__value,
.stat__label,
.stat__note {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.stat__value {
  color: var(--xr-text);
  font-size: clamp(14px, 9cqi, var(--xr-fs-stat));
  line-height: 1.6;
}

.stat__label {
  color: var(--xr-text);
  font-size: clamp(12px, 6.5cqi, 16.8px);
  line-height: 1.5;
}

.stat__note {
  color: var(--xr-text-soft);
  font-size: 12px;
  line-height: 1.5;
}

/* Narrow card: a smaller icon, then none, so the text keeps its room; the height never changes. */
@container (max-width: 260px) {
  .stat__tile { gap: 10px; padding-inline: 14px; }
  .stat__icon { width: 44px; height: 44px; font-size: 18px; }
}

@container (max-width: 180px) {
  .stat__icon { display: none; }
  .stat__text { text-align: center; }
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
