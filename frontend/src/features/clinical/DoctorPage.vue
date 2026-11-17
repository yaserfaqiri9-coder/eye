<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { feeStatusLabels, visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { formatDate, formatDateTime, todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'
import type { DiagnosticOrder } from './types'

interface FollowUpRow {
  id: number
  patientId: number
  patientName: string
  dueDate: string
  note: string | null
}

const calls = useCallDesk('Doctor')
const rows = ref<VisitRow[]>([])
const results = ref<DiagnosticOrder[]>([])
const followUps = ref<FollowUpRow[]>([])
const loading = ref(true)
const error = ref('')

const inConsultation = computed(() => rows.value.filter((r) => r.status === 'InConsultation').length)

/** Visits whose ordered result is back: the patient returns for the doctor to read it, not for a new exam. */
const resultVisits = computed(() => new Set(results.value.map((r) => r.visitId)))

const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | undefined
onMounted(() => (clock = setInterval(() => (now.value = Date.now()), 60_000)))
onBeforeUnmount(() => clearInterval(clock))

/** How long the patient has waited since the visit was opened, in minutes or hours. */
function waited(createdAt: string) {
  const minutes = Math.max(0, Math.floor((now.value - new Date(createdAt).getTime()) / 60_000))
  if (minutes < 60) return `${quantity(minutes)} دقیقه`
  return `${quantity(Math.floor(minutes / 60))} ساعت ${minutes % 60 ? `و ${quantity(minutes % 60)} دقیقه` : ''}`.trim()
}

const ageText = (row: VisitRow) => (row.patientAgeYears != null ? `${quantity(row.patientAgeYears)} ساله` : '')

async function load() {
  try {
    ;[rows.value, results.value, followUps.value] = await Promise.all([
      api<VisitRow[]>('/clinical/worklist?kind=doctor'),
      api<{ items: DiagnosticOrder[] }>('/diagnostic-orders?status=ResultReady&mine=true&pageSize=20').then((r) => r.items),
      api<FollowUpRow[]>('/follow-ups'),
    ])
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

/** Patients this doctor has already finished, by day, so their records can be opened again. */
const tab = ref<'queue' | 'done'>('queue')
const seenDate = ref<string | null>(todayIso())
const seen = ref<VisitRow[]>([])
const seenLoading = ref(false)

async function loadSeen() {
  if (tab.value !== 'done') return
  seenLoading.value = true
  try {
    seen.value = await api<VisitRow[]>(`/clinical/worklist?kind=doctor&done=true&date=${seenDate.value ?? todayIso()}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    seenLoading.value = false
  }
}

watch([tab, seenDate], loadSeen)
useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'DiagnosticResultReady'], () => Promise.all([load(), loadSeen()]))
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <!-- The day's four numbers on one slim line, so the queue itself starts at the top of the screen. -->
    <div class="xr-figures">
      <span class="xr-figure"><strong>{{ quantity(rows.length) }}</strong> مریضان در صف</span>
      <span class="xr-figure"><strong>{{ quantity(inConsultation) }}</strong> در حال معاینه</span>
      <span class="xr-figure xr-figure--orange"><strong>{{ quantity(results.length) }}</strong> نتایج آماده</span>
      <span class="xr-figure"><strong>{{ quantity(followUps.length) }}</strong> مراجعات بعدی</span>
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <UiCard :title="tab === 'queue' ? 'مریضان در صف داکتر' : 'مریضان دیده‌شده'">
      <template #actions>
        <nav class="xr-tabs" role="tablist" aria-label="بخش‌های صف داکتر">
          <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'queue' }" :aria-selected="tab === 'queue'" @click="tab = 'queue'">صف</button>
          <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'done' }" :aria-selected="tab === 'done'" @click="tab = 'done'">مریضان دیده‌شده</button>
        </nav>
      </template>
      <template v-if="tab === 'done'">
        <div class="seen-date"><ShamsiDateInput v-model="seenDate" label="تاریخ" :max="todayIso()" /></div>
        <div v-if="seenLoading" class="xr-state" role="status">در حال بارگذاری...</div>
        <div v-else-if="seen.length === 0" class="xr-state">در این روز مریضی دیده نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr><th>نوبت</th><th>مریض</th><th>علت</th><th>وضعیت</th><th>عملیات</th></tr>
            </thead>
            <tbody>
              <tr v-for="row in seen" :key="row.id" class="xr-row-link">
                <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
                <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" :note="ageText(row)" /></td>
                <td>{{ row.reason }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[row.status]}`">{{ visitStatusLabels[row.status] }}</span></td>
                <td class="text-nowrap"><RouterLink :to="`/chart/${row.id}`" class="btn btn-soft btn-sm xr-row-link__target">باز کردن پرونده</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
      <template v-else>
      <CallNotice :desks="[calls]" />
      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="rows.length === 0" class="xr-state">مریضی در صف نیست.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>نوبت</th><th>مریض</th><th>علت</th><th>انتظار</th><th>وضعیت</th><th>فراخوان</th><th>عملیات</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in rows" :key="row.id" class="xr-row-link">
              <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
              <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" :note="ageText(row)" /></td>
              <td>{{ row.reason }}</td>
              <td class="text-nowrap">{{ waited(row.createdAt) }}</td>
              <td>
                <!-- Only what needs the doctor's attention: a ready result, missing vision, or a fee that is not settled. -->
                <div class="xr-stack">
                  <span class="xr-badge" :class="`xr-badge--${visitStatusTones[row.status]}`">{{ visitStatusLabels[row.status] }}</span>
                  <span v-if="resultVisits.has(row.id)" class="xr-note xr-note--orange">نتیجه آزمایش آماده است</span>
                  <span v-else-if="!row.visionComplete" class="xr-note xr-note--orange">بینایی‌سنجی انجام نشده</span>
                  <span v-if="row.feeStatus !== 'Paid' && feeStatusLabels[row.feeStatus]" class="xr-note" :class="`xr-note--${feeStatusLabels[row.feeStatus].tone}`">{{ feeStatusLabels[row.feeStatus].label }}</span>
                </div>
              </td>
              <td><CallControls :desk="calls" :visit-id="row.id" /></td>
              <td class="text-nowrap"><RouterLink :to="`/chart/${row.id}`" class="btn btn-soft btn-sm xr-row-link__target">باز کردن پرونده</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
      </template>
    </UiCard>

    <UiCard v-if="results.length" title="نتایج آماده آزمایش‌های شما">
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>مریض</th><th>آزمایش</th><th>زمان نتیجه</th><th></th></tr></thead>
          <tbody>
            <tr v-for="row in results" :key="row.id">
              <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
              <td>{{ row.testName }}</td>
              <td>{{ formatDateTime(row.performedAt) }}</td>
              <td><RouterLink v-if="row.visitId" :to="`/chart/${row.visitId}`" class="btn btn-soft btn-sm">باز کردن پرونده</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard v-if="followUps.length" title="مراجعات بعدی">
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>تاریخ</th><th>مریض</th><th>یادداشت</th></tr></thead>
          <tbody>
            <tr v-for="row in followUps" :key="row.id">
              <td class="text-nowrap">
                {{ formatDate(row.dueDate) }}
                <span v-if="row.dueDate < todayIso()" class="xr-badge xr-badge--red ms-1">گذشته</span>
                <span v-else-if="row.dueDate === todayIso()" class="xr-badge xr-badge--orange ms-1">امروز</span>
              </td>
              <td><RouterLink :to="`/patients/${row.patientId}`">{{ row.patientName }}</RouterLink></td>
              <td>{{ row.note ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>
</template>

<style scoped>
.seen-date {
  max-width: 220px;
  margin-bottom: var(--xr-gap);
}
</style>
