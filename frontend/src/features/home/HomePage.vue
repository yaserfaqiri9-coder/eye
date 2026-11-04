<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import StatCard from '@/components/StatCard.vue'
import UiAvatar from '@/components/UiAvatar.vue'
import UiCard from '@/components/UiCard.vue'
import type { DashboardReport, FinancialReport } from '@/features/reports/types'
import { visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import ActivityChart from './ActivityChart.vue'
import ClinicIllustration from './ClinicIllustration.vue'
import DistributionChart from './DistributionChart.vue'
import PatientsInChart from './PatientsInChart.vue'

const auth = useAuthStore()
const dashboard = ref<DashboardReport | null>(null)
const financial = ref<FinancialReport | null>(null)
const visits = ref<VisitRow[]>([])
const error = ref('')
const queueTab = ref('waiting')

async function load() {
  try {
    const [nextDashboard, nextFinancial, nextVisits] = await Promise.all([
      auth.can('reports.view') ? api<DashboardReport>('/reports/dashboard') : null,
      auth.can('ledger.view') ? api<FinancialReport>('/reports/financial') : null,
      auth.can('visits.view') ? api<{ items: VisitRow[] }>('/visits?pageSize=100').then((r) => r.items) : [],
    ])
    dashboard.value = nextDashboard
    financial.value = nextFinancial
    visits.value = nextVisits
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PaymentReceived'], load)
onMounted(load)

const statusGroups = [
  { label: 'در انتظار', statuses: ['Registered', 'Waiting'], color: 'var(--xr-status-orange)' },
  { label: 'در معاینه', statuses: ['InPreExam', 'ReadyForDoctor', 'InConsultation'], color: 'var(--xr-status-primary)' },
  { label: 'منتظر آزمایش یا پیگیری', statuses: ['AwaitingDiagnostic', 'FollowUpRequired'], color: 'var(--xr-status-cyan)' },
  { label: 'تکمیل‌شده', statuses: ['Completed'], color: 'var(--xr-status-green)' },
  { label: 'لغو شده', statuses: ['Cancelled'], color: 'var(--xr-status-red)' },
]

const todayTotal = computed(() => dashboard.value?.todayByStatus.reduce((sum, row) => sum + row.count, 0) ?? 0)

const statusRows = computed(() =>
  statusGroups.map((group) => {
    const count = dashboard.value?.todayByStatus.filter((row) => group.statuses.includes(row.name)).reduce((sum, row) => sum + row.count, 0) ?? 0
    return { ...group, count, percent: todayTotal.value ? Math.round((count / todayTotal.value) * 100) : 0 }
  }),
)

/** Change against yesterday; hidden when yesterday had no visits because a percentage of zero means nothing. */
const trend = computed(() => {
  const yesterday = dashboard.value?.yesterdayVisits ?? 0
  if (!yesterday) return null
  return Math.round(((todayTotal.value - yesterday) / yesterday) * 1000) / 10
})

const queueTabs = [
  { key: 'waiting', label: 'انتظار', statuses: ['Registered', 'Waiting'] },
  { key: 'exam', label: 'معاینه', statuses: ['InPreExam', 'ReadyForDoctor', 'InConsultation'] },
  { key: 'pending', label: 'پیگیری', statuses: ['AwaitingDiagnostic', 'FollowUpRequired'] },
]

const queueRows = computed(() => {
  const statuses = queueTabs.find((t) => t.key === queueTab.value)?.statuses ?? []
  return visits.value.filter((v) => statuses.includes(v.status)).slice(0, 6)
})

const timeline = computed(() => [...visits.value].sort((a, b) => b.createdAt.localeCompare(a.createdAt)).slice(0, 4))
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>

  <div class="dash">
    <UiCard v-if="dashboard" title="آمار مراجعات" class="dash__activity">
      <ActivityChart :days="dashboard.days" />
    </UiCard>

    <section class="xr-card dash__welcome"><ClinicIllustration /></section>

    <UiCard v-if="dashboard" title="توزیع مراجعات بخش‌ها" class="dash__half">
      <DistributionChart :rows="dashboard.visitsByDepartment" />
    </UiCard>

    <UiCard v-if="dashboard" title="مریضان ورودی" class="dash__half">
      <PatientsInChart :days="dashboard.days" />
    </UiCard>

    <UiCard v-if="dashboard" title="وضعیت مراجعات امروز" class="dash__third">
      <div class="status-total">
        <span class="status-total__value">{{ quantity(todayTotal) }}</span>
        <span v-if="trend !== null" class="status-total__trend" :class="trend < 0 ? 'text-danger' : 'text-success'">
          <i :class="trend < 0 ? 'ri-arrow-down-line' : 'ri-arrow-up-line'" aria-hidden="true"></i>
          <span class="ltr-num">{{ trend > 0 ? '+' : '' }}{{ quantity(trend) }}٪</span>
        </span>
        <span class="status-total__note">{{ trend !== null ? 'نسبت به دیروز' : 'مراجعه امروز' }}</span>
      </div>
      <div class="status-bar" aria-hidden="true">
        <template v-for="row in statusRows" :key="row.label">
          <span v-if="row.count" :style="{ flexGrow: row.count, background: row.color }"></span>
        </template>
      </div>
      <ul class="status-rows">
        <li v-for="row in statusRows" :key="row.label">
          <span class="status-rows__dot" :style="{ background: row.color }"></span>
          <span class="status-rows__label">{{ row.label }}</span>
          <span class="status-rows__count">{{ quantity(row.count) }}</span>
          <span class="status-rows__percent">{{ quantity(row.percent) }}٪</span>
        </li>
      </ul>
    </UiCard>

    <UiCard v-if="auth.can('visits.view')" title="صف مریضان" class="dash__third">
      <template #actions>
        <div class="xr-tabs" role="tablist">
          <button
            v-for="t in queueTabs"
            :key="t.key"
            type="button"
            role="tab"
            class="xr-tab"
            :class="{ 'xr-tab--active': queueTab === t.key }"
            :aria-selected="queueTab === t.key"
            @click="queueTab = t.key"
          >{{ t.label }}</button>
        </div>
      </template>
      <p v-if="queueRows.length === 0" class="text-muted mb-0">مریضی در این بخش از صف نیست.</p>
      <ul v-else class="queue">
        <li v-for="row in queueRows" :key="row.id">
          <UiAvatar :seed="row.patientFileNumber" :gender="row.patientGender" :size="36" />
          <RouterLink :to="`/patients/${row.patientId}`" class="queue__name">{{ row.patientName }}</RouterLink>
          <span class="queue__number" :title="`نوبت ${row.queueNumber}`">{{ quantity(row.queueNumber) }}</span>
        </li>
      </ul>
    </UiCard>

    <UiCard v-if="auth.can('visits.view')" title="جریان مراجعات" class="dash__third">
      <template #actions>
        <RouterLink to="/reception" class="dash__all">مشاهده همه</RouterLink>
      </template>
      <p v-if="timeline.length === 0" class="text-muted mb-0">امروز مراجعه‌ای ثبت نشده است.</p>
      <ol v-else class="timeline">
        <li v-for="row in timeline" :key="row.id" class="timeline__item" :class="`timeline__item--${visitStatusTones[row.status]}`">
          <div class="timeline__head">
            <span class="timeline__title">{{ row.patientName }}</span>
            <span class="timeline__time">{{ formatTime(row.createdAt) }}</span>
          </div>
          <p class="timeline__text">{{ row.departmentName }} — {{ visitStatusLabels[row.status] }}</p>
        </li>
      </ol>
    </UiCard>
  </div>

  <!-- Below the fold of the reference layout: today's money and stock figures for the roles that may see them. -->
  <div v-if="dashboard" class="home-stats">
    <StatCard label="اجناس کم‌موجود" :value="dashboard.lowStockProducts" icon="ri-archive-fill" tone="orange" />
    <StatCard v-if="financial" label="دریافت امروز" :value="money(financial.receipts)" icon="ri-money-dollar-circle-fill" />
    <StatCard v-if="financial" label="مصارف امروز" :value="money(financial.expenses)" icon="ri-wallet-3-fill" tone="red" />
    <StatCard v-if="financial" label="باقی حساب‌ها" :value="money(financial.outstandingNow)" icon="ri-receipt-fill" tone="orange" />
  </div>
</template>

<style scoped>
.dash {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: var(--xr-gap);
}

.dash__activity { grid-column: span 4; min-height: 465px; }
.dash__welcome { grid-column: span 2; min-height: 465px; overflow: hidden; }
.dash__half { grid-column: span 3; min-height: 515px; }
.dash__third { grid-column: span 2; min-height: 440px; }

/* Every dashboard card header is 60px, including the one holding the queue tabs. */
.dash :deep(.xr-card__header) {
  min-height: 60px;
  padding-block: 0;
}

.dash__all {
  color: var(--xr-primary);
  font-size: 14px;
  text-decoration: none;
}

.status-total {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 12px;
}

.status-total__value {
  font-size: 32px;
  font-weight: 300;
  line-height: 1.2;
}

.status-total__trend {
  font-size: 13px;
}

.status-total__note {
  color: var(--xr-text-muted);
  font-size: 13px;
}

.status-bar {
  display: flex;
  height: 15px;
  margin: 18px 0 24px;
  overflow: hidden;
  border-radius: 8px;
  background: var(--xr-border);
}

.status-rows,
.queue,
.timeline {
  margin: 0;
  padding: 0;
  list-style: none;
}

.status-rows li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding-block: 11px;
  font-size: 15px;
}

.status-rows__dot {
  flex: none;
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.status-rows__label {
  flex: 1;
  min-width: 0;
}

.status-rows__count,
.status-rows__percent {
  flex: none;
  width: 44px;
  color: var(--xr-text-muted);
  font-size: 13px;
}

.queue li {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 56px;
  border-bottom: 1px solid var(--xr-border);
}

.queue li:last-child {
  border-bottom: 0;
}

.queue__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--xr-text);
  font-size: 14px;
  text-decoration: none;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queue__name:hover {
  color: var(--xr-primary);
}

.queue__number {
  flex: none;
  color: var(--xr-text-muted);
  font-size: 13px;
}

.timeline__item {
  --tone: var(--xr-status-primary);
  position: relative;
  padding-inline-start: 28px;
  padding-bottom: 26px;
}

.timeline__item--orange { --tone: var(--xr-status-orange); }
.timeline__item--red { --tone: var(--xr-status-red); }
.timeline__item--cyan { --tone: var(--xr-status-cyan); }
.timeline__item--green { --tone: var(--xr-status-green); }

/* The connecting line, then the ring on top of it. */
.timeline__item::before {
  content: '';
  position: absolute;
  inset-inline-start: 7px;
  inset-block: 8px -8px;
  width: 1px;
  background: var(--xr-border);
}

.timeline__item:last-child {
  padding-bottom: 0;
}

.timeline__item:last-child::before {
  display: none;
}

.timeline__item::after {
  content: '';
  position: absolute;
  inset-inline-start: 0;
  top: 4px;
  width: 15px;
  height: 15px;
  border: 3px solid var(--tone);
  border-radius: 50%;
  background: var(--xr-surface);
}

.timeline__head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
}

.timeline__title {
  min-width: 0;
  overflow: hidden;
  font-size: 15px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.timeline__time {
  flex: none;
  color: var(--xr-text-muted);
  font-size: 12px;
}

.timeline__text {
  margin: 6px 0 0;
  color: var(--xr-text-muted);
  font-size: 13px;
}

.home-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: var(--xr-gap);
  margin-top: var(--xr-gap-y);
}

/* Between phone and the 1600px reference the three-up row wraps before the charts get cramped. */
@media (max-width: 1399.98px) {
  .dash__third { grid-column: span 3; }
  .dash__third:last-child { grid-column: span 6; min-height: 0; }
}

@media (max-width: 1199.98px) {
  .dash__activity,
  .dash__welcome,
  .dash__half,
  .dash__third,
  .dash__third:last-child {
    grid-column: span 6;
    min-height: 0;
  }

  .dash__welcome {
    max-width: 420px;
    width: 100%;
    justify-self: center;
  }
}
</style>
