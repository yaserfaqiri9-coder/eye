<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import InventoryStockList from './InventoryStockList.vue'
import type { InventoryListRow } from './stockPresentation'
import UiModal from '@/components/UiModal.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { removeRecord } from '@/utils/removeRecord'
import { drugFormChoices, loadStoredDrugForms, productGroupLabels, stockUnits } from './drugForms'
import { movementLabels } from './labels'

interface LocationRow {
  code: string
  name: string
  productCount: number
  batchCount: number
  stockValue: number
  lastMovementAt: string | null
}

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
  reversible: boolean
}

interface PurchaseRow {
  id: number
  purchaseNumber: string
  supplierName: string | null
  locationName: string
  total: number
  isCancelled: boolean
  cancelReason: string | null
  createdAt: string
  version: number
}

// The product group (دوا، عمومی …). The drug form is a separate field, kept in the product's category.
const kindLabels = productGroupLabels
const storedDrugForms = ref<string[]>([])
const locationLabels: Record<string, string> = { central: 'گدام مرکزی', pharmacy: 'دواخانه', optical: 'بخش عینک' }

const locationVisuals: Record<string, { tone: string; icon: string }> = {
  central: { tone: 'central', icon: 'ri-archive-stack-line' },
  pharmacy: { tone: 'pharmacy', icon: 'ri-capsule-line' },
  optical: { tone: 'optical', icon: 'ri-glasses-2-line' },
}

function locationVisual(code: string) {
  return locationVisuals[code.toLowerCase()] ?? { tone: 'default', icon: 'ri-store-2-line' }
}

const auth = useAuthStore()
const tab = ref('products')
const stockView = ref<'batches' | 'alerts'>('batches')
const historyView = ref<'purchases' | 'movements'>('purchases')
const stockLocations = ref(new Map<number, { code: string; name: string }[]>())
const locationError = ref('')
const locations = ref<LocationRow[]>([])
const products = ref<ProductRow[]>([])
const batches = ref<BatchRow[]>([])
const movements = ref<MovementRow[]>([])
const purchases = ref<PurchaseRow[]>([])
const alerts = ref<{ lowStock: ProductRow[]; expiring: BatchRow[] }>({ lowStock: [], expiring: [] })
const loading = ref(true)
const error = ref('')
const notice = ref('')

const alertCount = computed(() => alerts.value.lowStock.length + alerts.value.expiring.length)
const tabs = computed(() => [
  { key: 'locations', label: 'گدام‌ها', count: 0 },
  { key: 'products', label: 'اجناس', count: 0 },
  { key: 'stock', label: 'موجودی', count: alertCount.value },
  { key: 'history', label: 'سوابق', count: 0 },
])

// Read every server page so local search, filters and eight-row pagination include the entire catalogue.
let productsController: AbortController | null = null

async function loadProducts() {
  productsController?.abort()
  const controller = new AbortController()
  productsController = controller
  try {
    const all: ProductRow[] = []
    let page = 1
    let total = 0
    do {
      const result = await api<{ items: ProductRow[]; total: number }>(`/inventory/products?pageSize=100&page=${page++}`, { signal: controller.signal })
      all.push(...result.items)
      total = result.total
      if (!result.items.length) break
    } while (all.length < total && !controller.signal.aborted)
    if (!controller.signal.aborted) products.value = all
  } catch (cause) {
    if (!controller.signal.aborted) throw cause
  }
}

// The batch endpoint is capped, so use full per-location stock to avoid inventing or omitting locations.
let locationRequest = 0
async function loadStockLocations(rows: LocationRow[]) {
  const request = ++locationRequest
  const result = await Promise.allSettled(rows.map(async location => ({
    location,
    detail: await api<{ stock: { productId: number }[] }>(`/inventory/locations/${encodeURIComponent(location.code)}`),
  })))
  if (request !== locationRequest) return
  const map = new Map<number, { code: string; name: string }[]>()
  locationError.value = result.some(r => r.status === 'rejected') ? 'فهرست محل‌های نگهداری کامل بارگذاری نشد؛ دوباره تلاش کنید.' : ''
  for (const item of result) {
    if (item.status !== 'fulfilled') continue
    const { location, detail } = item.value
    for (const row of detail.stock) {
      const list = map.get(row.productId) ?? []
      list.push({ code: location.code, name: location.name })
      map.set(row.productId, list)
    }
  }
  stockLocations.value = map
}
const listRows = computed<InventoryListRow[]>(() => products.value.map(row => ({
  id: row.id, name: row.name, code: row.code, kind: row.kind, category: row.category,
  quantity: row.onHand, unit: row.unit, reorderLevel: row.reorderLevel, isActive: row.isActive,
  locations: stockLocations.value.get(row.id) ?? [],
})))
const productById = (id: number) => products.value.find(row => row.id === id)!

