<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import { useHospital } from '@/features/print/paper'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, todayIso } from '@/utils/date'
import { downloadCsv } from './exportCsv'
import { periodChoices, quickRange, reportGroups, type DateRange, type PeriodKind } from './reportCatalog'
import { numberText, periodWords } from './reportFormat'
import ReportTableCard from './ReportTableCard.vue'
import ReportTrendChart from './ReportTrendChart.vue'
import type { ReportDocument, ReportTile } from './types'

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
const active = computed(() => allowed.value.find((report) => report.key === route.query.report) ?? allowed.value[0] ?? null)

const isoDate = /^\d{4}-\d{2}-\d{2}$/
const today = computed(() => todayIso())

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

const customOpen = ref(selected.value.kind === 'custom')
const draft = reactive<{ from: string | null; to: string | null }>({ from: selected.value.from, to: selected.value.to })
const shownChoice = computed<PeriodKind>(() => (customOpen.value ? 'custom' : selected.value.kind))

function choosePeriod(kind: PeriodKind) {
  if (kind === 'custom') {
    customOpen.value = true
    Object.assign(draft, { from: selected.value.from, to: selected.value.to })
    return
  }
  customOpen.value = false
  router.push({ query: queryFor(active.value!.key, kind) })
}

function showCustom() {
  if (!draft.from || !draft.to) return
  router.push({ query: queryFor(active.value!.key, 'custom', { from: draft.from, to: draft.to }) })
}

const doc = ref<ReportDocument | null>(null)
const loading = ref(false)
const error = ref('')
const loadedAt = ref<string | null>(null)

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
    const params = report.noPeriod ? '' : `?${new URLSearchParams({ from: selected.value.from, to: selected.value.to })}`
    doc.value = await api<ReportDocument>(`/reports/${report.key}${params}`, { signal })
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

// Back and forward can land on any period, so the panel follows the address.
watch(
  () => selected.value.kind,
  (kind) => {
    customOpen.value = kind === 'custom'
    if (kind === 'custom') Object.assign(draft, { from: selected.value.from, to: selected.value.to })
  },
)

// An unknown or forbidden choice in the address is replaced, so the address always says what is on screen.
watch(
  () => route.query,
  (query) => {
    if (route.name !== 'reports' || !active.value || (query.report === undefined && query.period === undefined)) return
    const wanted = queryFor(active.value.key, selected.value.kind, selected.value) as Record<string, string>
    const same = Object.keys({ ...wanted, ...query }).every((key) => query[key] === wanted[key])
    if (!same) router.replace({ query: wanted })
  },
  { immediate: true },
)

const icons: Partial<Record<ReportTile['kind'], string>> = {
  count: 'ri-bar-chart-fill',
  money: 'ri-wallet-3-fill',
  quantity: 'ri-stack-fill',
}

const printReport = () => window.print()
const exportReport = () => doc.value && active.value && downloadCsv(active.value.key, doc.value)

onMounted(loadHospital)
onBeforeUnmount(() => controller?.abort())
</script>

