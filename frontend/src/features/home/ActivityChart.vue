<script setup lang="ts">
import { computed } from 'vue'
import type { DashboardDay } from '@/features/reports/types'
import { quantity } from '@/utils/money'
import { dayLabel, niceTicks, useChartTip } from './chart'
import ChartTooltip from './ChartTooltip.vue'

const props = defineProps<{ days: DashboardDay[] }>()

const WIDTH = 690
const PLOT_END = 615
const TOP = 28
const HEIGHT = 280
const BAR = 72
const STEP = 102

const series = [
  { key: 'completed', label: 'تکمیل‌شده', color: 'var(--xr-chart-teal)', ink: '#fff' },
  { key: 'active', label: 'در جریان', color: 'var(--xr-chart-orange)', ink: 'var(--xr-text)' },
  { key: 'cancelled', label: 'لغو شده', color: 'var(--xr-chart-sky)', ink: 'var(--xr-text)' },
] as const

const { tip, show, hide } = useChartTip()

const ticks = computed(() => niceTicks(Math.max(...props.days.map((d) => d.completed + d.active + d.cancelled), 0)))
const scale = computed(() => HEIGHT / ticks.value[ticks.value.length - 1])
const hasData = computed(() => props.days.some((d) => d.completed + d.active + d.cancelled > 0))

/** Right-to-left time axis: the oldest day sits next to the Y axis on the right. */
const bars = computed(() =>
  props.days.map((day, index) => {
    const x = PLOT_END - 7 - BAR - index * STEP
    let base = TOP + HEIGHT
    const segments = series.map((s) => {
      const value = day[s.key]
      const height = value * scale.value
      base -= height
      return { ...s, value, y: base, height }
    })
    return {
      x,
      label: dayLabel(day.date),
      lines: series.map((s) => `${s.label}: ${quantity(day[s.key])}`),
      segments: segments.filter((s) => s.value > 0),
    }
  }),
)
</script>

<template>
  <div class="activity">
    <div class="chart activity__plot" @mouseleave="hide">
      <svg :viewBox="`0 0 ${WIDTH} 348`" role="img" :aria-label="`آمار مراجعات شش روز اخیر`">
        <g v-for="value in ticks" :key="value">
          <line x1="0" :x2="PLOT_END" :y1="TOP + HEIGHT - value * scale" :y2="TOP + HEIGHT - value * scale" class="grid" />
          <text :x="PLOT_END + 10" :y="TOP + HEIGHT - value * scale + 4" class="tick">{{ quantity(value) }}</text>
        </g>
        <g v-for="bar in bars" :key="bar.label" @mousemove="show($event, bar.label, bar.lines)">
          <rect :x="bar.x" :y="TOP" :width="BAR" :height="HEIGHT" fill="transparent" />
          <g v-for="segment in bar.segments" :key="segment.key">
            <rect :x="bar.x" :y="segment.y" :width="BAR" :height="segment.height" :fill="segment.color" />
            <text v-if="segment.height >= 16" :x="bar.x + BAR / 2" :y="segment.y + segment.height / 2 + 4" :fill="segment.ink" class="value" :class="{ 'value--small': segment.height < 28 }">{{ quantity(segment.value) }}</text>
          </g>
          <text :x="bar.x + BAR / 2" :y="TOP + HEIGHT + 26" class="day">{{ bar.label }}</text>
        </g>
        <text v-if="!hasData" :x="PLOT_END / 2" :y="TOP + HEIGHT / 2" class="empty">هنوز مراجعه‌ای ثبت نشده است</text>
      </svg>
      <ChartTooltip :tip="tip" />
    </div>
    <ul class="activity__legend">
      <li v-for="s in series" :key="s.key"><span :style="{ background: s.color }"></span>{{ s.label }}</li>
    </ul>
    <table class="visually-hidden">
      <caption>آمار مراجعات شش روز اخیر</caption>
      <thead><tr><th>روز</th><th v-for="s in series" :key="s.key">{{ s.label }}</th></tr></thead>
      <tbody><tr v-for="day in days" :key="day.date"><td>{{ dayLabel(day.date) }}</td><td v-for="s in series" :key="s.key">{{ day[s.key] }}</td></tr></tbody>
    </table>
  </div>
</template>

<style scoped>
.activity {
  display: flex;
  gap: 20px;
}

.activity__plot {
  position: relative;
  flex: 1;
  min-width: 0;
}

svg {
  display: block;
  width: 100%;
  height: auto;
}

.grid { stroke: var(--xr-chart-grid); }
/* The page is RTL, so "end" is what makes axis numbers grow away from the plot. */
.tick { fill: var(--xr-text); font-size: 12px; text-anchor: end; }
.value { font-size: 12px; font-weight: 500; text-anchor: middle; pointer-events: none; }
.day { fill: var(--xr-text); font-size: 12px; font-weight: 600; text-anchor: middle; }
.empty { fill: var(--xr-text-muted); font-size: 15px; text-anchor: middle; }

.activity__legend {
  flex: none;
  width: 93px;
  margin: 45px 0 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
}

.activity__legend li {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}

.activity__legend span {
  flex: none;
  width: 12px;
  height: 12px;
  border-radius: 2px;
}

@media (max-width: 575.98px) {
  .activity {
    flex-direction: column;
  }

  /* The drawing shrinks to fit a phone, so its text is sized up to stay readable. */
  .tick,
  .value,
  .day {
    font-size: 22px;
  }

  .empty {
    font-size: 24px;
  }

  .value--small {
    display: none;
  }

  .activity__legend {
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
    width: auto;
    margin: 0;
  }

  .activity__legend li {
    margin: 0;
  }
}
</style>
