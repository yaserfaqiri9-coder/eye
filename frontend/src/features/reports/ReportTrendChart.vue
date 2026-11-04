<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import UiCard from '@/components/UiCard.vue'
import { dayLabel, niceTicks, useChartTip } from '@/features/home/chart'
import ChartTooltip from '@/features/home/ChartTooltip.vue'
import { formatDate } from '@/utils/date'
import { quantity } from '@/utils/money'
import { numberText } from './reportFormat'
import type { ReportTrend } from './types'

const props = defineProps<{ trend: ReportTrend }>()

const TOP = 30
const PLOT_HEIGHT = 200
const AXIS_BAND = 30
const START_GAP = 24
const BAR_MAX = 24
const LABEL_SPACE = 64
const TIP_HALF = 70

const root = ref<HTMLElement | null>(null)
const width = ref(640)
const { tip, hide } = useChartTip()
const activeIndex = ref<number | null>(null)
const focusIndex = ref(0)

/** Counting has no half steps, so an axis that would show one takes the next step count instead. */
const ticks = computed(() => {
  const peak = Math.max(...props.trend.points.map((p) => p.value), 0)
  const four = niceTicks(peak, 4)
  return four.every(Number.isInteger) ? four : niceTicks(peak, 5)
})
const axisTop = computed(() => ticks.value[ticks.value.length - 1])
const axisWidth = computed(() => Math.max(...ticks.value.map((t) => quantity(t).length)) * 7.5 + 12)
const plotEnd = computed(() => width.value - axisWidth.value)
const baseline = TOP + PLOT_HEIGHT
const yOf = (value: number) => baseline - (value * PLOT_HEIGHT) / axisTop.value

const hasData = computed(() => props.trend.points.some((p) => p.value > 0))
const slot = computed(() => (plotEnd.value - START_GAP) / Math.max(props.trend.points.length, 1))
const barWidth = computed(() => Math.min(BAR_MAX, slot.value * 0.65))
const labelEvery = computed(() => Math.max(1, Math.ceil(props.trend.points.length / Math.max(1, Math.floor((plotEnd.value - START_GAP) / LABEL_SPACE)))))

/** Square on the baseline and rounded at the value, with a shorter radius when the bar is too small for the full one. */
function barPath(x: number, top: number, w: number) {
  const r = Math.min(4, w / 2, baseline - top)
  return `M${x} ${baseline}V${top + r}Q${x} ${top} ${x + r} ${top}H${x + w - r}Q${x + w} ${top} ${x + w} ${top + r}V${baseline}Z`
}

/** Right-to-left time axis: the oldest day sits next to the Y axis on the right. */
const bars = computed(() =>
  props.trend.points.map((point, index) => {
    const center = plotEnd.value - slot.value * (index + 0.5)
    // A small day must still be visible next to a very large one.
    const top = point.value > 0 ? Math.min(yOf(point.value), baseline - 2) : baseline
    return {
      index,
      center,
      top,
      slotX: center - slot.value / 2,
      path: point.value > 0 ? barPath(center - barWidth.value / 2, top, barWidth.value) : null,
      day: dayLabel(point.date),
      date: formatDate(point.date),
      value: numberText(point.value, props.trend.kind),
    }
  }),
)

/** Only the highest day carries a number; the axis, tooltip and table carry the rest. */
const peakLabel = computed(() => {
  if (!hasData.value) return null
  const values = props.trend.points.map((p) => p.value)
  const bar = bars.value[values.indexOf(Math.max(...values))]
  const half = (bar.value.length * 7) / 2
  return { text: bar.value, x: Math.min(Math.max(bar.center, START_GAP + half), plotEnd.value - half), y: bar.top - 7 }
})

function showBar(index: number) {
  const bar = bars.value[index]
  if (!bar || !root.value) return
  activeIndex.value = index
  const rendered = root.value.clientWidth
  const scale = rendered / width.value
  tip.value = {
    x: Math.min(Math.max(bar.center * scale, TIP_HALF), rendered - TIP_HALF),
    y: bar.top * scale,
    title: bar.value,
    lines: [bar.date],
  }
}

function clearBar() {
  activeIndex.value = null
  hide()
}

