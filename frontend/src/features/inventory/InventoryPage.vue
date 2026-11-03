<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'

interface ProductRow {
  id: number
  code: string
  name: string
  kind: string
  category: string | null
  unit: string
  reorderLevel: number
  salePrice: number
  isActive: boolean
  onHand: number
  version: number
}

interface BatchRow {
  id: number
  productId: number
  productName: string
  locationCode: string
  locationName: string
  lotNumber: string
  expiryDate: string | null
  quantity: number
  unitCost: number
}

interface MovementRow {
  id: number
  productName: string
  locationName: string
  lotNumber: string
  kind: string
  quantity: number
  sourceType: string
  reason: string | null
  createdAt: string
}

const kindLabels: Record<string, string> = { Drug: 'دوا', Frame: 'فریم', Lens: 'عدسی', General: 'عمومی' }
const locationLabels: Record<string, string> = { central: 'گدام مرکزی', pharmacy: 'دواخانه', optical: 'بخش عینک' }
const movementLabels: Record<string, string> = { Purchase: 'خرید', Sale: 'فروش', SaleReturn: 'برگشت فروش', Adjustment: 'تعدیل', Consumption: 'مصرف بخش', TransferOut: 'انتقال خروجی', TransferIn: 'انتقال ورودی' }

const auth = useAuthStore()
const tab = ref('products')
const q = ref('')
const products = ref<ProductRow[]>([])
const batches = ref<BatchRow[]>([])
const movements = ref<MovementRow[]>([])
const alerts = ref<{ lowStock: ProductRow[]; expiring: BatchRow[] }>({ lowStock: [], expiring: [] })
const loading = ref(true)
const error = ref('')

const tabs = computed(() => [
  { key: 'products', label: 'اجناس' },
  { key: 'batches', label: 'موجودی بچ‌ها' },
  { key: 'alerts', label: `هشدارها (${alerts.value.lowStock.length + alerts.value.expiring.length})` },
  { key: 'movements', label: 'گردش موجودی' },
])

