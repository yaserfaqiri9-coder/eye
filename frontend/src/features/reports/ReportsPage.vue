<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, downloadFile, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiFilterBar from '@/components/UiFilterBar.vue'
import UiPeriodFilter from '@/components/UiPeriodFilter.vue'
import { useHospital } from '@/features/print/paper'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { kindOf, periodChoices, quickRange, type DateRange, type PeriodKind } from '@/utils/period'
import { reportGroups } from './reportCatalog'
import { numberText, periodWords } from './reportFormat'
import ReportArtDefs from './ReportArtDefs.vue'
import ReportGallery from './ReportGallery.vue'
import ReportStat from './ReportStat.vue'
import ReportTableCard from './ReportTableCard.vue'
import type { ReportDocument } from './types'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const { hospital, load: loadHospital } = useHospital()

const groups = computed(() =>
  reportGroups
    .map((group) => ({ ...group, reports: group.reports.filter((report) => report.needs.some((need) => auth.can(need))) }))
    .filter((group) => group.reports.length > 0),
)
const allowed = computed(() => groups.value.flatMap((group) => group.reports))
/** No report in the address means the gallery of reports; an unknown or forbidden one is treated the same. */
const active = computed(() => allowed.value.find((report) => report.key === route.query.report) ?? null)
const activeGroup = computed(() => groups.value.find((group) => group.reports.some((report) => report.key === active.value?.key))?.key)
const galleryGroup = computed(() => (typeof route.query.group === 'string' ? route.query.group : undefined))
const chooseGroup = (key: string) => router.replace({ query: { group: key } })

const isoDate = /^\d{4}-\d{2}-\d{2}$/

/** What the address asks for, or today when it asks for something that makes no sense. */
const selected = computed<DateRange & { kind: PeriodKind }>(() => {
  const { period, from, to } = route.query
  if (period === 'custom' && typeof from === 'string' && typeof to === 'string' && isoDate.test(from) && isoDate.test(to) && from <= to) {
    return { kind: 'custom', from, to }
  }
  const choice = periodChoices.find((c) => c.kind === period && c.kind !== 'custom')?.kind ?? 'today'
  return { kind: choice, ...quickRange(choice as Exclude<PeriodKind, 'custom'>) }
})

function queryFor(report: string, kind: PeriodKind, range?: DateRange) {
  return kind === 'custom' && range ? { report, period: kind, from: range.from, to: range.to } : { report, period: kind }
}

const chooseReport = (key: string) => router.push({ query: queryFor(key, selected.value.kind, selected.value) })

/** The shared period filter reads and writes the address, so back, forward and reload keep the days. */
const period = computed<DateRange | null>({
  get: () => ({ from: selected.value.from, to: selected.value.to }),
  set: (range) => {
    if (range && active.value) router.push({ query: queryFor(active.value.key, kindOf(range), range) })
  },
})

const doc = ref<ReportDocument | null>(null)
const loading = ref(false)
const error = ref('')
const loadedAt = ref<string | null>(null)

const daysQuery = () => (active.value?.noPeriod ? '' : new URLSearchParams({ from: selected.value.from, to: selected.value.to }).toString())

let controller: AbortController | null = null
async function load() {
  const report = active.value
  if (!report) return
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  loading.value = true
  error.value = ''
  try {
    const days = daysQuery()
    doc.value = await api<ReportDocument>(`/reports/${report.key}${days ? `?${days}` : ''}`, { signal })
    loadedAt.value = new Date().toISOString()
  } catch (cause) {
    if (signal.aborted) return
    error.value = errorMessage(cause)
    // The previous answer belongs to another choice, and must not stand under this one.
    doc.value = null
  }
  loading.value = false
}

const requestKey = computed(() => {
  if (route.name !== 'reports' || !active.value) return null
  return active.value.noPeriod ? active.value.key : `${active.value.key}|${selected.value.from}|${selected.value.to}`
})
watch(requestKey, (key) => key && load(), { immediate: true })

// An unknown or forbidden choice in the address is replaced, so the address always says what is on screen.
watch(
  () => route.query,
  (query) => {
    if (route.name !== 'reports') return
    if (!active.value) {
      if (query.report !== undefined || query.period !== undefined) router.replace({ query: galleryGroup.value ? { group: galleryGroup.value } : {} })
      return
    }
    if (query.report === undefined && query.period === undefined) return
    const wanted = queryFor(active.value.key, selected.value.kind, selected.value) as Record<string, string>
    const same = Object.keys({ ...wanted, ...query }).every((key) => query[key] === wanted[key])
    if (!same) router.replace({ query: wanted })
  },
  { immediate: true },
)

