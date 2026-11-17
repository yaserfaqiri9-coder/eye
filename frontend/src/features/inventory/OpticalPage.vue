<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
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
// Read a page at a time, so an old order can still be found and corrected.
const orderList = usePagedList<OrderRow>(() => '/optical/orders')
const orders = orderList.rows
const prescriptions = ref<OpticalRx[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    ;[, prescriptions.value] = await Promise.all([orderList.load(), api<OpticalRx[]>('/optical-prescriptions')])
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
  // One slip of the pointer on a list must not move an order: the step is named and confirmed first.
  if (!window.confirm(`سفارش ${row.orderNumber} به «${statusLabels[status]}» تغییر کند؟`)) {
    select.value = ''
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
          <thead><tr><th>مریض و نسخه</th><th>SPH / CYL / AXIS / ADD</th><th>فراخوان</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="rx in prescriptions" :key="rx.id">
              <td>
                <span class="xr-person__name">{{ rx.patientName }}</span>
                <div class="xr-meta">
                  <span class="ltr-num">{{ rx.rxNumber }}</span>
                  <span v-if="rx.fromRefraction" class="xr-badge xr-badge--cyan ms-1">بینایی‌سنجی</span>
                </div>
              </td>
              <td>
                <!-- Both eyes in one cell, right above left, so the prescription reads as it does on paper. -->
                <div class="optical-rx"><span class="xr-eye xr-eye--od">OD</span> <span class="ltr-num">{{ rx.sphereOd ?? '—' }} / {{ rx.cylinderOd ?? '—' }} / {{ rx.axisOd ?? '—' }} / {{ rx.addOd ?? '—' }}</span></div>
                <div class="optical-rx"><span class="xr-eye xr-eye--os">OS</span> <span class="ltr-num">{{ rx.sphereOs ?? '—' }} / {{ rx.cylinderOs ?? '—' }} / {{ rx.axisOs ?? '—' }} / {{ rx.addOs ?? '—' }}</span></div>
                <div v-if="rx.pd" class="xr-meta">PD <span class="ltr-num">{{ rx.pd }}</span></div>
              </td>
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
          <thead><tr><th>سفارش</th><th>مریض و شرح</th><th>مبلغ</th><th>وضعیت</th><th>اقدام</th></tr></thead>
          <tbody>
            <tr v-for="row in orders" :key="row.id">
              <td>
                <span class="ltr-num">{{ row.orderNumber }}</span>
                <div class="xr-meta text-nowrap">{{ formatDateTime(row.createdAt) }}</div>
              </td>
              <td>
                <span class="xr-person__name">{{ row.patientName ?? 'مستقل' }}</span>
                <div v-if="row.description" class="xr-meta">{{ row.description }}</div>
              </td>
              <td class="text-nowrap">
                {{ money(row.total) }}
                <div v-if="row.balance > 0" class="xr-note xr-note--orange">مانده {{ money(row.balance) }}</div>
              </td>
              <td><span class="xr-badge" :class="row.status === 'Cancelled' ? 'xr-badge--red' : row.status === 'Delivered' ? 'xr-badge--green' : row.status === 'Ready' ? 'xr-badge--cyan' : 'xr-badge--primary'">{{ statusLabels[row.status] }}</span></td>
              <td>
                <div class="xr-row-actions">
                  <select v-if="row.nextStatuses.some(allowed)" class="form-select form-select-sm" :aria-label="`تغییر وضعیت ${row.orderNumber}`" @change="changeStatus(row, ($event.target as HTMLSelectElement).value, $event.target as HTMLSelectElement)">
                    <option value="" selected disabled>تغییر وضعیت</option>
                    <option v-for="next in row.nextStatuses.filter(allowed)" :key="next" :value="next">{{ statusLabels[next] }}</option>
                  </select>
                  <RouterLink v-if="row.nextStatuses.includes('Cancelled') && auth.can('optical.cancel') && auth.can('optical.create')" :to="{ name: 'optical-order', query: { correct: row.id } }" class="btn btn-quiet btn-sm">اصلاح</RouterLink>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <UiPager :page="orderList.page.value" :page-count="orderList.pageCount.value" @go="orderList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
    </UiCard>
  </div>

  <UiModal v-if="cancelling.row" :title="`لغو سفارش ${cancelling.row.orderNumber}`" @close="cancelling.row = null">
    <form @submit.prevent="submitCancel">
      <p>اجناس به موجودی و مبلغ به حالت قبل برمی‌گردد. سفارشی که پرداخت شده باشد از اینجا لغو نمی‌شود.</p>
      <label class="form-label" for="oc-reason">دلیل</label>
      <input id="oc-reason" v-model="cancelling.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="cancelling.saving">لغو سفارش</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.optical-rx {
  display: flex;
  align-items: center;
  gap: 6px;
  white-space: nowrap;
}

.optical-rx + .optical-rx {
  margin-top: 2px;
}
</style>
