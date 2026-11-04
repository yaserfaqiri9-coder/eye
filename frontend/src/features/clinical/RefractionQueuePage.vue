<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { quantity } from '@/utils/money'

interface QueueRow {
  id: number
  visitId: number
  queueNumber: number
  patientFileNumber: string
  patientName: string
  status: string
  paid: boolean
}

const calls = useCallDesk('Refraction')
const rows = ref<QueueRow[]>([])
const q = ref('')
const loading = ref(true)
const error = ref('')

const visible = computed(() => {
  const term = q.value.trim()
  return rows.value.filter((row) => !term || row.patientName.includes(term) || row.patientFileNumber.includes(term))
})

async function load() {
  try {
    rows.value = await api<QueueRow[]>('/refractions/queue')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

useLiveRefresh(['DiagnosticOrderCreated', 'PaymentReceived', 'VisitStatusChanged', 'DiagnosticResultReady'], load)
onMounted(load)
</script>

<template>
  <UiCard title="درخواست‌های رفرکشن امروز">
    <template #actions>
      <input v-model="q" type="search" class="form-control" placeholder="نام یا شماره پرونده" aria-label="جستجوی مریض امروز" />
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <CallNotice :desks="[calls]" />
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="visible.length === 0" class="xr-state">درخواستی پیدا نشد.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نوبت</th><th>مریض</th><th>وضعیت</th><th>فراخوان</th><th>عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in visible" :key="row.id">
            <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
            <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
            <td><span class="xr-badge" :class="row.status === 'InProgress' ? 'xr-badge--primary' : 'xr-badge--cyan'">{{ row.status === 'InProgress' ? 'در جریان' : 'درخواست‌شده' }}</span></td>
            <td><CallControls :desk="calls" :visit-id="row.visitId" /></td>
            <td>
              <RouterLink v-if="row.paid || row.status === 'InProgress'" :to="`/refraction/${row.visitId}`" class="btn btn-soft btn-sm text-nowrap">ثبت رفرکشن</RouterLink>
              <span v-else class="xr-badge xr-badge--orange">در انتظار پرداخت</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>
</template>