/** One row of four figures at most heads a report; the server already sends no more, this only guards the page. */
const MAX_TILES = 4
const tiles = computed(() => doc.value?.tiles.slice(0, MAX_TILES) ?? [])
const table = computed(() => doc.value?.tables[0] ?? null)

const canExcel = computed(() => auth.can('reports.export'))
const canPdf = computed(() => auth.can('reports.print') || auth.can('reports.export'))
const saving = ref<'xlsx' | 'pdf' | null>(null)

/** The server writes the file from the same report, so the file always matches the screen. */
async function save(format: 'xlsx' | 'pdf') {
  if (!active.value || saving.value) return
  saving.value = format
  error.value = ''
  try {
    const days = daysQuery()
    await downloadFile(`/reports/${active.value.key}/export?format=${format}${days ? `&${days}` : ''}`, `report-${active.value.key}.${format}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = null
  }
}

onMounted(loadHospital)
onBeforeUnmount(() => controller?.abort())
</script>

<template>
  <div class="report-page">
    <UiCard v-if="allowed.length === 0">
      <p class="xr-state mb-0">هنوز گزارشی برای نقش شما باز نشده است. اگر به گزارشی نیاز دارید، به مدیر سیستم بگویید.</p>
    </UiCard>

    <ReportGallery v-else-if="!active" :groups="groups" :group="galleryGroup" @update:group="chooseGroup" @open="chooseReport" />

    <!--
      One report on its own page: a single bar with its name, its days and its actions (with the way back to
      the gallery), then at most four figures and its tables.
    -->
    <div v-else class="report-layout">
      <ReportArtDefs />
      <div class="report-main">
        <UiFilterBar :title="doc?.title ?? active.name" :subtitle="doc ? periodWords(doc.from, doc.to) : undefined" sticky class="report-bar no-print">
          <UiPeriodFilter v-if="!active.noPeriod" v-model="period" label="دوره گزارش" />
          <template #actions>
            <button v-if="doc && canExcel" type="button" class="report-file report-file--excel" :disabled="loading || saving !== null" @click="save('xlsx')">
              <i :class="saving === 'xlsx' ? 'ri-loader-4-line report-spin' : 'ri-file-excel-2-line'" aria-hidden="true"></i> Excel
            </button>
            <button v-if="doc && canPdf" type="button" class="report-file report-file--pdf" :disabled="loading || saving !== null" @click="save('pdf')">
              <i :class="saving === 'pdf' ? 'ri-loader-4-line report-spin' : 'ri-file-pdf-2-line'" aria-hidden="true"></i> PDF
            </button>
            <RouterLink :to="{ query: activeGroup ? { group: activeGroup } : {} }" class="report-back">
              <i class="ri-arrow-right-line" aria-hidden="true"></i> گزارش‌ها
            </RouterLink>
          </template>
        </UiFilterBar>

        <div v-if="error" class="alert alert-danger report-error" role="alert">
          <span>{{ error }}</span>
          <button type="button" class="btn btn-soft-danger btn-sm" @click="load">تلاش دوباره</button>
        </div>

        <p class="visually-hidden" role="status">{{ loading ? 'گزارش در حال آمدن است' : '' }}</p>

        <article v-if="doc" class="report-sheet" :class="{ 'report-sheet--busy': loading }" :aria-busy="loading">
          <div class="report-print-head">
            <div v-if="hospital?.hospitalName" class="report-print-head__name">{{ hospital.hospitalName }}</div>
            <div>گرفته‌شده در {{ formatDateTime(loadedAt) }}</div>
          </div>
          <!-- On paper the bar is gone, so the name and days are written once above the figures. -->
          <div class="report-print-title">
            <div class="report-head__title">{{ doc.title }}</div>
            <div class="report-head__period">{{ periodWords(doc.from, doc.to) }}</div>
          </div>

          <div v-if="tiles.length" class="report-tiles">
            <ReportStat
              v-for="tile in tiles"
              :key="tile.label"
              :label="tile.label"
              :value="numberText(tile.value, tile.kind)"
              :kind="tile.kind"
              :negative="tile.kind === 'money' && tile.value < 0"
            />
          </div>

          <ReportTableCard v-if="table" :table="table" />
          <p v-if="doc.description" class="report-about no-print">{{ doc.description }}</p>
        </article>

        <div v-else-if="loading" class="report-sheet" aria-hidden="true">
          <div class="report-tiles">
            <div v-for="n in 4" :key="n" class="report-skeleton-tile"><span class="xr-skeleton xr-skeleton--wide"></span></div>
          </div>
          <div class="report-skeleton-tile report-skeleton-tile--table"><span class="xr-skeleton xr-skeleton--wide"></span></div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* One calm palette for a report page, the same as the gallery it is opened from. */
.report-page {
  --rp-ink: #1f2a44;
  --rp-soft: #6b7487;
  --rp-accent: #1a6fdb;
  --rp-accent-bg: #e9f1fd;
  --rp-line: #e6eaf0;
}

.report-main,
.report-sheet {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 18px;
  align-content: start;
}

/* The shared bar sits in the page's grid, which already spaces it. */
.report-bar {
  margin-bottom: 0;
}

/* Written only on paper: the bar that carries the name on screen is not printed. */
.report-print-title {
  display: none;
}

.report-head__title {
  color: var(--rp-ink);
  font-size: 18px;
  font-weight: 700;
}

.report-head__period {
  color: var(--rp-soft);
  font-size: 12px;
}

/* The two files, each in its own colour so they are told apart at a glance. */
.report-file {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 34px;
  padding: 0 12px;
  border: 1px solid;
  border-radius: 6px;
  background: var(--xr-surface);
  font-size: 13.5px;
  font-weight: 600;
  transition: background-color 0.15s ease;
}

.report-file i {
  font-size: 17px;
}

.report-file--excel {
  border-color: #b9e2cb;
  color: #1d7a46;
}

.report-file--excel:hover:not(:disabled) {
  background: #eaf7f0;
}

.report-file--pdf {
  border-color: #f2c4c0;
  color: #c0392b;
}

.report-file--pdf:hover:not(:disabled) {
  background: #fdeeed;
}

.report-file:disabled {
  opacity: 0.55;
}

.report-spin {
  display: inline-block;
  animation: report-spin 0.8s linear infinite;
}

@keyframes report-spin {
  to {
    transform: rotate(360deg);
  }
}

.report-back {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 34px;
  padding: 0 12px;
  border: 1px solid #d5dce6;
  border-radius: 6px;
  background: var(--xr-surface);
  color: var(--rp-ink);
  font-size: 13.5px;
  font-weight: 600;
  text-decoration: none;
  transition: border-color 0.15s ease, color 0.15s ease;
}

.report-back:hover {
  border-color: var(--rp-accent);
  color: var(--rp-accent);
}

.report-back:focus-visible,
.report-file:focus-visible {
  outline: 2px solid var(--rp-accent);
  outline-offset: 2px;
}

/* What the report shows, in one quiet line under its list. */
.report-about {
  margin: 0;
  color: var(--rp-soft);
  font-size: 12.5px;
}

.report-error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: 0;
}

/* The last answer stays on screen while the next one comes, so nothing jumps. The delay keeps a fast answer from flickering. */
.report-sheet {
  transition: opacity 0.15s ease 0.12s;
}

.report-sheet--busy {
  opacity: 0.5;
}

.report-print-head {
  display: none;
}

/* Four figures in one row, never more. */
.report-tiles {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
}


.report-skeleton-tile {
  display: flex;
  align-items: center;
  height: 84px;
  padding: 14px;
  border-radius: 10px;
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.report-skeleton-tile--table {
  height: 240px;
}


@media (max-width: 1199.98px) {
  .report-tiles {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 575.98px) {
  .report-tiles {
    gap: 10px;
  }
}
</style>

<!-- These reach inside shared components, and the print rules must also reach the page around this one. -->
<style>
/*
 * Cards and tables inside a report, kept calm: a white card with a soft shadow, a bold title with no rule under it,
 * a pale header band on the table, tall rows split by hairlines, no stripes.
 */
.report-sheet .xr-card {
  border-radius: 10px;
  box-shadow: 0 1px 2px rgba(31, 42, 68, 0.05), 0 4px 16px rgba(31, 42, 68, 0.06);
}

.report-sheet .xr-card__header {
  min-height: 0;
  padding: 16px 20px 12px;
  border-bottom: 0;
}

.report-sheet .xr-card__title {
  color: #1f2a44;
  font-size: 14.5px;
  font-weight: 700;
}

.report-sheet .xr-card__body {
  padding: 0 10px 12px;
}

.report-sheet .xr-table {
  color: #2b3445;
}

.report-sheet .xr-table th {
  padding: 12px 14px;
  border: 0;
  background: #eef2f8;
  color: #1f2a44;
  font-size: 13px;
  font-weight: 700;
}

.report-sheet .xr-table td {
  height: 46px;
  padding: 10px 14px;
  border-top: 0;
  border-bottom: 1px solid #edf0f4;
  font-size: 13.5px;
}

.report-sheet .xr-table tbody tr:last-child td {
  border-bottom: 0;
}

.report-sheet .xr-table tbody tr:hover {
  background: #f7f9fc;
}

.report-sheet .xr-table tfoot td {
  border-top: 1px solid #dfe5ee;
  border-bottom: 0;
  background: #f7f9fc;
  color: #1f2a44;
}

.report-sheet .xr-section > summary {
  color: #1a6fdb;
  font-size: 13px;
}

/* A named page, so that the slip and form sizes other print pages set never apply to a report. */
@page report-a4 {
  size: A4 portrait;
  margin: 12mm;
}

@media print {
  .layout__content:has(.report-page) > .alert {
    display: none !important;
  }

  .layout__content:has(.report-page) {
    margin: 0;
  }

  .layout__main:has(.report-page) {
    min-height: 0;
    padding: 0;
    border-radius: 0;
  }

  .layout:has(.report-page) {
    min-height: 0;
  }

  .report-page,
  .report-page .report-layout,
  .report-page .report-main,
  .report-page .report-grid {
    display: block;
  }

  .report-page .report-sheet {
    display: block;
    page: report-a4;
    opacity: 1 !important;
    color: #000;
    font-size: 11px;
  }

  .report-page .report-sheet > * + *,
  .report-page .report-grid > * + * {
    margin-top: 14px;
  }

  .report-page .report-print-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
    padding-bottom: 6px;
    border-bottom: 1px solid #000;
    color: #000;
    font-size: 10px;
  }

  .report-page .report-print-head__name {
    font-size: 15px;
    font-weight: 700;
  }

  .report-page .report-print-title {
    display: block;
  }

  .report-page .report-head__title {
    color: #000;
    font-size: 18px;
  }

  .report-page .report-head__period {
    color: #000;
    font-size: 11px;
  }

  .report-tiles {
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)) !important;
    gap: 8px !important;
  }

  .report-page .report-tiles .rs {
    height: auto;
    padding: 6px 10px;
    border: 1px solid var(--xr-border-table);
    border-radius: 6px;
    box-shadow: none;
    break-inside: avoid;
  }

  .report-page .report-tiles .rs__picture {
    display: none;
  }

  .report-page .report-tiles .rs__value {
    color: #000;
    font-size: 15px;
  }

  .report-page .report-tiles .rs__label,
  .report-page .report-tiles .rs__note {
    color: #000;
    font-size: 10px;
  }

  .report-sheet .xr-card {
    border-radius: 0;
    box-shadow: none;
  }

  .report-sheet .xr-card__header {
    min-height: 0;
    padding: 0 0 4px;
    border-bottom: 1px solid #000;
    break-after: avoid;
  }

  .report-sheet .xr-card__title {
    color: #000;
    font-size: 13px;
    font-weight: 600;
  }

  .report-sheet .xr-card__body {
    padding: 4px 0 0;
  }

  .report-sheet .chart {
    break-inside: avoid;
  }

  /* Paper has no scrolling: a table shows in full, and its titles repeat on each page. */
  .report-sheet .xr-table-wrap {
    max-height: none !important;
    overflow: visible !important;
  }

  .report-sheet .xr-table {
    color: #000;
    font-size: 10px;
  }

  .report-sheet .xr-table thead {
    display: table-header-group;
  }

  /* A total belongs once at the end, not at the foot of every page. */
  .report-sheet .xr-table tfoot {
    display: table-row-group;
  }

  .report-sheet .xr-table tr {
    break-inside: avoid;
  }

  .report-sheet .xr-table th,
  .report-sheet .xr-table td {
    position: static !important;
    padding: 3px 4px;
    color: #000;
    font-size: 10px;
    white-space: normal;
  }

  .report-sheet .xr-table th {
    background: none;
  }

  .report-sheet .xr-table td {
    height: auto;
    border-top: 1px solid var(--xr-border-table);
    border-bottom: 0;
  }

  .report-sheet .xr-table tfoot td {
    background: none;
  }

  .report-page .report-sheet .report-cell__text {
    width: auto;
    max-width: none;
  }

  .report-sheet .xr-table tfoot td {
    border-top: 1px solid #000;
  }

  .report-page .report-sheet .report-table__note {
    color: #000;
    font-size: 9.5px;
  }
}
</style>
