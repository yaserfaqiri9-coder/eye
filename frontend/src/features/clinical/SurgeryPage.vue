<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiPager from '@/components/UiPager.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import SurgeryPackagesPanel from './SurgeryPackagesPanel.vue'
import { eyeLabels, surgeryStatusLabels, type SurgeryRow } from './types'

const auth = useAuthStore()
const status = ref('')
// Read a page at a time, so cases older than the newest hundred stay reachable.
const list = usePagedList<SurgeryRow>(() => (status.value ? `/surgeries?status=${status.value}` : '/surgeries'))
const rows = list.rows
const loading = ref(true)
const error = ref('')
const tab = ref<'cases' | 'packages'>('cases')

async function load() {
  try {
    await list.load()
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function changeStatus(row: SurgeryRow, next: string, select: HTMLSelectElement) {
  error.value = ''
  // One slip of the pointer on a list must not move a case: the step is named and confirmed first.
  if (!window.confirm(`پرونده ${row.caseNumber} به «${surgeryStatusLabels[next]}» تغییر کند؟`)) {
    select.value = ''
    return
  }
  try {
    await api(`/surgeries/${row.id}/status`, { method: 'POST', body: { status: next, version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

function canMove(next: string) {
  return next === 'Cancelled' ? auth.can('surgery.cancel') : auth.can('surgery.approve')
}

useLiveRefresh(['SurgeryScheduled'], load)
onMounted(load)
watch(status, () => {
  list.page.value = 1
  return load()
})

function goTo(page: number) {
  list.page.value = page
  return load()
}
</script>

<template>
  <UiCard title="عملیات">
    <template #actions>
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های عملیات">
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'cases' }" :aria-selected="tab === 'cases'" @click="tab = 'cases'">پرونده‌ها</button>
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'packages' }" :aria-selected="tab === 'packages'" @click="tab = 'packages'">پکیج‌های عملیات</button>
      </nav>
      <select v-if="tab === 'cases'" v-model="status" class="form-select w-auto" aria-label="فیلتر وضعیت">
        <option value="">همه وضعیت‌ها</option>
        <option v-for="(label, value) in surgeryStatusLabels" :key="value" :value="value">{{ label }}</option>
      </select>
    </template>
    <SurgeryPackagesPanel v-if="tab === 'packages'" />
    <template v-else>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">پرونده عملیاتی نیست.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>شماره</th><th>مریض</th><th>نوع عملیات</th><th>چشم</th><th>تاریخ</th><th>وضعیت</th><th v-if="auth.can('surgery.edit')">عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><span class="ltr-num">{{ row.caseNumber }}</span></td>
            <td>{{ row.patientName }}</td>
            <td>{{ row.surgeryType }}</td>
            <td>{{ eyeLabels[row.eye] }}</td>
            <td>{{ formatDateTime(row.scheduledAt) }}</td>
            <td><span class="xr-badge xr-badge--primary">{{ surgeryStatusLabels[row.status] }}</span></td>
            <td v-if="auth.can('surgery.edit')">
              <div class="xr-row-actions">
                <RouterLink :to="`/surgery/${row.id}`" class="btn btn-soft btn-sm text-nowrap">جزئیات</RouterLink>
                <select
                  v-if="row.nextStatuses.some(canMove)"
                  class="form-select form-select-sm"
                  :aria-label="`تغییر وضعیت ${row.caseNumber}`"
                  @change="changeStatus(row, ($event.target as HTMLSelectElement).value, $event.target as HTMLSelectElement)"
                >
                  <option value="" selected disabled>تغییر وضعیت</option>
                  <option v-for="next in row.nextStatuses.filter(canMove)" :key="next" :value="next">{{ surgeryStatusLabels[next] }}</option>
                </select>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <UiPager :page="list.page.value" :page-count="list.pageCount.value" @go="goTo" />
    </template>
  </UiCard>

</template>
