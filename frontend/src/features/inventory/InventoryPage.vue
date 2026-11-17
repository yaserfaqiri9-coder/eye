<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import InventoryStockList from './InventoryStockList.vue'
import StockActionModals from './StockActionModals.vue'
import type { InventoryListRow } from './stockPresentation'
import UiModal from '@/components/UiModal.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { removeRecord } from '@/utils/removeRecord'
import { drugFormChoices, loadStoredDrugForms, productGroupLabels, stockUnits } from './drugForms'
import { doseOptions } from '@/features/clinical/doctorShortcuts'

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
  /** Active ingredients, as the old prescriptions wrote them in brackets. */
  genericName?: string | null
  /** The dose doctors usually write for the drug ("1x3"). */
  defaultDose?: string | null
}

/** One product's stock in one store, as the store detail reports it. */
interface LocationStock {
  productId: number
  quantity: number
  value: number
  nearestExpiry: string | null
  batchCount: number
}

interface ProductStock {
  locations: { code: string; name: string }[]
  quantity: number
  value: number
  nearestExpiry: string | null
  batchCount: number
}

// The product group (دوا، عمومی …). The drug form is a separate field, kept in the product's category.
const kindLabels = productGroupLabels
const storedDrugForms = ref<string[]>([])

const auth = useAuthStore()
// Stores open first; products remain one tab away. Purchases and stock history keep their existing pages.
const tab = ref('locations')
const tabs = [
  { key: 'locations', label: 'گدام‌ها' },
  { key: 'products', label: 'محصولات' },
]
const stockByProduct = ref(new Map<number, ProductStock>())
const locationError = ref('')
const locations = ref<LocationRow[]>([])
const products = ref<ProductRow[]>([])
const loading = ref(true)
const error = ref('')
const notice = ref('')
const actions = ref<InstanceType<typeof StockActionModals> | null>(null)

const productMenu = (row: InventoryListRow) => {
  const item = productById(row.id)
  return [
    { label: 'ویرایش جنس', icon: 'ri-edit-line', onSelect: () => openProduct(item) },
    { label: 'حذف جنس', icon: 'ri-delete-bin-line', danger: true, onSelect: () => removeProduct(item) },
  ]
}

// Read every server page so local search, filters and pagination include the entire catalogue.
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

// The batch endpoint is capped, so each store's full stock gives the places, purchase price and expiry of every product.
let locationRequest = 0
async function loadStock(rows: LocationRow[]) {
  const request = ++locationRequest
  const result = await Promise.allSettled(rows.map(async location => ({
    location,
    detail: await api<{ stock: LocationStock[] }>(`/inventory/locations/${encodeURIComponent(location.code)}`),
  })))
  if (request !== locationRequest) return
  const map = new Map<number, ProductStock>()
  locationError.value = result.some(r => r.status === 'rejected') ? 'موجودی همه گدام‌ها کامل بارگذاری نشد؛ دوباره تلاش کنید.' : ''
  for (const item of result) {
    if (item.status !== 'fulfilled') continue
    const { location, detail } = item.value
    for (const row of detail.stock) {
      const entry = map.get(row.productId) ?? { locations: [], quantity: 0, value: 0, nearestExpiry: null, batchCount: 0 }
      entry.locations.push({ code: location.code, name: location.name })
      entry.quantity += row.quantity
      entry.value += row.value
      entry.batchCount += row.batchCount
      if (row.nearestExpiry && (!entry.nearestExpiry || row.nearestExpiry < entry.nearestExpiry)) entry.nearestExpiry = row.nearestExpiry
      map.set(row.productId, entry)
    }
  }
  stockByProduct.value = map
}

const listRows = computed<InventoryListRow[]>(() => products.value.map(row => {
  const stock = stockByProduct.value.get(row.id)
  return {
    id: row.id, name: row.name, code: row.code, kind: row.kind, category: row.category, genericName: row.genericName,
    quantity: row.onHand, unit: row.unit, reorderLevel: row.reorderLevel, isActive: row.isActive, salePrice: row.salePrice,
    locations: stock?.locations ?? [],
    purchasePrice: stock && stock.quantity > 0 ? stock.value / stock.quantity : null,
    nearestExpiry: stock?.nearestExpiry ?? null,
    batchCount: stock?.batchCount ?? 0,
  }
}))
const productById = (id: number) => products.value.find(row => row.id === id)!

