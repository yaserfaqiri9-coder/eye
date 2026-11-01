<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
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

interface ProductOption {
  id: number
  name: string
  kind: string
  salePrice: number
  onHand: number
  unit: string
}

const statusLabels: Record<string, string> = { Ordered: 'سفارش‌شده', InWorkshop: 'در کارگاه', Ready: 'آماده', Delivered: 'تحویل‌شده', Cancelled: 'لغو شده' }

const auth = useAuthStore()
const orders = ref<OrderRow[]>([])
const prescriptions = ref<OpticalRx[]>([])
const products = ref<ProductOption[]>([])
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
  }
}

async function changeStatus(row: OrderRow, status: string) {
  error.value = ''
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

const form = reactive({
  open: false,
  rx: null as OpticalRx | null,
  description: '',
  productId: null as number | null,
  customDescription: '',
  customAmount: null as number | null,
  saving: false,
  error: '',
  key: '',
})

const formTotal = computed(() => (products.value.find((p) => p.id === form.productId)?.salePrice ?? 0) + (form.customAmount || 0))

async function openOrder(rx?: OpticalRx) {
  Object.assign(form, { open: true, rx: rx ?? null, description: '', productId: null, customDescription: '', customAmount: null, saving: false, error: '', key: newIdempotencyKey() })
  try {
    const all = (await api<{ items: ProductOption[] }>('/inventory/products?pageSize=100')).items
    products.value = all.filter((p) => p.kind === 'Frame' || p.kind === 'Lens')
  } catch (cause) {
    form.error = errorMessage(cause)
  }
}

async function submit() {
  const hasCustom = form.customDescription.trim() && form.customAmount
  if (!form.productId && !hasCustom) {
    form.error = 'حداقل یک جنس یا یک قلم با مبلغ وارد کنید.'
    return
  }
  form.saving = true
  form.error = ''
  try {
    await api('/optical/orders', {
      method: 'POST',
      idempotencyKey: form.key,
      body: {
        patientId: form.rx?.patientId ?? null,
        opticalPrescriptionId: form.rx?.id ?? null,
        description: form.description,
        items: form.productId ? [{ productId: form.productId, quantity: 1 }] : [],
        customLines: hasCustom ? [{ description: form.customDescription, amount: form.customAmount }] : [],
      },
    })
    form.open = false
    await load()
  } catch (cause) {
    form.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) form.key = newIdempotencyKey()
  } finally {
    form.saving = false
  }
}

useLiveRefresh(['OpticalPrescriptionCreated', 'OpticalOrderChanged', 'InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger py-2 mb-0" role="alert">{{ error }}</div>

    <UiCard title="نسخه‌های عینک">
      <template #actions>
        <button v-if="auth.can('optical.create')" type="button" class="btn btn-primary text-nowrap" @click="openOrder()">سفارش مستقل</button>
      </template>
      <p v-if="prescriptions.length === 0" class="py-3 text-center text-muted">نسخه‌ای نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>راست (SPH / CYL / AXIS / ADD)</th><th>چپ (SPH / CYL / AXIS / ADD)</th><th>PD</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="rx in prescriptions" :key="rx.id">
              <td><span class="ltr-num">{{ rx.rxNumber }}</span></td>
              <td>{{ rx.patientName }}</td>
              <td><span class="ltr-num">{{ rx.sphereOd ?? '—' }} / {{ rx.cylinderOd ?? '—' }} / {{ rx.axisOd ?? '—' }} / {{ rx.addOd ?? '—' }}</span></td>
              <td><span class="ltr-num">{{ rx.sphereOs ?? '—' }} / {{ rx.cylinderOs ?? '—' }} / {{ rx.axisOs ?? '—' }} / {{ rx.addOs ?? '—' }}</span></td>
              <td><span class="ltr-num">{{ rx.pd ?? '—' }}</span></td>
              <td><button v-if="auth.can('optical.create')" type="button" class="btn btn-soft btn-sm text-nowrap" @click="openOrder(rx)">ثبت سفارش</button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="سفارش‌ها">
      <p v-if="orders.length === 0" class="py-3 text-center text-muted">سفارشی نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>شرح</th><th>مبلغ</th><th>مانده</th><th>وضعیت</th><th>زمان</th><th>تغییر وضعیت</th></tr></thead>
          <tbody>
            <tr v-for="row in orders" :key="row.id">
              <td><span class="ltr-num">{{ row.orderNumber }}</span></td>
              <td>{{ row.patientName ?? 'مستقل' }}</td>
              <td>{{ row.description ?? '—' }}</td>
              <td>{{ money(row.total) }}</td>
              <td>{{ money(row.balance) }}</td>
              <td>{{ statusLabels[row.status] }}</td>
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td>
                <select v-if="row.nextStatuses.some(allowed)" class="form-select form-select-sm" :aria-label="`تغییر وضعیت ${row.orderNumber}`" @change="changeStatus(row, ($event.target as HTMLSelectElement).value)">
                  <option value="" selected disabled>انتخاب</option>
                  <option v-for="next in row.nextStatuses.filter(allowed)" :key="next" :value="next">{{ statusLabels[next] }}</option>
                </select>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <UiModal v-if="form.open" :title="form.rx ? `سفارش برای ${form.rx.patientName}` : 'سفارش مستقل عینک'" @close="form.open = false">
    <form @submit.prevent="submit">
      <div class="mb-3">
        <label class="form-label" for="oo-desc">شرح سفارش</label>
        <input id="oo-desc" v-model="form.description" class="form-control" maxlength="500" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="oo-product">فریم یا عدسی از گدام</label>
        <select id="oo-product" v-model.number="form.productId" class="form-select">
          <option :value="null">بدون جنس</option>
          <option v-for="p in products" :key="p.id" :value="p.id">{{ p.name }} — موجودی: {{ p.onHand }} {{ p.unit }} — {{ money(p.salePrice) }}</option>
        </select>
      </div>
      <div class="row g-3 mb-3">
        <div class="col-8">
          <label class="form-label" for="oo-custom">قلم دیگر (مثلاً ساخت عدسی)</label>
          <input id="oo-custom" v-model="form.customDescription" class="form-control" maxlength="200" />
        </div>
        <div class="col-4">
          <label class="form-label" for="oo-amount">مبلغ</label>
          <input id="oo-amount" v-model.number="form.customAmount" class="form-control" type="number" min="0" step="0.01" />
        </div>
      </div>
      <p>مجموع: <strong>{{ money(formTotal) }}</strong></p>
      <div v-if="form.error" class="alert alert-danger py-2" role="alert">{{ form.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="form.saving">ثبت سفارش</button>
    </form>
  </UiModal>
</template>
