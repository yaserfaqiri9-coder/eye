<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { feeStatusLabels, visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { quantity } from '@/utils/money'

const calls = useCallDesk('Vision')
const rows = ref<VisitRow[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    rows.value = await api<VisitRow[]>('/clinical/worklist?kind=triage')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted'], load)
onMounted(load)
</script>

<template>
  <UiCard title="صف معاینه ابتدایی">
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
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
            <td class="text-nowrap">
              <RouterLink :to="`/triage/${row.id}`" class="btn btn-soft btn-sm">ثبت معاینه</RouterLink>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>
</template>
