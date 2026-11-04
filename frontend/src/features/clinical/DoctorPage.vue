<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { feeStatusLabels, visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { formatDate, formatDateTime } from '@/utils/date'
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

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'DiagnosticResultReady'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div class="doctor-stats">
      <StatCard label="مریضان در صف" :value="rows.length" icon="ri-group-fill" />
      <StatCard label="در حال معاینه" :value="inConsultation" icon="ri-eye-fill" tone="cyan" />
      <StatCard label="نتایج آماده" :value="results.length" icon="ri-test-tube-fill" tone="orange" />
      <StatCard label="مراجعات بعدی" :value="followUps.length" icon="ri-calendar-event-fill" tone="red" />
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <UiCard title="مریضان در صف داکتر">
      <CallNotice :desks="[calls]" />
      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="rows.length === 0" class="xr-state">مریضی در صف نیست.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>نوبت</th><th>مریض</th><th>علت</th><th>وضعیت</th><th>فراخوان</th><th>عملیات</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in rows" :key="row.id">
              <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
              <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
              <td>{{ row.reason ?? '—' }}</td>
              <td>
                <div class="xr-stack">
                  <span class="xr-badge" :class="`xr-badge--${visitStatusTones[row.status]}`">{{ visitStatusLabels[row.status] }}</span>
                  <span v-if="feeStatusLabels[row.feeStatus]" class="xr-note" :class="`xr-note--${feeStatusLabels[row.feeStatus].tone}`">{{ feeStatusLabels[row.feeStatus].label }}</span>
                </div>
              </td>
              <td><CallControls :desk="calls" :visit-id="row.id" /></td>
              <td class="text-nowrap"><RouterLink :to="`/chart/${row.id}`" class="btn btn-soft btn-sm">باز کردن پرونده</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard v-if="results.length" title="نتایج آماده آزمایش‌های شما">
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>مریض</th><th>آزمایش</th><th>زمان نتیجه</th><th></th></tr></thead>
          <tbody>
            <tr v-for="row in results" :key="row.id">
              <td>{{ row.patientName }}</td>
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
              <td>{{ formatDate(row.dueDate) }}</td>
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
.doctor-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--xr-gap);
}
</style>
