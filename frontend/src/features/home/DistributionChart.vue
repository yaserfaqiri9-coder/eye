<script setup lang="ts">
import { computed } from 'vue'
import { quantity } from '@/utils/money'
import { niceTicks, useChartTip } from './chart'
import ChartTooltip from './ChartTooltip.vue'

const props = defineProps<{ rows: { name: string; count: number }[] }>()

const CENTER_X = 292
const CENTER_Y = 195
const INNER = 48
const OUTER = 142
const CORNER = 4

const { tip, show, hide } = useChartTip()

const ticks = computed(() => niceTicks(Math.max(...props.rows.map((r) => r.count), 0), 2))
const top = computed(() => ticks.value[ticks.value.length - 1])
const hasData = computed(() => props.rows.some((r) => r.count > 0))

function radiusOf(value: number) {
  return INNER + (value / top.value) * (OUTER - INNER)
}

function point(angle: number, radius: number) {
  const a = ((angle - 90) * Math.PI) / 180
  return [CENTER_X + radius * Math.cos(a), CENTER_Y + radius * Math.sin(a)]
}

const wedges = computed(() => {
  const step = 360 / Math.max(props.rows.length, 1)
  return props.rows.map((row, index) => {
    const start = index * step + 2.5
    const end = (index + 1) * step - 2.5
    const middle = (start + end) / 2
    const inner = INNER + CORNER
    const outer = Math.max(radiusOf(row.count) - CORNER, inner + 1)
    const [x1, y1] = point(start, inner)
    const [x2, y2] = point(start, outer)
    const [x3, y3] = point(end, outer)
    const [x4, y4] = point(end, inner)
    const [labelX, labelY] = point(middle, OUTER + 34)
    const cosine = Math.sin((middle * Math.PI) / 180)
    return {
      ...row,
      color: index % 2 === 0 ? 'var(--xr-chart-teal)' : 'var(--xr-chart-orange)',
      path: `M${x1} ${y1}L${x2} ${y2}A${outer} ${outer} 0 0 1 ${x3} ${y3}L${x4} ${y4}A${inner} ${inner} 0 0 0 ${x1} ${y1}Z`,
      labelX,
      labelY: labelY + 4,
      // The page is RTL, so a label growing to the right is anchored at its "end".
      anchor: Math.abs(cosine) < 0.2 ? 'middle' : cosine > 0 ? 'end' : 'start',
    }
  })
})
</script>

<template>
  <div class="chart distribution" @mouseleave="hide">
    <svg viewBox="0 0 585 395" role="img" aria-label="توزیع مراجعات بخش‌ها در سی روز اخیر">
      <circle v-for="value in ticks" :key="value" :cx="CENTER_X" :cy="CENTER_Y" :r="radiusOf(value)" class="ring" />
      <template v-for="wedge in wedges" :key="wedge.name">
        <path
          v-if="wedge.count > 0"
          :d="wedge.path"
          :fill="wedge.color"
          :stroke="wedge.color"
          :stroke-width="CORNER * 2"
          stroke-linejoin="round"
          @mousemove="show($event, wedge.name, [`${quantity(wedge.count)} مراجعه`])"
        />
        <text :x="wedge.labelX" :y="wedge.labelY" :text-anchor="wedge.anchor" class="label">{{ wedge.name }}</text>
      </template>
      <text v-for="value in ticks" :key="`t${value}`" :x="CENTER_X" :y="CENTER_Y - radiusOf(value) + 4" class="tick">{{ quantity(value) }}</text>
      <text v-if="!hasData" :x="CENTER_X" :y="CENTER_Y + 4" class="empty">بدون داده</text>
    </svg>
    <ChartTooltip :tip="tip" />
    <table class="visually-hidden">
      <caption>توزیع مراجعات بخش‌ها در سی روز اخیر</caption>
      <thead><tr><th>بخش</th><th>تعداد</th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.name"><td>{{ row.name }}</td><td>{{ row.count }}</td></tr></tbody>
    </table>
  </div>
</template>

<style scoped>
.distribution {
  position: relative;
}

svg {
  display: block;
  width: 100%;
  height: auto;
}

.ring { fill: none; stroke: var(--xr-chart-grid); }
.label { fill: var(--xr-text); font-size: 13px; }
.tick { fill: var(--xr-text); font-size: 11px; text-anchor: middle; pointer-events: none; }
.empty { fill: var(--xr-text-muted); font-size: 12px; text-anchor: middle; }

/* The drawing shrinks to fit a phone, so its text is sized up to stay readable. */
@media (max-width: 575.98px) {
  .label { font-size: 20px; }
  .tick { font-size: 18px; }
  .empty { font-size: 18px; }
}
</style>
