<script setup lang="ts">
import { computed } from 'vue'
import type { DashboardDay } from '@/features/reports/types'
import { quantity } from '@/utils/money'
import { dayLabel, niceTicks, useChartTip } from './chart'
import ChartTooltip from './ChartTooltip.vue'

const props = defineProps<{ days: DashboardDay[] }>()

const PLOT_END = 525
const TOP = 37
const HEIGHT = 306
const BAR = 16
const GAP = 5
const STEP = 86

const { tip, show, hide } = useChartTip()

const ticks = computed(() => niceTicks(Math.max(...props.days.flatMap((d) => [d.male, d.female]), 0), 4))
const scale = computed(() => HEIGHT / ticks.value[ticks.value.length - 1])
const hasData = computed(() => props.days.some((d) => d.male + d.female > 0))

/** A rounded-top bar; a value too small for the full cap gets a shorter radius so it never pokes below the baseline. */
function barPath(x: number, value: number) {
  const height = value * scale.value
  const r = Math.min(BAR / 2, height)
  const top = TOP + HEIGHT - height
  return `M${x} ${TOP + HEIGHT}V${top + r}A${BAR / 2} ${r} 0 0 1 ${x + BAR} ${top + r}V${TOP + HEIGHT}Z`
}

const groups = computed(() =>
  props.days.map((day, index) => {
    const center = PLOT_END - 46 - index * STEP
    return {
      center,
      label: dayLabel(day.date),
      lines: [`مرد: ${quantity(day.male)}`, `زن: ${quantity(day.female)}`],
      male: day.male > 0 ? barPath(center + GAP / 2, day.male) : null,
      female: day.female > 0 ? barPath(center - GAP / 2 - BAR, day.female) : null,
    }
  }),
)
</script>

<template>
  <div class="chart patients-in" @mouseleave="hide">
    <svg viewBox="0 0 565 375" role="img" aria-label="مریضان مرد و زن در شش روز اخیر">
      <g v-for="value in ticks" :key="value">
        <line x1="0" :x2="PLOT_END" :y1="TOP + HEIGHT - value * scale" :y2="TOP + HEIGHT - value * scale" class="grid" />
        <text :x="PLOT_END + 10" :y="TOP + HEIGHT - value * scale + 4" class="tick">{{ quantity(value) }}</text>
      </g>
      <g v-for="group in groups" :key="group.label" @mousemove="show($event, group.label, group.lines)">
        <rect :x="group.center - 40" :y="TOP" width="80" :height="HEIGHT" fill="transparent" />
        <path v-if="group.male" :d="group.male" fill="var(--xr-chart-teal)" />
        <path v-if="group.female" :d="group.female" fill="var(--xr-chart-orange)" />
        <text :x="group.center" :y="TOP + HEIGHT + 24" class="tick tick--middle">{{ group.label }}</text>
      </g>
      <text v-if="!hasData" :x="PLOT_END / 2" :y="TOP + HEIGHT / 2" class="empty">هنوز مراجعه‌ای ثبت نشده است</text>
    </svg>
    <ul class="patients-in__legend">
      <li><span style="background: var(--xr-chart-teal)"></span>مرد</li>
      <li><span style="background: var(--xr-chart-orange)"></span>زن</li>
    </ul>
    <ChartTooltip :tip="tip" />
    <table class="visually-hidden">
      <caption>مریضان مرد و زن در شش روز اخیر</caption>
      <thead><tr><th>روز</th><th>مرد</th><th>زن</th></tr></thead>
      <tbody><tr v-for="day in days" :key="day.date"><td>{{ dayLabel(day.date) }}</td><td>{{ day.male }}</td><td>{{ day.female }}</td></tr></tbody>
    </table>
  </div>
</template>

<style scoped>
.patients-in {
  position: relative;
}

svg {
  display: block;
  width: 100%;
  height: auto;
}

.grid { stroke: var(--xr-chart-grid); }
/* The page is RTL, so "end" is what makes axis numbers grow away from the plot. */
.tick { fill: var(--xr-text); font-size: 12px; text-anchor: end; }
.tick--middle { text-anchor: middle; }
.empty { fill: var(--xr-text-muted); font-size: 15px; text-anchor: middle; }

/* The drawing shrinks to fit a phone, so its text is sized up to stay readable. */
@media (max-width: 575.98px) {
  .tick { font-size: 20px; }
  .empty { font-size: 22px; }
}

.patients-in__legend {
  display: flex;
  justify-content: center;
  gap: 16px;
  margin: 4px 0 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
}

.patients-in__legend li {
  display: flex;
  align-items: center;
  gap: 6px;
}

.patients-in__legend span {
  width: 12px;
  height: 12px;
  border-radius: 2px;
}
</style>
