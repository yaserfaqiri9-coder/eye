<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
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

interface ProductOption {
  id: number
  name: string
  salePrice: number
  onHand: number
  unit: string
}

interface SaleLine {
  productId: number | null
  quantity: number
  prescriptionItemId: number | null
  label: string
}

const auth = useAuthStore()
const prescriptions = ref<Prescription[]>([])
const sales = ref<SaleRow[]>([])
const products = ref<ProductOption[]>([])
const q = ref('')
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
  }
}

const sale = reactive({
  open: false,
  rx: null as Prescription | null,
  lines: [] as SaleLine[],
  saving: false,
  error: '',
  key: '',
  done: null as SaleRow | null,
})

const saleTotal = computed(() =>
  sale.lines.reduce((sum, line) => sum + (products.value.find((p) => p.id === line.productId)?.salePrice ?? 0) * (line.quantity || 0), 0),
)

async function openSale(rx?: Prescription) {
  Object.assign(sale, {
    open: true,
    rx: rx ?? null,
    lines: rx
      ? rx.items.filter((i) => i.dispensedQuantity < i.quantity).map((i) => ({ productId: null, quantity: i.quantity - i.dispensedQuantity, prescriptionItemId: i.id, label: i.drugName }))
      : [{ productId: null, quantity: 1, prescriptionItemId: null, label: '' }],
    saving: false,
    error: '',
    key: newIdempotencyKey(),
    done: null,
  })
  try {
    products.value = (await api<{ items: ProductOption[] }>('/inventory/products?kind=Drug&pageSize=100')).items
  } catch (cause) {
    sale.error = errorMessage(cause)
  }
}

async function submitSale() {
  sale.saving = true
  sale.error = ''
  try {
    sale.done = await api<SaleRow>('/pharmacy/sales', {
      method: 'POST',
      idempotencyKey: sale.key,
      body: {
        patientId: sale.rx?.patientId ?? null,
        prescriptionId: sale.rx?.id ?? null,
        items: sale.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, prescriptionItemId: l.prescriptionItemId })),
      },
    })
    await load()
  } catch (cause) {
    sale.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) sale.key = newIdempotencyKey()
  } finally {
    sale.saving = false
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

    <UiCard title="نسخه‌های در انتظار">
      <template #actions>
        <div class="d-flex gap-2">
          <input v-model="q" type="search" class="form-control" placeholder="شماره نسخه، پرونده یا نام" aria-label="جستجوی نسخه" />
          <button v-if="auth.can('pharmacy.create')" type="button" class="btn btn-primary text-nowrap" @click="openSale()">فروش مستقل</button>
        </div>
      </template>
      <p v-if="openPrescriptions.length === 0" class="py-3 text-center text-muted">نسخه‌ای در انتظار نیست.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره</th><th>مریض</th><th>دواها</th><th>توضیح</th><th>وضعیت</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="rx in openPrescriptions" :key="rx.id">
              <td><span class="ltr-num">{{ rx.rxNumber }}</span></td>
              <td>{{ rx.patientName }}</td>
              <td>
                <div v-for="item in rx.items" :key="item.id" class="small">
                  {{ item.drugName }} — {{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join('، ') }} ({{ item.dispensedQuantity }} از {{ item.quantity }})
                </div>
              </td>
              <td>{{ rx.notes ?? '—' }}</td>
              <td>{{ rxStatusLabels[rx.status] }}</td>
              <td><button v-if="auth.can('pharmacy.create')" type="button" class="btn btn-soft btn-sm text-nowrap" @click="openSale(rx)">تحویل دوا</button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="فروش‌های اخیر">
      <p v-if="sales.length === 0" class="py-3 text-center text-muted">فروشی ثبت نشده است.</p>
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
                <button v-if="!row.isReturned" type="button" class="btn btn-soft btn-sm" @click="Object.assign(returning, { row, reason: '', error: '' })">برگشت</button>
                <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${row.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">رسید</RouterLink>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <UiModal v-if="sale.open" :title="sale.rx ? `تحویل نسخه ${sale.rx.rxNumber}` : 'فروش مستقل'" @close="sale.open = false">
    <div v-if="sale.done" class="alert alert-success" role="status">
      فروش <span class="ltr-num">{{ sale.done.saleNumber }}</span> ثبت شد. فاکتور <span class="ltr-num">{{ sale.done.invoiceNumber }}</span> در صندوق قابل پرداخت است.
      <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${sale.done.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">چاپ رسید</RouterLink>
    </div>
    <form v-else @submit.prevent="submitSale">
      <div v-for="(line, i) in sale.lines" :key="i" class="row g-2 mb-2 align-items-end">
        <div class="col-8">
          <label class="form-label" :for="`sl-p-${i}`">{{ line.label ? `جنس برای: ${line.label}` : 'جنس' }}</label>
          <select :id="`sl-p-${i}`" v-model.number="line.productId" class="form-select" required>
            <option :value="null" disabled>انتخاب کنید</option>
            <option v-for="p in products" :key="p.id" :value="p.id">{{ p.name }} — موجودی: {{ p.onHand }} {{ p.unit }} — {{ money(p.salePrice) }}</option>
          </select>
        </div>
        <div class="col-4">
          <label class="form-label" :for="`sl-q-${i}`">مقدار</label>
          <input :id="`sl-q-${i}`" v-model.number="line.quantity" class="form-control" type="number" min="0.001" step="any" required />
        </div>
      </div>
      <button v-if="!sale.rx" type="button" class="btn btn-soft btn-sm mb-3" @click="sale.lines.push({ productId: null, quantity: 1, prescriptionItemId: null, label: '' })">افزودن قلم</button>
      <p>مجموع: <strong>{{ money(saleTotal) }}</strong></p>
      <div v-if="sale.error" class="alert alert-danger" role="alert">{{ sale.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="sale.saving">ثبت فروش</button>
    </form>
  </UiModal>

  <UiModal v-if="returning.row" :title="`برگشت فروش ${returning.row.saleNumber}`" @close="returning.row = null">
    <form @submit.prevent="submitReturn">
      <label class="form-label" for="ret-reason">دلیل</label>
      <input id="ret-reason" v-model="returning.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="returning.error" class="alert alert-danger" role="alert">{{ returning.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="returning.saving">ثبت برگشت</button>
    </form>
  </UiModal>
</template>
