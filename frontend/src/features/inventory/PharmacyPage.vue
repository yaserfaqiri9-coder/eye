<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { rxStatusLabels, type Prescription } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'

interface SaleRow {
  id: number
  saleNumber: string
  patientName: string | null
  invoiceNumber: string
  total: number
  balance: number
  invoiceStatus: string
  isReturned: boolean
  createdAt: string
  version: number
}

const auth = useAuthStore()
const calls = useCallDesk('Pharmacy')
const prescriptions = ref<Prescription[]>([])
const sales = ref<SaleRow[]>([])
const q = ref('')
const loading = ref(true)
const error = ref('')

const openPrescriptions = computed(() => prescriptions.value.filter((p) => ['Issued', 'PartiallyDispensed'].includes(p.status)))

async function load() {
  error.value = ''
  try {
    const params = q.value.trim() ? `?q=${encodeURIComponent(q.value.trim())}` : ''
    prescriptions.value = await api<Prescription[]>(`/prescriptions${params}`)
    sales.value = (await api<{ items: SaleRow[] }>('/pharmacy/sales?pageSize=50')).items
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const returning = reactive({ row: null as SaleRow | null, reason: '', saving: false, error: '' })

async function submitReturn() {
  returning.saving = true
  returning.error = ''
  try {
    await api(`/pharmacy/sales/${returning.row!.id}/return`, { method: 'POST', body: { version: returning.row!.version, reason: returning.reason } })
    returning.row = null
    await load()
  } catch (cause) {
    returning.error = errorMessage(cause)
  } finally {
    returning.saving = false
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(load, 300)
})

useLiveRefresh(['PrescriptionCreated', 'InventoryChanged'], load)
onMounted(load)
onBeforeUnmount(() => clearTimeout(debounce))
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="calls.error.value || !calls.stationOnline.value"><CallNotice :desks="[calls]" /></div>

    <UiCard title="نسخه‌های در انتظار">
      <template #actions>
        <div class="d-flex gap-2">
          <input v-model="q" type="search" class="form-control" placeholder="شماره نسخه، پرونده یا نام" aria-label="جستجوی نسخه" />
          <RouterLink v-if="auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale' }" class="btn btn-primary text-nowrap">فروش مستقل</RouterLink>
        </div>
      </template>
      <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
      <p v-else-if="openPrescriptions.length === 0" class="xr-state">نسخه‌ای در انتظار نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>دواها</th><th>توضیح</th><th>وضعیت</th><th>فراخوان</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="rx in openPrescriptions" :key="rx.id">
              <td><span class="ltr-num">{{ rx.rxNumber }}</span></td>
              <td><span class="xr-person__name">{{ rx.patientName }}</span></td>
              <td>
                <div v-for="item in rx.items" :key="item.id" class="small">
                  {{ item.drugName }} — {{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join('، ') }} ({{ item.dispensedQuantity }} از {{ item.quantity }})
                </div>
              </td>
              <td>{{ rx.notes ?? '—' }}</td>
              <td><span class="xr-badge xr-badge--orange">{{ rxStatusLabels[rx.status] }}</span></td>
              <td><CallControls :desk="calls" :visit-id="rx.visitId" /></td>
              <td><RouterLink v-if="auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale', query: { rx: rx.id, n: rx.rxNumber } }" class="btn btn-soft btn-sm text-nowrap">تحویل دوا</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="فروش‌های اخیر">
      <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
      <p v-else-if="sales.length === 0" class="xr-state">فروشی ثبت نشده است.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>فاکتور</th><th>مبلغ</th><th>مانده</th><th>زمان</th><th v-if="auth.can('pharmacy.cancel')">عملیات</th></tr></thead>
          <tbody>
            <tr v-for="row in sales" :key="row.id">
              <td><span class="ltr-num">{{ row.saleNumber }}</span></td>
              <td>{{ row.patientName ?? 'فروش مستقل' }}</td>
              <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
              <td>{{ row.isReturned ? 'برگشت شده' : money(row.total) }}</td>
              <td>{{ money(row.balance) }}</td>
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td v-if="auth.can('pharmacy.cancel')" class="text-nowrap">
                <button v-if="!row.isReturned" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(returning, { row, reason: '', error: '' })">برگشت</button>
                <RouterLink v-if="!row.isReturned && auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale', query: { correct: row.id } }" class="btn btn-soft btn-sm ms-2">اصلاح</RouterLink>
                <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${row.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">فاکتور</RouterLink>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <UiModal v-if="returning.row" :title="`برگشت فروش ${returning.row.saleNumber}`" @close="returning.row = null">
    <form @submit.prevent="submitReturn">
      <label class="form-label" for="ret-reason">دلیل</label>
      <input id="ret-reason" v-model="returning.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="returning.error" class="alert alert-danger" role="alert">{{ returning.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="returning.saving">ثبت برگشت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