async function load() {
  error.value = ''
  try {
    await Promise.all([
      loadProducts(),
      Promise.all([
        api<LocationRow[]>('/inventory/locations'),
        api<BatchRow[]>('/inventory/batches'),
        api<typeof alerts.value>('/inventory/alerts'),
        api<{ items: MovementRow[] }>('/inventory/movements?pageSize=100').then((r) => r.items),
        api<{ items: PurchaseRow[] }>('/inventory/purchases?pageSize=100').then((r) => r.items),
      ]).then((rows) => {
        ;[locations.value, batches.value, alerts.value, movements.value, purchases.value] = rows
        return loadStockLocations(locations.value)
      }),
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
  loadStoredDrugForms().then((forms) => (storedDrugForms.value = forms)).catch(() => {})
}

const drugFormOptions = computed(() => drugFormChoices(storedDrugForms.value, product.category))

async function saveProduct() {
  product.error = ''
  // A drug always carries its form, so the pharmacy can tell a tablet from an eye drop of the same name.
  if (product.kind === 'Drug' && !product.category.trim()) {
    product.error = 'نوعیت دوا را انتخاب کنید.'
    return
  }
  product.saving = true
  const body = { name: product.name, kind: product.kind, category: product.category, unit: product.unit, reorderLevel: product.reorderLevel, salePrice: product.salePrice, isActive: product.isActive, version: product.version }
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

async function removeProduct(row: ProductRow) {
  error.value = ''
  notice.value = ''
  try {
    notice.value = await removeRecord(`/inventory/products/${row.id}?version=${row.version}`, row.name)
    if (notice.value) await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

const cancelPurchase = reactive({ row: null as PurchaseRow | null, reason: '', saving: false, error: '' })

async function submitCancelPurchase() {
  cancelPurchase.saving = true
  cancelPurchase.error = ''
  try {
    await api(`/inventory/purchases/${cancelPurchase.row!.id}/cancel`, { method: 'POST', body: { reason: cancelPurchase.reason, version: cancelPurchase.row!.version } })
    cancelPurchase.row = null
    await load()
  } catch (cause) {
    cancelPurchase.error = errorMessage(cause)
  } finally {
    cancelPurchase.saving = false
  }
}

const reverse = reactive({ row: null as MovementRow | null, reason: '', saving: false, error: '' })

function canReverse(row: MovementRow) {
  return row.reversible && auth.can(row.kind === 'TransferOut' ? 'inventory.edit' : 'inventory.approve')
}

async function submitReverse() {
  reverse.saving = true
  reverse.error = ''
  try {
    await api(`/inventory/movements/${reverse.row!.id}/reverse`, { method: 'POST', body: { reason: reverse.reason } })
    reverse.row = null
    await load()
  } catch (cause) {
    reverse.error = errorMessage(cause)
  } finally {
    reverse.saving = false
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

onBeforeUnmount(() => { productsController?.abort(); locationRequest++ })

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
  <section class="xr-card inventory-workspace">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های گدام">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" :aria-describedby="t.count ? 'inventory-alert-count' : undefined" @click="tab = t.key">
          {{ t.label }}<span v-if="t.count" class="xr-tab__count" aria-hidden="true">{{ quantity(t.count) }}</span>
        </button>
      </nav>
      <span v-if="alertCount" id="inventory-alert-count" class="visually-hidden">{{ quantity(alertCount) }} هشدار موجودی</span>
      <div class="d-flex flex-wrap gap-2">
        <button v-if="auth.can('inventory.create')" type="button" class="btn btn-soft" @click="openProduct()">ثبت محصول</button>
        <RouterLink v-if="auth.can('inventory.create')" :to="{ name: 'inventory-purchase', query: { opening: '1' } }" class="btn btn-soft">موجودی اول دوره</RouterLink>
        <RouterLink v-if="auth.can('inventory.create')" :to="{ name: 'inventory-purchase' }" class="btn btn-primary">ثبت خرید</RouterLink>
      </div>
    </div>
    <div class="xr-card__body inventory-workspace__body">
      <UiListSkeleton v-if="loading" />
      <template v-else-if="tab === 'locations'">
        <p v-if="locations.length === 0" class="xr-state">گدامی ثبت نشده است.</p>
        <div v-else class="xr-location-list" role="list">
          <div class="xr-location-list__header" aria-hidden="true">
            <span>نام گدام</span>
            <span>جنس</span>
            <span>ارزش موجودی</span>
            <span></span>
          </div>
          <RouterLink
            v-for="row in locations"
            :key="row.code"
            :to="{ name: 'inventory-location', params: { code: row.code } }"
            class="xr-location-row"
            :class="`xr-location-row--${locationVisual(row.code).tone}`"
            role="listitem"
          >
            <span class="xr-location-row__name">
              <span class="xr-location-row__avatar" aria-hidden="true">
                <i :class="locationVisual(row.code).icon"></i>
              </span>
              <strong :title="row.name">{{ row.name }}</strong>
            </span>
            <strong class="xr-location-row__products ltr-num">{{ quantity(row.productCount) }}</strong>
            <strong class="xr-location-row__value">{{ money(row.stockValue) }}</strong>
            <i class="ri-arrow-left-s-line xr-location-row__arrow" aria-hidden="true"></i>
          </RouterLink>
        </div>
      </template>

      <template v-else-if="tab === 'products'">
        <InventoryStockList :rows="listRows" :error="locationError">
          <template v-if="auth.can('inventory.edit')" #name="{ row }"><button type="button" @click="openProduct(productById(row.id))">{{ row.name }}</button></template>
          <template v-if="auth.can('inventory.edit')" #actions="{ row }">
            <button type="button" @click="openProduct(productById(row.id))">ویرایش جنس</button>
            <button type="button" @click="removeProduct(productById(row.id))">حذف جنس</button>
          </template>
        </InventoryStockList>
      </template>

      <template v-else-if="tab === 'stock'">
        <nav class="xr-inventory-switch" role="tablist" aria-label="جزئیات موجودی">
          <button type="button" role="tab" :class="{ 'xr-inventory-switch__button--active': stockView === 'batches' }" :aria-selected="stockView === 'batches'" @click="stockView = 'batches'">بچ‌ها</button>
          <button type="button" role="tab" :class="{ 'xr-inventory-switch__button--active': stockView === 'alerts' }" :aria-selected="stockView === 'alerts'" :aria-describedby="alertCount ? 'inventory-alert-count' : undefined" @click="stockView = 'alerts'">
            هشدارها <span class="xr-inventory-switch__count" aria-hidden="true">{{ quantity(alertCount) }}</span>
          </button>
        </nav>

        <template v-if="stockView === 'batches'">
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
        <template v-else>
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
      </template>

      <template v-else>
        <nav class="xr-inventory-switch" role="tablist" aria-label="نوع سابقه">
          <button type="button" role="tab" :class="{ 'xr-inventory-switch__button--active': historyView === 'purchases' }" :aria-selected="historyView === 'purchases'" @click="historyView = 'purchases'">خریدها</button>
          <button type="button" role="tab" :class="{ 'xr-inventory-switch__button--active': historyView === 'movements' }" :aria-selected="historyView === 'movements'" @click="historyView = 'movements'">گردش موجودی</button>
        </nav>

        <template v-if="historyView === 'purchases'">
          <p v-if="purchases.length === 0" class="xr-state">خریدی ثبت نشده است.</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>تأمین‌کننده</th><th>محل</th><th>مجموع</th><th>زمان</th><th>وضعیت</th><th v-if="auth.can('inventory.cancel')">عملیات</th></tr></thead>
              <tbody>
                <tr v-for="row in purchases" :key="row.id">
                  <td><span class="ltr-num">{{ row.purchaseNumber }}</span></td>
                  <td>{{ row.supplierName ?? '—' }}</td>
                  <td>{{ row.locationName }}</td>
                  <td>{{ money(row.total) }}</td>
                  <td>{{ formatDateTime(row.createdAt) }}</td>
                  <td>
                    <span class="xr-badge" :class="row.isCancelled ? 'xr-badge--red' : 'xr-badge--green'" :title="row.cancelReason ?? undefined">{{ row.isCancelled ? 'لغو شده' : 'ثبت‌شده' }}</span>
                    <div v-if="row.cancelReason" class="small text-muted">{{ row.cancelReason }}</div>
                  </td>
                  <td v-if="auth.can('inventory.cancel')" class="text-nowrap">
                    <template v-if="!row.isCancelled">
                      <RouterLink v-if="auth.can('inventory.create')" :to="{ name: 'inventory-purchase', query: { correct: row.id } }" class="btn btn-soft btn-sm">اصلاح</RouterLink>
                      <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(cancelPurchase, { row, reason: '', saving: false, error: '' })">لغو</button>
                    </template>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
        <template v-else>
          <p v-if="movements.length === 0" class="xr-state">گردشی ثبت نشده است.</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>زمان</th><th>جنس</th><th>محل</th><th>نوع</th><th>مقدار</th><th>دلیل</th><th>عملیات</th></tr></thead>
              <tbody>
                <tr v-for="row in movements" :key="row.id">
                  <td>{{ formatDateTime(row.createdAt) }}</td>
                  <td>{{ row.productName }}</td>
                  <td>{{ row.locationName }}</td>
                  <td>{{ movementLabels[row.kind] }}</td>
                  <td><span class="ltr-num">{{ row.quantity }}</span></td>
                  <td>{{ row.reason ?? '—' }}</td>
                  <td><button v-if="canReverse(row)" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(reverse, { row, reason: '', saving: false, error: '' })">برگشت</button></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </template>
    </div>
  </section>

  <UiModal v-if="product.open" :title="product.id ? 'ویرایش جنس' : 'جنس جدید'" @close="product.open = false">
    <form @submit.prevent="saveProduct">
      <div class="row g-3 mb-3">
        <div class="col-6"><label class="form-label" for="pr-code">کد</label><input id="pr-code" :value="product.id ? product.code : 'خودکار'" class="form-control" dir="ltr" readonly tabindex="-1" /></div>
        <div class="col-6"><label class="form-label" for="pr-kind">گروه جنس</label><select id="pr-kind" v-model="product.kind" class="form-select"><option v-for="(label, value) in kindLabels" :key="value" :value="value">{{ label }}</option></select></div>
        <div class="col-12"><label class="form-label" for="pr-name">نام جنس</label><input id="pr-name" v-model="product.name" class="form-control" maxlength="200" required /></div>
        <div v-if="product.kind === 'Drug'" class="col-6">
          <label class="form-label" for="pr-form">نوعیت دوا</label>
          <select id="pr-form" v-model="product.category" class="form-select" required>
            <option value="" disabled>انتخاب کنید</option>
            <option v-for="form in drugFormOptions" :key="form" :value="form">{{ form }}</option>
          </select>
        </div>
        <div v-else class="col-6"><label class="form-label" for="pr-cat">دسته</label><input id="pr-cat" v-model="product.category" class="form-control" maxlength="80" /></div>
        <div class="col-6">
          <label class="form-label" for="pr-unit">واحد موجودی</label>
          <input id="pr-unit" v-model="product.unit" class="form-control" maxlength="30" list="pr-unit-options" required />
          <datalist id="pr-unit-options"><option v-for="unit in stockUnits" :key="unit" :value="unit" /></datalist>
        </div>
        <div class="col-6"><label class="form-label" for="pr-reorder">حد کمبود</label><input id="pr-reorder" v-model.number="product.reorderLevel" class="form-control" type="number" min="0" step="any" /></div>
        <div class="col-6"><label class="form-label" for="pr-price">قیمت فروش</label><input id="pr-price" v-model.number="product.salePrice" class="form-control" type="number" min="0" step="0.01" /></div>
      </div>
      <label class="form-check-label mb-3"><input v-model="product.isActive" type="checkbox" class="form-check-input ms-2" />فعال</label>
      <div v-if="product.error" class="alert alert-danger" role="alert">{{ product.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="product.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="cancelPurchase.row" :title="`لغو خرید ${cancelPurchase.row.purchaseNumber}`" @close="cancelPurchase.row = null">
    <form @submit.prevent="submitCancelPurchase">
      <p>اجناس این خرید از موجودی کم می‌شوند و ثبت مالی آن برمی‌گردد. اگر بخشی از آن فروخته یا منتقل شده باشد، لغو انجام نمی‌شود.</p>
      <label class="form-label" for="cp-reason">دلیل</label>
      <input id="cp-reason" v-model="cancelPurchase.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelPurchase.error" class="alert alert-danger" role="alert">{{ cancelPurchase.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="cancelPurchase.saving">لغو خرید</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="reverse.row" :title="`برگشت ${movementLabels[reverse.row.kind]}: ${reverse.row.productName}`" @close="reverse.row = null">
    <form @submit.prevent="submitReverse">
      <p>گردشی برعکس این مورد ثبت می‌شود و موجودی به حالت قبل برمی‌گردد. سابقه هر دو می‌ماند.</p>
      <label class="form-label" for="rv-reason">دلیل</label>
      <input id="rv-reason" v-model="reverse.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="reverse.error" class="alert alert-danger" role="alert">{{ reverse.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="reverse.saving">ثبت برگشت</button>
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

<style scoped>
.xr-tab__count,
.xr-inventory-switch__count {
  display: inline-grid;
  min-width: 1.25rem;
  min-height: 1.25rem;
  margin-inline-start: 0.3rem;
  padding-inline: 0.3rem;
  place-items: center;
  border-radius: 999px;
  background: var(--xr-tone-primary-bg);
  color: var(--xr-tone-primary-text);
  font-size: 0.7rem;
  font-weight: 700;
  line-height: 1;
}

.inventory-workspace { display: flex; flex-direction: column; border-radius: 7px; box-shadow: none; border: 1px solid #f0f1f5; }
.inventory-workspace > .xr-card__header { padding: 14px 24px; min-height: 64px; border-top: 0; border-bottom: 1px solid #f0f1f5; }
.inventory-workspace__body { padding: 0; }
.inventory-workspace__body > :not(.inventory-list) { margin: 20px 24px; }
.inventory-workspace .xr-tabs { gap: 18px; }
.inventory-workspace .xr-tab { font-size: 12px; }
.inventory-workspace .xr-tab { color: #69768a; }.inventory-workspace .xr-tab--active { color: #315bd4; background: #f0f3fb; border-radius: 4px; }
.inventory-workspace > .xr-card__header .btn { font-size: 13px; border-radius: 4px; padding: 7px 12px; }
.inventory-workspace > .xr-card__header .btn-primary { background: #2854ce; border-color: #2854ce; }

.xr-inventory-switch {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 0.25rem;
  margin-block-end: 1rem;
  padding: 0.25rem;
  border: 1px solid var(--xr-row-border);
  border-radius: var(--xr-radius-control);
  background: #f5f8f9;
}

.xr-inventory-switch button {
  min-height: 2.1rem;
  padding: 0.35rem 0.8rem;
  border: 0;
  border-radius: 0.55rem;
  background: transparent;
  color: var(--xr-text-soft);
  font: inherit;
  font-size: 0.82rem;
  cursor: pointer;
}

.xr-inventory-switch button:hover {
  color: var(--xr-primary);
}

.xr-inventory-switch button:focus-visible {
  outline: 2px solid var(--xr-primary);
  outline-offset: 1px;
}

.xr-inventory-switch .xr-inventory-switch__button--active {
  background: var(--xr-surface);
  color: var(--xr-primary);
  box-shadow: 0 1px 4px rgba(20, 54, 65, 0.12);
  font-weight: 650;
}

.xr-inventory-switch__count {
  min-width: 1.15rem;
  min-height: 1.15rem;
  font-size: 0.66rem;
}

.xr-location-list {
  overflow: hidden;
  border: 1px solid var(--xr-row-border);
  border-radius: var(--xr-radius-control);
  background: var(--xr-surface);
}

.xr-location-list__header,
.xr-location-row {
  display: grid;
  grid-template-columns: minmax(10rem, 1fr) 5.5rem minmax(10rem, 0.65fr) 1rem;
  align-items: center;
  gap: 1.25rem;
}

.xr-location-list__header {
  padding: 0.55rem 1rem;
  border-inline-start: 3px solid transparent;
  background: #f7fafb;
  color: var(--xr-text-soft);
  font-size: 0.74rem;
  font-weight: 600;
}

.xr-location-row {
  --location-accent: var(--xr-primary);
  --location-accent-strong: var(--xr-tone-primary-text);
  --location-tint: var(--xr-tone-primary-bg);
  min-width: 0;
  min-height: 4rem;
  padding: 0.65rem 1rem;
  border-inline-start: 3px solid var(--location-accent);
  color: var(--xr-text);
  text-decoration: none;
  transition: background-color 150ms ease;
}

.xr-location-row + .xr-location-row {
  border-block-start: 1px solid var(--xr-row-border);
}

.xr-location-row--pharmacy {
  --location-accent: var(--xr-status-green);
  --location-accent-strong: var(--xr-tone-green-text);
  --location-tint: var(--xr-tone-green-bg);
}

.xr-location-row--optical {
  --location-accent: #6574cf;
  --location-accent-strong: #4f5eaf;
  --location-tint: #eef0ff;
}

.xr-location-row--default {
  --location-accent: #77828d;
  --location-accent-strong: #59636d;
  --location-tint: #f0f3f5;
}

.xr-location-row__name {
  display: flex;
  align-items: center;
  gap: 0.65rem;
  min-width: 0;
}

.xr-location-row__avatar {
  display: grid;
  flex: 0 0 2.25rem;
  width: 2.25rem;
  height: 2.25rem;
  place-items: center;
  border-radius: 0.65rem;
  background: var(--location-tint);
  color: var(--location-accent-strong);
  font-size: 1.1rem;
}

.xr-location-row__name strong {
  overflow: hidden;
  font-size: 0.94rem;
  font-weight: 650;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.xr-location-row__products {
  font-size: 0.9rem;
  font-weight: 650;
}

.xr-location-list__header > :nth-child(2),
.xr-location-row__products {
  justify-self: stretch;
  text-align: center;
}

.xr-location-row__value {
  overflow: hidden;
  color: var(--location-accent-strong);
  font-size: 0.9rem;
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.xr-location-list__header > :nth-child(3),
.xr-location-row__value {
  justify-self: stretch;
  text-align: start;
}

.xr-location-row__arrow {
  color: var(--xr-text-soft);
  font-size: 1.1rem;
}

.xr-location-row:hover,
.xr-location-row:focus-visible {
  background: #f8fbfc;
}

.xr-location-row:focus-visible {
  outline: 2px solid var(--location-accent);
  outline-offset: -2px;
}

.xr-location-row:hover .xr-location-row__arrow,
.xr-location-row:focus-visible .xr-location-row__arrow {
  color: var(--location-accent-strong);
}

@media (max-width: 575.98px) {
  .xr-location-list__header,
  .xr-location-row {
    grid-template-columns: minmax(0, 1fr) 3.25rem minmax(6.25rem, auto) 0.75rem;
    gap: 0.5rem;
  }

  .xr-location-list__header {
    padding-inline: 0.6rem;
    font-size: 0.68rem;
  }

  .xr-location-row {
    min-height: 3.6rem;
    padding-inline: 0.6rem;
  }

  .xr-location-row__avatar {
    flex-basis: 2rem;
    width: 2rem;
    height: 2rem;
  }

  .xr-location-row__name {
    gap: 0.45rem;
  }

  .xr-location-row__name strong,
  .xr-location-row__products,
  .xr-location-row__value {
    font-size: 0.8rem;
  }
}

@media (prefers-reduced-motion: reduce) {
  .xr-location-row {
    transition: none;
  }
}
</style>
