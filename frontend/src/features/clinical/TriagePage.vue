<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import { feeStatusLabels, visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'

const calls = useCallDesk('Vision')
const tab = ref<'queue' | 'done'>('queue')
const date = ref<string | null>(todayIso())
const rows = ref<VisitRow[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    const query = tab.value === 'done' ? `&done=true&date=${date.value ?? todayIso()}` : ''
    rows.value = await api<VisitRow[]>(`/clinical/worklist?kind=triage${query}`)
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
useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted'], load)
onMounted(load)
</script>

<template>
  <UiCard title="معاینه ابتدایی">
    <template #actions>
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های معاینه ابتدایی">
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'queue' }" :aria-selected="tab === 'queue'" @click="tab = 'queue'">صف</button>
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'done' }" :aria-selected="tab === 'done'" @click="tab = 'done'">مریضان دیده‌شده</button>
      </nav>
    </template>
    <div v-if="tab === 'done'" class="seen-date"><ShamsiDateInput v-model="date" label="تاریخ" :max="todayIso()" /></div>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <CallNotice v-if="tab === 'queue'" :desks="[calls]" />
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">{{ tab === 'queue' ? 'مریضی در صف نیست.' : 'در این روز مریضی دیده نشده است.' }}</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نوبت</th><th>مریض</th><th>علت</th><th>وضعیت</th><th v-if="tab === 'queue'">فراخوان</th><th>عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id" class="xr-row-link">
            <td><span class="xr-queue">{{ quantity(row.queueNumber) }}</span></td>
            <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
            <td>{{ row.reason ?? '—' }}</td>
            <td>
              <div class="xr-stack">
                <span class="xr-badge" :class="`xr-badge--${visitStatusTones[row.status]}`">{{ visitStatusLabels[row.status] }}</span>
                <span v-if="tab === 'queue' && feeStatusLabels[row.feeStatus]" class="xr-note" :class="`xr-note--${feeStatusLabels[row.feeStatus].tone}`">{{ feeStatusLabels[row.feeStatus].label }}</span>
              </div>
            </td>
            <td v-if="tab === 'queue'"><CallControls :desk="calls" :visit-id="row.id" /></td>
            <td class="text-nowrap">
              <RouterLink :to="`/triage/${row.id}`" class="btn btn-sm xr-row-link__target" :class="tab === 'queue' ? 'btn-primary' : 'btn-soft'">{{ tab === 'queue' ? 'ثبت معاینه' : 'مشاهده معاینه' }}</RouterLink>
            </td>
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
