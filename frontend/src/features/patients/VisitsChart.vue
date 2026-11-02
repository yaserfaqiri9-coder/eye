<script setup lang="ts">
import { computed } from 'vue'
import ChartTooltip from '@/features/home/ChartTooltip.vue'
import { niceTicks, useChartTip } from '@/features/home/chart'
import { afghanMonths, todayShamsi } from '@/utils/date'
import { quantity } from '@/utils/money'

const props = defineProps<{ dates: string[] }>()

// Plot geometry measured from the template's chart, with the value axis moved to the right.
const WIDTH = 532
const HEIGHT = 280
const PLOT_START = 10
const PLOT_WIDTH = 455
const TOP = 30
const PLOT_HEIGHT = 208
const BAR = 22
const MONTHS = 10

const STEP = PLOT_WIDTH / MONTHS
const BOTTOM = TOP + PLOT_HEIGHT
const PLOT_END = PLOT_START + PLOT_WIDTH

const shamsiParts = new Intl.DateTimeFormat('en-u-ca-persian-nu-latn', { timeZone: 'UTC', year: 'numeric', month: 'numeric' })

function monthKey(iso: string) {
  const parts = shamsiParts.formatToParts(new Date(`${iso}T00:00:00Z`))
  const get = (type: string) => Number(parts.find((p) => p.type === type)!.value)
  return get('year') * 12 + get('month') - 1
}

/** The last ten Shamsi months, oldest first, whether or not the patient visited in them. */
const months = computed(() => {
  const today = todayShamsi()
  const current = today.year * 12 + today.month - 1
  const counts = new Map<number, number>()
  for (const date of props.dates) {
    const key = monthKey(date)
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return Array.from({ length: MONTHS }, (_, index) => {
    const key = current - MONTHS + 1 + index
    return { key, label: afghanMonths[key % 12], count: counts.get(key) ?? 0 }
  })
})

const ticks = computed(() => niceTicks(Math.max(...months.value.map((m) => m.count), 0), 4))
const scale = computed(() => PLOT_HEIGHT / ticks.value[ticks.value.length - 1])
const hasData = computed(() => months.value.some((m) => m.count > 0))

const { tip, show, hide } = useChartTip()

const bars = computed(() =>
  months.value.map((month, index) => {
    const center = PLOT_END - STEP * (index + 0.5)
    const height = month.count * scale.value
    const x = center - BAR / 2
    const y = BOTTOM - height
    const dome = Math.min(5, height)
    return {
      ...month,
      center,
      x,
      height,
      slot: PLOT_END - STEP * (index + 1),
      outline: `M${x} ${BOTTOM}V${y + dome}Q${x + BAR / 2} ${y - dome} ${x + BAR} ${y + dome}V${BOTTOM}Z`,
      shine: `M${x} ${BOTTOM}V${y + dome}Q${x + BAR / 4} ${y - dome / 3} ${center} ${y}V${BOTTOM}Z`,
    }
  }),
)
</script>

<template>
  <div class="chart" @mouseleave="hide">
    <svg :viewBox="`0 0 ${WIDTH} ${HEIGHT}`" role="img" aria-label="تعداد مراجعات ده ماه اخیر">
      <template v-for="(value, index) in ticks" :key="value">
        <rect
          v-if="index < ticks.length - 1 && index % 2 === 0"
          :x="PLOT_START"
          :y="BOTTOM - ticks[index + 1] * scale"
          :width="PLOT_WIDTH"
          :height="(ticks[index + 1] - value) * scale"
          class="band"
        />
        <line v-if="index > 0" :x1="PLOT_START" :x2="PLOT_END" :y1="BOTTOM - value * scale" :y2="BOTTOM - value * scale" class="grid" />
        <text :x="PLOT_END + 13" :y="BOTTOM - value * scale + 4" class="tick">{{ quantity(value) }}</text>
      </template>
      <text class="title" :transform="`translate(${WIDTH - 20} ${TOP + PLOT_HEIGHT / 2}) rotate(90)`">مراجعه</text>

      <g v-for="bar in bars" :key="bar.key" @mousemove="show($event, bar.label, [`مراجعه: ${quantity(bar.count)}`])">
        <rect :x="bar.slot" :y="TOP" :width="STEP" :height="PLOT_HEIGHT" fill="transparent" />
        <template v-if="bar.count > 0">
          <path :d="bar.outline" class="bar" />
          <path :d="bar.shine" class="shine" />
          <path :d="bar.outline" class="edge" />
        </template>
        <text :x="bar.center" :y="BOTTOM + 24" class="month">{{ bar.label }}</text>
      </g>

      <line :x1="PLOT_START" :x2="PLOT_END" :y1="BOTTOM" :y2="BOTTOM" class="axis" />
      <line v-for="index in MONTHS + 1" :key="index" :x1="PLOT_START + STEP * (index - 1)" :x2="PLOT_START + STEP * (index - 1)" :y1="BOTTOM" :y2="BOTTOM + 6" class="axis" />
      <text v-if="!hasData" :x="PLOT_START + PLOT_WIDTH / 2" :y="TOP + PLOT_HEIGHT / 2" class="empty">هنوز مراجعه‌ای ثبت نشده است</text>
    </svg>
    <ChartTooltip :tip="tip" />
    <table class="visually-hidden">
      <caption>تعداد مراجعات ده ماه اخیر</caption>
      <thead><tr><th>ماه</th><th>مراجعه</th></tr></thead>
      <tbody><tr v-for="month in months" :key="month.key"><td>{{ month.label }}</td><td>{{ month.count }}</td></tr></tbody>
    </table>
  </div>
</template>

<style scoped>
.chart {
  position: relative;
}

svg {
  display: block;
  width: 100%;
  height: auto;
  direction: ltr;
}

text {
  fill: var(--xr-chart-axis);
  font-size: 12px;
}

.band { fill: var(--xr-chart-band); opacity: 0.6; }
.grid { stroke: var(--xr-chart-grid); }
.axis { stroke: #78909c; }
.tick { text-anchor: start; }
.month { text-anchor: middle; }
.title { text-anchor: middle; }
.bar { fill: var(--xr-primary); opacity: 0.72; }
.shine { fill: #fff; opacity: 0.22; }
.edge { fill: none; stroke: var(--xr-primary); stroke-width: 2; }
.empty { fill: var(--xr-text-muted); font-size: 15px; text-anchor: middle; }

/* The drawing shrinks to fit a phone, so its text is sized up to stay readable. */
@media (max-width: 575.98px) {
  .tick,
  .title {
    font-size: 18px;
  }

  .month {
    font-size: 15px;
  }

  .empty {
    font-size: 22px;
  }
}
</style>