function onKeydown(event: KeyboardEvent) {
  const last = bars.value.length - 1
  // The newest day is to the left, so the left arrow moves forward in time.
  const moves: Record<string, number> = {
    ArrowLeft: Math.min(focusIndex.value + 1, last),
    ArrowRight: Math.max(focusIndex.value - 1, 0),
    Home: 0,
    End: last,
  }
  if (event.key === 'Escape') return clearBar()
  const next = moves[event.key]
  if (next === undefined) return
  event.preventDefault()
  focusIndex.value = next
  root.value?.querySelector<SVGElement>(`[data-bar="${next}"]`)?.focus()
}

let observer: ResizeObserver | undefined
onMounted(() => {
  observer = new ResizeObserver(([entry]) => {
    if (entry.contentRect.width > 0) width.value = Math.round(entry.contentRect.width)
  })
  observer.observe(root.value!)
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <UiCard :title="trend.title">
    <div ref="root" class="chart report-trend" @pointerleave="clearBar">
      <svg :viewBox="`0 0 ${width} ${baseline + AXIS_BAND}`" role="group" :aria-label="trend.title" @keydown="onKeydown">
        <text v-if="trend.kind === 'money'" :x="plotEnd + 8" :y="TOP - 14" class="tick">افغانی</text>
        <g v-for="value in ticks" :key="value">
          <line :x1="START_GAP" :x2="plotEnd" :y1="yOf(value)" :y2="yOf(value)" class="grid" />
          <text :x="plotEnd + 8" :y="yOf(value) + 4" class="tick">{{ quantity(value) }}</text>
        </g>
        <template v-for="bar in bars" :key="bar.index">
          <path v-if="bar.path" :d="bar.path" class="bar" :class="{ 'bar--active': activeIndex === bar.index }" />
          <text v-if="bar.index % labelEvery === 0" :x="bar.center" :y="baseline + 20" class="day">{{ bar.day }}</text>
        </template>
        <text v-if="peakLabel" :x="peakLabel.x" :y="peakLabel.y" class="peak">{{ peakLabel.text }}</text>
        <text v-if="!hasData" :x="(plotEnd + START_GAP) / 2" :y="TOP + PLOT_HEIGHT / 2" class="empty">برای این دوره چیزی ثبت نشده است.</text>
        <rect
          v-for="bar in bars"
          :key="bar.index"
          :data-bar="bar.index"
          :x="bar.slotX"
          :y="TOP"
          :width="slot"
          :height="PLOT_HEIGHT"
          class="hit"
          role="img"
          :tabindex="bar.index === focusIndex ? 0 : -1"
          :aria-label="`${bar.date}: ${bar.value}`"
          @pointerenter="showBar(bar.index)"
          @focus="focusIndex = bar.index; showBar(bar.index)"
          @blur="clearBar"
        />
      </svg>
      <ChartTooltip :tip="tip" />
    </div>
    <details class="xr-section report-trend__table no-print">
      <summary>نمایش به صورت جدول</summary>
      <div class="xr-section__body">
        <div class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr>
                <th scope="col">تاریخ</th>
                <th scope="col">{{ trend.title }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="bar in bars" :key="bar.index">
                <td>{{ bar.date }}</td>
                <td>{{ bar.value }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </details>
  </UiCard>
</template>

<style scoped>
.report-trend {
  position: relative;
}

svg {
  display: block;
  width: 100%;
  height: auto;
}

.grid { stroke: var(--xr-chart-grid); stroke-width: 1; }
.bar { fill: var(--xr-chart-teal); }
.bar--active { fill: var(--xr-primary-end); }
/* The page is RTL, so "end" is what makes axis numbers grow away from the plot. */
.tick { fill: var(--xr-text-soft); font-size: 12px; font-variant-numeric: tabular-nums; text-anchor: end; }
.day { fill: var(--xr-text-soft); font-size: 12px; text-anchor: middle; }
.peak { fill: var(--xr-text); font-size: 12px; font-weight: 600; text-anchor: middle; }
.empty { fill: var(--xr-text-soft); font-size: 14px; text-anchor: middle; }

.hit {
  fill: transparent;
  outline: none;
  pointer-events: all;
}

.hit:focus-visible {
  stroke: var(--xr-text);
  stroke-width: 1.5;
}

.report-trend__table {
  margin-top: 12px;
}
</style>
