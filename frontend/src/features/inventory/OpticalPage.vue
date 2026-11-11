<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import type { OpticalRx } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'

interface OrderRow {
  id: number
  orderNumber: string
  patientName: string | null
  description: string | null
  status: string
  invoiceNumber: string
  total: number
  balance: number
  deliveredAt: string | null
  createdAt: string
  version: number
  nextStatuses: string[]
}

const statusLabels: Record<string, string> = { Ordered: 'سفارش‌شده', InWorkshop: 'در کارگاه', Ready: 'آماده', Delivered: 'تحویل‌شده', Cancelled: 'لغو شده' }

const auth = useAuthStore()
const calls = useCallDesk('Optical')
const orders = ref<OrderRow[]>([])
const prescriptions = ref<OpticalRx[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    ;[orders.value, prescriptions.value] = await Promise.all([
      api<{ items: OrderRow[] }>('/optical/orders?pageSize=100').then((r) => r.items),
      api<OpticalRx[]>('/optical-prescriptions'),
    ])
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const cancelling = reactive({ row: null as OrderRow | null, reason: '', saving: false, error: '' })

async function submitCancel() {
  cancelling.saving = true
  cancelling.error = ''
  try {
    await api(`/optical/orders/${cancelling.row!.id}/status`, { method: 'POST', body: { status: 'Cancelled', version: cancelling.row!.version, reason: cancelling.reason } })
    cancelling.row = null
    await load()
  } catch (cause) {
    cancelling.error = errorMessage(cause)
  } finally {
    cancelling.saving = false
  }
}

async function changeStatus(row: OrderRow, status: string, select: HTMLSelectElement) {
  error.value = ''
  if (status === 'Cancelled') {
    // The list stays on "choose" so backing out of the dialog changes nothing.
    select.value = ''
    Object.assign(cancelling, { row, reason: '', saving: false, error: '' })
    return
  }
  try {
    await api(`/optical/orders/${row.id}/status`, { method: 'POST', body: { status, version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

function allowed(status: string) {
  return status === 'Cancelled' ? auth.can('optical.cancel') : auth.can('optical.edit')
}

useLiveRefresh(['OpticalPrescriptionCreated', 'OpticalOrderChanged', 'InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="calls.error.value || !calls.stationOnline.value"><CallNotice :desks="[calls]" /></div>

    <UiCard title="نسخه‌های عینک">
      <template #actions>
        <RouterLink v-if="auth.can('optical.create')" :to="{ name: 'optical-order' }" class="btn btn-primary text-nowrap">سفارش مستقل</RouterLink>
      </template>
      <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
      <p v-else-if="prescriptions.length === 0" class="xr-state">نسخه‌ای نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th><span class="xr-eye xr-eye--od">OD</span> SPH / CYL / AXIS / ADD</th><th><span class="xr-eye xr-eye--os">OS</span> SPH / CYL / AXIS / ADD</th><th>PD</th><th>فراخوان</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="rx in prescriptions" :key="rx.id">
              <td>
                <span class="ltr-num">{{ rx.rxNumber }}</span>
                <span v-if="rx.fromRefraction" class="xr-badge xr-badge--cyan ms-2">بینایی‌سنجی</span>
              </td>
              <td><span class="xr-person__name">{{ rx.patientName }}</span></td>
              <td><span class="ltr-num">{{ rx.sphereOd ?? '—' }} / {{ rx.cylinderOd ?? '—' }} / {{ rx.axisOd ?? '—' }} / {{ rx.addOd ?? '—' }}</span></td>
              <td><span class="ltr-num">{{ rx.sphereOs ?? '—' }} / {{ rx.cylinderOs ?? '—' }} / {{ rx.axisOs ?? '—' }} / {{ rx.addOs ?? '—' }}</span></td>
              <td><span class="ltr-num">{{ rx.pd ?? '—' }}</span></td>
              <td><CallControls :desk="calls" :visit-id="rx.visitId" /></td>
              <td class="text-nowrap"><RouterLink v-if="auth.can('optical.create')" :to="{ name: 'optical-order', query: { rx: rx.id } }" class="btn btn-soft btn-sm">ثبت سفارش</RouterLink><RouterLink v-if="auth.can('opticalrx.print')" :to="`/print/optical/${rx.id}`" target="_blank" class="btn btn-quiet btn-sm ms-2">چاپ نسخه</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="سفارش‌ها">
      <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
      <p v-else-if="orders.length === 0" class="xr-state">سفارشی نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>شرح</th><th>مبلغ</th><th>مانده</th><th>وضعیت</th><th>زمان</th><th>تغییر وضعیت</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="row in orders" :key="row.id">
              <td><span class="ltr-num">{{ row.orderNumber }}</span></td>
              <td>{{ row.patientName ?? 'مستقل' }}</td>
              <td>{{ row.description ?? '—' }}</td>
              <td>{{ money(row.total) }}</td>
              <td>{{ money(row.balance) }}</td>
              <td><span class="xr-badge xr-badge--primary">{{ statusLabels[row.status] }}</span></td>
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td>
                <select v-if="row.nextStatuses.some(allowed)" class="form-select form-select-sm" :aria-label="`تغییر وضعیت ${row.orderNumber}`" @change="changeStatus(row, ($event.target as HTMLSelectElement).value, $event.target as HTMLSelectElement)">
                  <option value="" selected disabled>انتخاب</option>
                  <option v-for="next in row.nextStatuses.filter(allowed)" :key="next" :value="next">{{ statusLabels[next] }}</option>
                </select>
              </td>
              <td><RouterLink v-if="row.nextStatuses.includes('Cancelled') && auth.can('optical.cancel') && auth.can('optical.create')" :to="{ name: 'optical-order', query: { correct: row.id } }" class="btn btn-soft btn-sm">اصلاح</RouterLink></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <UiModal v-if="cancelling.row" :title="`لغو سفارش ${cancelling.row.orderNumber}`" @close="cancelling.row = null">
    <form @submit.prevent="submitCancel">
      <p>اجناس به موجودی و مبلغ به حالت قبل برمی‌گردد. سفارشی که پرداخت شده باشد از اینجا لغو نمی‌شود.</p>
      <label class="form-label" for="oc-reason">دلیل</label>
      <input id="oc-reason" v-model="cancelling.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="cancelling.saving">لغو سفارش</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
