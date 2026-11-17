<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { todayIso } from '@/utils/date'
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
const tab = ref<'queue' | 'done'>('queue')
const date = ref<string | null>(todayIso())
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
    const query = tab.value === 'done' ? `?done=true&date=${date.value ?? todayIso()}` : ''
    rows.value = await api<QueueRow[]>(`/refractions/queue${query}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

watch([tab, date], () => {
  loading.value = true
  rows.value = []
  load()
})
useLiveRefresh(['DiagnosticOrderCreated', 'PaymentReceived', 'VisitStatusChanged', 'DiagnosticResultReady'], load)
onMounted(load)
</script>

<template>
  <UiCard title="رفرکشن">
    <template #actions>
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های رفرکشن">
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'queue' }" :aria-selected="tab === 'queue'" @click="tab = 'queue'">درخواست‌های امروز</button>
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'done' }" :aria-selected="tab === 'done'" @click="tab = 'done'">مریضان دیده‌شده</button>
      </nav>
      <input v-model="q" type="search" class="form-control" placeholder="نام یا شماره پرونده" aria-label="جستجوی مریض" />
    </template>
    <div v-if="tab === 'done'" class="seen-date"><ShamsiDateInput v-model="date" label="تاریخ" :max="todayIso()" /></div>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <CallNotice v-if="tab === 'queue'" :desks="[calls]" />
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="visible.length === 0" class="xr-state">{{ tab === 'queue' ? 'درخواستی پیدا نشد.' : 'در این روز رفرکشنی ثبت نشده است.' }}</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نوبت</th><th>مریض</th><th>وضعیت</th><th v-if="tab === 'queue'">فراخوان</th><th>عملیات</th></tr>
        </thead>
        <tbody v-if="tab === 'queue'">
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
        <tbody v-else>
          <tr v-for="row in visible" :key="row.id">
            <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
            <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
            <td><span class="xr-badge xr-badge--green">تکمیل‌شده</span></td>
            <td><RouterLink :to="`/refraction/${row.visitId}`" class="btn btn-soft btn-sm text-nowrap">مشاهده رفرکشن</RouterLink></td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>
</template>

<style scoped>
.seen-date {
  max-width: 220px;
  margin-bottom: var(--xr-gap);
}
</style>