async function load() {
  error.value = ''
  try {
    const params = new URLSearchParams({ pageSize: '100' })
    if (q.value.trim()) params.set('q', q.value.trim())
    products.value = (await api<{ items: ProductRow[] }>(`/inventory/products?${params}`)).items
    ;[batches.value, alerts.value, movements.value] = await Promise.all([
      api<BatchRow[]>('/inventory/batches'),
      api<typeof alerts.value>('/inventory/alerts'),
      api<{ items: MovementRow[] }>('/inventory/movements?pageSize=100').then((r) => r.items),
    ])
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const product = reactive({ open: false, id: null as number | null, version: null as number | null, code: '', name: '', kind: 'Drug', category: '', unit: 'عدد', reorderLevel: 0, salePrice: 0, isActive: true, saving: false, error: '' })

function openProduct(row?: ProductRow) {
  Object.assign(product, {
    open: true, id: row?.id ?? null, version: row?.version ?? null, code: row?.code ?? '', name: row?.name ?? '', kind: row?.kind ?? 'Drug',
    category: row?.category ?? '', unit: row?.unit ?? 'عدد', reorderLevel: row?.reorderLevel ?? 0, salePrice: row?.salePrice ?? 0, isActive: row?.isActive ?? true, saving: false, error: '',
  })
}

async function saveProduct() {
  product.saving = true
  product.error = ''
  const body = { code: product.code, name: product.name, kind: product.kind, category: product.category, unit: product.unit, reorderLevel: product.reorderLevel, salePrice: product.salePrice, isActive: product.isActive, version: product.version }
  try {
    if (product.id) await api(`/inventory/products/${product.id}`, { method: 'PUT', body })
    else await api('/inventory/products', { method: 'POST', body })
    product.open = false
    await load()
  } catch (cause) {
    product.error = errorMessage(cause)
  } finally {
    product.saving = false
  }
}

const adjust = reactive({ batch: null as BatchRow | null, kind: 'Adjustment', quantity: 0, reason: '', saving: false, error: '', key: '' })

function openAdjust(row: BatchRow) {
  Object.assign(adjust, { batch: row, kind: 'Adjustment', quantity: 0, reason: '', saving: false, error: '', key: newIdempotencyKey() })
}

async function saveAdjust() {
  adjust.saving = true
  adjust.error = ''
  try {
    await api('/inventory/adjustments', { method: 'POST', idempotencyKey: adjust.key, body: { batchId: adjust.batch!.id, quantity: adjust.quantity, kind: adjust.kind, reason: adjust.reason } })
    adjust.batch = null
    await load()
  } catch (cause) {
    adjust.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) adjust.key = newIdempotencyKey()
  } finally {
    adjust.saving = false
  }
}

const transfer = reactive({ batch: null as BatchRow | null, to: 'pharmacy', quantity: 1, saving: false, error: '', key: '' })

function openTransfer(row: BatchRow) {
  Object.assign(transfer, { batch: row, to: row.locationCode === 'pharmacy' ? 'central' : 'pharmacy', quantity: 1, saving: false, error: '', key: newIdempotencyKey() })
}

async function saveTransfer() {
  transfer.saving = true
  transfer.error = ''
  try {
    await api('/inventory/transfers', { method: 'POST', idempotencyKey: transfer.key, body: { batchId: transfer.batch!.id, toLocationCode: transfer.to, quantity: transfer.quantity } })
    transfer.batch = null
    await load()
  } catch (cause) {
    transfer.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) transfer.key = newIdempotencyKey()
  } finally {
    transfer.saving = false
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(load, 300)
})

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های گدام">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
      <div class="d-flex gap-2">
        <button v-if="auth.can('inventory.create')" type="button" class="btn btn-soft" @click="openProduct()">جنس جدید</button>
        <RouterLink v-if="auth.can('inventory.create')" :to="{ name: 'inventory-purchase' }" class="btn btn-primary">ثبت خرید</RouterLink>
      </div>
    </div>
    <div class="xr-card__body">
      <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
      <template v-else-if="tab === 'products'">
        <div class="row mb-3"><div class="col-md-6"><input v-model="q" type="search" class="form-control" placeholder="جستجوی نام یا کد جنس" aria-label="جستجوی جنس" /></div></div>
        <p v-if="products.length === 0" class="xr-state">جنسی ثبت نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>کد</th><th>نام</th><th>نوع</th><th>موجودی</th><th>حد کمبود</th><th>قیمت فروش</th><th v-if="auth.can('inventory.edit')">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in products" :key="row.id">
                <td><span class="ltr-num">{{ row.code }}</span></td>
                <td>{{ row.name }}</td>
                <td>{{ kindLabels[row.kind] }}</td>
                <td>{{ quantity(row.onHand) }} {{ row.unit }}</td>
                <td>{{ quantity(row.reorderLevel) }}</td>
                <td>{{ money(row.salePrice) }}</td>
                <td v-if="auth.can('inventory.edit')"><button type="button" class="btn btn-soft btn-sm" @click="openProduct(row)">ویرایش</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'batches'">
        <p v-if="batches.length === 0" class="xr-state">موجودی ثبت نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>جنس</th><th>محل</th><th>بچ</th><th>انقضا</th><th>مقدار</th><th>قیمت خرید</th><th>عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in batches" :key="row.id">
                <td>{{ row.productName }}</td>
                <td>{{ row.locationName }}</td>
                <td><span class="ltr-num">{{ row.lotNumber || '—' }}</span></td>
                <td>{{ formatDate(row.expiryDate) }}</td>
                <td>{{ quantity(row.quantity) }}</td>
                <td>{{ money(row.unitCost) }}</td>
                <td class="text-nowrap">
                  <button v-if="auth.can('inventory.edit')" type="button" class="btn btn-soft btn-sm" @click="openTransfer(row)">انتقال</button>
                  <button v-if="auth.can('inventory.approve')" type="button" class="btn btn-soft btn-sm ms-2" @click="openAdjust(row)">تعدیل</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'alerts'">
        <h3 class="h6">کمبود موجودی</h3>
        <p v-if="alerts.lowStock.length === 0" class="text-muted">موردی نیست.</p>
        <ul v-else>
          <li v-for="row in alerts.lowStock" :key="row.id">{{ row.name }}: {{ quantity(row.onHand) }} {{ row.unit }} (حد: {{ quantity(row.reorderLevel) }})</li>
        </ul>
        <h3 class="h6 mt-3">انقضای نزدیک یا گذشته (۹۰ روز)</h3>
        <p v-if="alerts.expiring.length === 0" class="text-muted">موردی نیست.</p>
        <ul v-else>
          <li v-for="row in alerts.expiring" :key="row.id">{{ row.productName }} — {{ row.locationName }} — انقضا: {{ formatDate(row.expiryDate) }} — مقدار: {{ quantity(row.quantity) }}</li>
        </ul>
      </template>

      <template v-else>
        <p v-if="movements.length === 0" class="xr-state">گردشی ثبت نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>زمان</th><th>جنس</th><th>محل</th><th>نوع</th><th>مقدار</th><th>دلیل</th></tr></thead>
            <tbody>
              <tr v-for="row in movements" :key="row.id">
                <td>{{ formatDateTime(row.createdAt) }}</td>
                <td>{{ row.productName }}</td>
                <td>{{ row.locationName }}</td>
                <td>{{ movementLabels[row.kind] }}</td>
                <td><span class="ltr-num">{{ row.quantity }}</span></td>
                <td>{{ row.reason ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </div>
  </section>

  <UiModal v-if="product.open" :title="product.id ? 'ویرایش جنس' : 'جنس جدید'" @close="product.open = false">
    <form @submit.prevent="saveProduct">
      <div class="row g-3 mb-3">
        <div class="col-6"><label class="form-label" for="pr-code">کد</label><input id="pr-code" v-model="product.code" class="form-control" dir="ltr" maxlength="40" required /></div>
        <div class="col-6"><label class="form-label" for="pr-kind">نوع</label><select id="pr-kind" v-model="product.kind" class="form-select"><option v-for="(label, value) in kindLabels" :key="value" :value="value">{{ label }}</option></select></div>
        <div class="col-12"><label class="form-label" for="pr-name">نام جنس</label><input id="pr-name" v-model="product.name" class="form-control" maxlength="200" required /></div>
        <div class="col-6"><label class="form-label" for="pr-cat">دسته</label><input id="pr-cat" v-model="product.category" class="form-control" maxlength="80" /></div>
        <div class="col-6"><label class="form-label" for="pr-unit">واحد</label><input id="pr-unit" v-model="product.unit" class="form-control" maxlength="30" required /></div>
        <div class="col-6"><label class="form-label" for="pr-reorder">حد کمبود</label><input id="pr-reorder" v-model.number="product.reorderLevel" class="form-control" type="number" min="0" step="any" /></div>
        <div class="col-6"><label class="form-label" for="pr-price">قیمت فروش</label><input id="pr-price" v-model.number="product.salePrice" class="form-control" type="number" min="0" step="0.01" /></div>
      </div>
      <div v-if="product.error" class="alert alert-danger" role="alert">{{ product.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="product.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="adjust.batch" :title="`تعدیل ${adjust.batch.productName}`" @close="adjust.batch = null">
    <form @submit.prevent="saveAdjust">
      <p>موجودی این بچ: {{ quantity(adjust.batch.quantity) }}</p>
      <div class="mb-3"><label class="form-label" for="ad-kind">نوع</label><select id="ad-kind" v-model="adjust.kind" class="form-select"><option value="Adjustment">تعدیل شمارش</option><option value="Consumption">مصرف بخش</option></select></div>
      <div class="mb-3"><label class="form-label" for="ad-qty">تغییر مقدار (کاهش با علامت منفی)</label><input id="ad-qty" v-model.number="adjust.quantity" class="form-control" dir="ltr" type="number" step="any" required /></div>
      <div class="mb-3"><label class="form-label" for="ad-reason">دلیل</label><input id="ad-reason" v-model="adjust.reason" class="form-control" minlength="3" maxlength="300" required /></div>
      <div v-if="adjust.error" class="alert alert-danger" role="alert">{{ adjust.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="adjust.saving">ثبت تعدیل</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="transfer.batch" :title="`انتقال ${transfer.batch.productName}`" @close="transfer.batch = null">
    <form @submit.prevent="saveTransfer">
      <p>از {{ transfer.batch.locationName }} — موجودی: {{ quantity(transfer.batch.quantity) }}</p>
      <div class="mb-3"><label class="form-label" for="tr-to">مقصد</label><select id="tr-to" v-model="transfer.to" class="form-select"><option v-for="(label, value) in locationLabels" :key="value" :value="value">{{ label }}</option></select></div>
      <div class="mb-3"><label class="form-label" for="tr-qty">مقدار</label><input id="tr-qty" v-model.number="transfer.quantity" class="form-control" type="number" min="0.001" step="any" :max="transfer.batch.quantity" required /></div>
      <div v-if="transfer.error" class="alert alert-danger" role="alert">{{ transfer.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="transfer.saving">ثبت انتقال</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
