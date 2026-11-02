<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiPager from '@/components/UiPager.vue'
import { formatDateTime } from '@/utils/date'

interface AuditRow {
  id: number
  userName: string | null
  action: string
  entity: string
  entityId: string | null
  details: string | null
  ipAddress: string | null
  createdAt: string
}

const filters = [
  { value: '', label: 'همه فعالیت‌ها' },
  { value: 'ClinicalEmergencyAccess', label: 'دسترسی اضطراری به پرونده' },
  { value: 'ClinicalRecordViewed', label: 'مشاهده پرونده طبی' },
  { value: 'LoginFailed', label: 'ورود ناموفق' },
  { value: 'UserDeactivated', label: 'غیرفعال‌سازی کاربر' },
  { value: 'UserRolesChanged', label: 'تغییر نقش' },
  { value: 'PasswordReset', label: 'بازنشانی رمز' },
  { value: 'RefundApproved', label: 'برگشت وجه' },
  { value: 'InvoiceDiscount', label: 'تخفیف' },
  { value: 'StockAdjusted', label: 'تعدیل موجودی' },
]

const rows = ref<AuditRow[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 50
const action = ref('')
const loading = ref(true)
const error = ref('')

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    if (action.value) params.set('action', action.value)
    const result = await api<{ items: AuditRow[]; total: number }>(`/audit?${params}`)
    rows.value = result.items
    total.value = result.total
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function goTo(next: number) {
  page.value = next
  load()
}

watch(action, () => {
  page.value = 1
  load()
})

onMounted(load)
</script>

<template>
  <UiCard title="ثبت فعالیت‌ها">
    <template #actions>
      <select v-model="action" class="form-select" aria-label="فیلتر فعالیت">
        <option v-for="f in filters" :key="f.value" :value="f.value">{{ f.label }}</option>
      </select>
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">موردی ثبت نشده است.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead><tr><th>زمان</th><th>کاربر</th><th>فعالیت</th><th>مورد</th><th>جزئیات</th><th>آدرس</th></tr></thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td class="text-nowrap">{{ formatDateTime(row.createdAt) }}</td>
            <td>{{ row.userName ?? '—' }}</td>
            <td><span class="ltr-num">{{ row.action }}</span></td>
            <td><span class="ltr-num">{{ row.entity }} {{ row.entityId }}</span></td>
            <td>{{ row.details ?? '—' }}</td>
            <td><span class="ltr-num">{{ row.ipAddress ?? '—' }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>
    <UiPager :page="page" :page-count="pageCount" @go="goTo" />
  </UiCard>
</template>