<template>
  <div class="report-page">
    <UiCard v-if="!active">
      <p class="xr-state mb-0">هنوز گزارشی برای نقش شما باز نشده است. اگر به گزارشی نیاز دارید، به مدیر سیستم بگویید.</p>
    </UiCard>

    <div v-else class="report-layout">
      <nav class="report-nav xr-card no-print d-none d-xl-block" aria-label="فهرست گزارش‌ها">
        <section v-for="group in groups" :key="group.title" class="report-nav__group">
          <h2 class="report-nav__title">{{ group.title }}</h2>
          <ul class="report-nav__list">
            <li v-for="report in group.reports" :key="report.key">
              <button
                type="button"
                class="report-nav__link"
                :class="{ 'report-nav__link--active': report.key === active.key }"
                :aria-current="report.key === active.key ? 'true' : undefined"
                @click="chooseReport(report.key)"
              >{{ report.name }}</button>
            </li>
          </ul>
        </section>
      </nav>

      <div class="report-main">
        <div class="report-controls xr-card no-print" :class="{ 'd-xl-none': active.noPeriod }">
          <div class="d-xl-none">
            <label class="form-label" for="report-choice">گزارش</label>
            <select id="report-choice" class="form-select" :value="active.key" @change="chooseReport(($event.target as HTMLSelectElement).value)">
              <optgroup v-for="group in groups" :key="group.title" :label="group.title">
                <option v-for="report in group.reports" :key="report.key" :value="report.key">{{ report.name }}</option>
              </optgroup>
            </select>
          </div>

          <template v-if="!active.noPeriod">
            <div class="report-periods" role="group" aria-label="دوره گزارش">
              <button
                v-for="choice in periodChoices"
                :key="choice.kind"
                type="button"
                class="report-period"
                :class="{ 'report-period--active': shownChoice === choice.kind }"
                :aria-pressed="shownChoice === choice.kind"
                @click="choosePeriod(choice.kind)"
              >{{ choice.label }}</button>
            </div>

            <form v-if="customOpen" class="report-custom" @submit.prevent="showCustom">
              <ShamsiDateInput v-model="draft.from" label="از تاریخ" required :max="draft.to ?? today" />
              <ShamsiDateInput v-model="draft.to" label="تا تاریخ" required :min="draft.from ?? undefined" :max="today" />
              <button type="submit" class="btn btn-primary xr-field-height">نمایش</button>
            </form>
          </template>
        </div>

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

          <header class="report-head">
            <div class="report-head__text">
              <h1 class="report-head__title">{{ doc.title }}</h1>
              <p class="report-head__description">{{ doc.description }}</p>
              <p class="report-head__period">
                <i class="ri-calendar-2-line no-print" aria-hidden="true"></i>
                {{ periodWords(doc.from, doc.to) }}
              </p>
            </div>
            <div v-if="auth.can('reports.print') || auth.can('reports.export')" class="report-head__actions no-print">
              <button v-if="auth.can('reports.print')" type="button" class="btn btn-soft" :disabled="loading" @click="printReport">
                <i class="ri-printer-line" aria-hidden="true"></i> چاپ
              </button>
              <button v-if="auth.can('reports.export')" type="button" class="btn btn-soft" :disabled="loading" @click="exportReport">
                <i class="ri-file-excel-2-line" aria-hidden="true"></i> خروجی اکسل
              </button>
            </div>
          </header>

          <div v-if="doc.tiles.length" class="report-tiles" :class="{ 'report-tiles--pairs': doc.tiles.length % 2 === 0 && doc.tiles.length % 3 !== 0 }">
            <StatCard
              v-for="tile in doc.tiles"
              :key="tile.label"
              :label="tile.label"
              :value="numberText(tile.value, tile.kind)"
              :icon="icons[tile.kind] ?? icons.count!"
              :tone="tile.kind === 'money' && tile.value < 0 ? 'red' : 'primary'"
            />
          </div>

          <ReportTrendChart v-if="doc.trend" :trend="doc.trend" />

          <ReportTableCard v-for="table in doc.tables" :key="table.title" :table="table" />
        </article>

        <div v-else-if="loading" class="report-sheet" aria-hidden="true">
          <div class="report-head__text">
            <span class="xr-skeleton xr-skeleton--title"></span>
            <span class="xr-skeleton xr-skeleton--wide mt-3"></span>
          </div>
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
.report-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--xr-gap-x);
}

.report-main,
.report-sheet {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--xr-gap-x);
  align-content: start;
}

@media (min-width: 1200px) {
  .report-layout {
    grid-template-columns: 260px minmax(0, 1fr);
    align-items: start;
  }

  /* Clears the fixed page header, and scrolls by itself on a short screen. */
  .report-nav {
    position: sticky;
    top: 95px;
    max-height: calc(100vh - 115px);
    overflow-y: auto;
  }
}

.report-nav {
  padding: 16px 12px;
}

.report-nav__group + .report-nav__group {
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid var(--xr-border);
}

.report-nav__title {
  margin: 0 0 8px;
  padding-inline: 12px;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  font-weight: 500;
}

.report-nav__list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.report-nav__link {
  display: block;
  width: 100%;
  padding: 8px 12px;
  border: 0;
  border-inline-start: 3px solid transparent;
  border-radius: var(--xr-radius-button);
  background: transparent;
  color: var(--xr-text);
  font-size: var(--xr-fs-body);
  line-height: 1.5;
  text-align: start;
  transition: background-color 0.12s ease;
}

.report-nav__link:hover {
  background: var(--xr-row-hover);
}

.report-nav__link--active,
.report-nav__link--active:hover {
  border-inline-start-color: var(--xr-primary);
  background: var(--xr-tone-primary-bg);
  color: var(--xr-tone-primary-text);
  font-weight: 600;
}