async function load() {
  error.value = ''
  try {
    await Promise.all([
      loadProducts(),
      api<LocationRow[]>('/inventory/locations').then((rows) => {
        locations.value = rows
        return loadStock(rows)
      }),
    ])
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const product = reactive({ open: false, id: null as number | null, version: null as number | null, code: '', name: '', kind: 'Drug', category: '', genericName: '', defaultDose: '', unit: 'عدد', reorderLevel: 0, salePrice: 0, isActive: true, saving: false, error: '' })

function openProduct(row?: ProductRow) {
  Object.assign(product, {
    open: true, id: row?.id ?? null, version: row?.version ?? null, code: row?.code ?? '', name: row?.name ?? '', kind: row?.kind ?? 'Drug',
    category: row?.category ?? '', genericName: row?.genericName ?? '', defaultDose: row?.defaultDose ?? '',
    unit: row?.unit ?? 'عدد', reorderLevel: row?.reorderLevel ?? 0, salePrice: row?.salePrice ?? 0, isActive: row?.isActive ?? true, saving: false, error: '',
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
  const isDrug = product.kind === 'Drug'
  const body = {
    name: product.name, kind: product.kind, category: product.category, unit: product.unit, reorderLevel: product.reorderLevel, salePrice: product.salePrice, isActive: product.isActive, version: product.version,
    genericName: isDrug ? product.genericName : null, defaultDose: isDrug ? product.defaultDose : null,
  }
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

onBeforeUnmount(() => { productsController?.abort(); locationRequest++ })

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success mb-0" role="status">{{ notice }}</div>

    <section class="xr-card">
      <div class="xr-card__header flex-wrap">
        <nav class="xr-tabs" role="tablist" aria-label="بخش‌های گدام">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
        </nav>
        <div v-if="auth.can('inventory.create')" class="d-flex flex-wrap gap-2">
          <button type="button" class="btn btn-primary" @click="openProduct()">ثبت محصول</button>
        </div>
      </div>

      <div class="xr-card__body">
        <UiListSkeleton v-if="loading" />

        <InventoryStockList v-else-if="tab === 'products'" :rows="listRows" :error="locationError" :menu="auth.can('inventory.edit') ? productMenu : undefined">
          <template v-if="auth.can('inventory.edit')" #name="{ row }">
            <button type="button" class="inventory-name" @click="openProduct(productById(row.id))">{{ row.name }}</button>
          </template>
          <template v-if="auth.can('inventory.edit') || auth.can('inventory.approve')" #actions="{ row }">
            <template v-if="row.quantity > 0">
              <button v-if="auth.can('inventory.edit')" type="button" class="btn btn-soft btn-sm" @click="actions?.open('transfer', row.id, row.name)">انتقال</button>
              <button v-if="auth.can('inventory.approve')" type="button" class="btn btn-soft btn-sm" @click="actions?.open('adjust', row.id, row.name)">تعدیل</button>
            </template>
          </template>
        </InventoryStockList>

        <template v-else>
          <p v-if="locations.length === 0" class="xr-state">گدامی ثبت نشده است.</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>گدام</th><th>تعداد جنس</th><th>بچ‌ها</th><th>ارزش موجودی</th><th>آخرین گردش</th><th></th></tr></thead>
              <tbody>
                <tr v-for="row in locations" :key="row.code">
                  <td><RouterLink :to="{ name: 'inventory-location', params: { code: row.code } }" class="fw-semibold">{{ row.name }}</RouterLink></td>
                  <td>{{ quantity(row.productCount) }}</td>
                  <td>{{ quantity(row.batchCount) }}</td>
                  <td>{{ money(row.stockValue) }}</td>
                  <td>{{ formatDateTime(row.lastMovementAt) }}</td>
                  <td><RouterLink :to="{ name: 'inventory-location', params: { code: row.code } }" class="btn btn-soft btn-sm">جزئیات</RouterLink></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>
    </section>
  </div>

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
        <template v-if="product.kind === 'Drug'">
          <div class="col-6">
            <label class="form-label" for="pr-dose">دوز معمول</label>
            <input id="pr-dose" v-model="product.defaultDose" class="form-control" dir="ltr" maxlength="20" list="pr-dose-options" placeholder="1x3" />
            <datalist id="pr-dose-options"><option v-for="dose in doseOptions" :key="dose" :value="dose" /></datalist>
          </div>
          <div class="col-12">
            <label class="form-label" for="pr-generic">نام جنریک (ماده مؤثره)</label>
            <input id="pr-generic" v-model="product.genericName" class="form-control" dir="ltr" maxlength="200" placeholder="Tobramycin + Dexamethasone" />
          </div>
        </template>
        <div class="col-6">
          <label class="form-label" for="pr-unit">واحد موجودی</label>
          <input id="pr-unit" v-model="product.unit" class="form-control" maxlength="30" list="pr-unit-options" required />
          <datalist id="pr-unit-options"><option v-for="unit in stockUnits" :key="unit" :value="unit" /></datalist>
        </div>
        <div class="col-6"><label class="form-label" for="pr-reorder">حد کمبود</label><input id="pr-reorder" v-model.number="product.reorderLevel" class="form-control" type="number" min="0" step="any" /></div>
        <div class="col-6"><label class="form-label" for="pr-price">قیمت فروش</label><input id="pr-price" v-model.number="product.salePrice" class="form-control" type="number" min="0" step="0.01" /></div>
      </div>
      <button
        type="button"
        class="btn mb-3"
        :class="product.isActive ? 'btn-soft-danger' : 'btn-primary'"
        :aria-pressed="product.isActive"
        @click="product.isActive = !product.isActive"
      >{{ product.isActive ? 'غیرفعال‌کردن محصول' : 'فعال‌کردن محصول' }}</button>
      <div v-if="product.error" class="alert alert-danger" role="alert">{{ product.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="product.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <StockActionModals ref="actions" @done="load" />
</template>

<style scoped>
.inventory-name {
  padding: 0;
  border: 0;
  background: none;
  color: var(--xr-text);
  font: inherit;
  font-weight: 600;
  text-align: start;
}

.inventory-name:hover {
  color: var(--xr-primary);
  text-decoration: underline;
}
</style>