.report-nav__link:focus-visible,
.report-period:focus-visible {
  outline: 2px solid var(--xr-primary);
  outline-offset: 1px;
}

.report-controls {
  display: grid;
  gap: 12px;
  padding: 12px 16px;
}

.report-periods {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.report-period {
  min-height: 36px;
  padding: 4px 14px;
  border: 0;
  border-radius: var(--xr-radius-button);
  background: transparent;
  color: var(--xr-text);
  font-size: var(--xr-fs-body);
  transition: background-color 0.12s ease;
}

.report-period:hover {
  background: var(--xr-row-hover);
}

.report-period--active,
.report-period--active:hover {
  background: var(--xr-tone-primary-bg);
  color: var(--xr-tone-primary-text);
  font-weight: 600;
}

.report-custom {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
  align-items: end;
  gap: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--xr-border);
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

.report-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px 24px;
}

.report-head__text {
  flex: 1 1 280px;
  min-width: 0;
}

.report-head__title {
  font-size: 24px;
  line-height: 1.4;
}

.report-head__description {
  margin: 4px 0 0;
  color: var(--xr-text);
  font-size: 14.5px;
}

.report-head__period {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 6px 0 0;
  color: var(--xr-tone-primary-text);
  font-weight: 500;
}

.report-head__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.report-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  gap: var(--xr-gap-x);
}

/* Two or four tiles sit in even rows rather than leaving one alone on a row of its own. */
.report-tiles--pairs {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

@media (min-width: 1600px) {
  .report-tiles--pairs {
    grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  }
}

.report-skeleton-tile {
  display: flex;
  align-items: center;
  min-height: 113px;
  padding: 20px;
  border-radius: var(--xr-radius-card);
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.report-skeleton-tile--table {
  min-height: 240px;
}

@media (max-width: 575.98px) {
  .report-tiles {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px;
  }

  .report-skeleton-tile {
    min-height: 88px;
  }

  .report-head__actions {
    width: 100%;
  }

  .report-head__actions .btn {
    flex: 1;
  }

  .report-period {
    flex: 1 1 calc(33.333% - 4px);
    padding-inline: 6px;
  }
}
</style>

<!-- These reach inside shared components, and the print rules must also reach the page around this one. -->
<style>
.report-tiles .stat .stat__tile {
  min-height: 96px;
  padding: 16px;
}

.report-tiles .stat .stat__icon {
  width: 46px;
  height: 46px;
  font-size: 18px;
}

/* A number is never broken across lines: only the space before its unit may wrap. */
.report-tiles .stat .stat__value {
  font-size: 24px;
  font-weight: 500;
  line-height: 1.35;
  overflow-wrap: normal;
}

.report-tiles .stat .stat__label {
  color: var(--xr-text-soft);
  font-size: 14px;
  line-height: 1.5;
}

@media (max-width: 575.98px) {
  .report-tiles .stat .stat__tile {
    min-height: 88px;
    padding: 12px;
  }

  .report-tiles .stat .stat__icon {
    display: none;
  }

  .report-tiles .stat .stat__text {
    flex: 1;
    text-align: start;
  }

  .report-tiles .stat .stat__value {
    font-size: 20px;
  }

  .report-tiles .stat .stat__label {
    font-size: 13px;
  }
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
  .report-page .report-main {
    display: block;
  }

  .report-page .report-sheet {
    display: block;
    page: report-a4;
    opacity: 1 !important;
    color: #000;
    font-size: 11px;
  }

  .report-page .report-sheet > * + * {
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

  .report-page .report-head__title {
    color: #000;
    font-size: 18px;
  }

  .report-page .report-head__description,
  .report-page .report-head__period {
    color: #000;
    font-size: 11px;
  }

  .report-tiles {
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)) !important;
    gap: 8px !important;
  }

  .report-tiles .stat {
    border: 1px solid var(--xr-border-table);
    border-radius: 6px;
    box-shadow: none;
    break-inside: avoid;
  }

  .report-tiles .stat .stat__tile {
    min-height: 0;
    padding: 6px 10px;
    border-radius: 0;
    background: none;
  }

  .report-tiles .stat .stat__icon {
    display: none;
  }

  .report-tiles .stat .stat__text {
    flex: 1;
    text-align: start;
  }

  .report-tiles .stat .stat__value {
    color: #000;
    font-size: 15px;
  }

  .report-tiles .stat .stat__label {
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

  .report-sheet .xr-table td {
    border-top: 1px solid var(--xr-border-table);
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
