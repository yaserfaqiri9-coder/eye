<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiPager from '@/components/UiPager.vue'
import InventoryStockList from './InventoryStockList.vue'
import type { InventoryListRow } from './stockPresentation'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { movementLabels } from './labels'

interface LocationSummary {
  code: string
  name: string
  productCount: number
  batchCount: number
  stockValue: number
  lastMovementAt: string | null
}

interface StockRow {
  productId: number
  productCode: string
  productName: string
  unit: string
  quantity: number
  value: number
  nearestExpiry: string | null
  batchCount: number
  kind: string
  category: string | null
}

interface MovementRow {
  id: number
  productName: string
  lotNumber: string
  kind: string
  quantity: number
  reason: string | null
  createdAt: string
}

const props = defineProps<{ code: string }>()

const pageSize = 25
const directions = [
  { key: '', label: 'همه' },
  { key: 'in', label: 'ورود' },
  { key: 'out', label: 'خروج' },
]

const location = ref<LocationSummary | null>(null)
const stock = ref<StockRow[]>([])
const movements = ref<MovementRow[]>([])
const total = ref(0)
const page = ref(1)
const direction = ref('')
const loading = ref(true)
const loadingMovements = ref(true)
const error = ref('')

const pageCount = computed(() => Math.ceil(total.value / pageSize))
const listRows = computed<InventoryListRow[]>(() => stock.value.map(row => ({
  id: row.productId, name: row.productName, code: row.productCode, kind: row.kind, category: row.category,
  quantity: row.quantity, unit: row.unit,
  locations: [{ code: props.code, name: location.value?.name ?? props.code }],
})))

let movementRequest = 0

async function loadMovements() {
  const request = ++movementRequest
  error.value = ''
  loadingMovements.value = true
  try {
    const params = new URLSearchParams({ location: props.code, page: String(page.value), pageSize: String(pageSize) })
    if (direction.value) params.set('direction', direction.value)
    const result = await api<{ items: MovementRow[]; total: number }>(`/inventory/movements?${params}`)
    if (request !== movementRequest) return
    movements.value = result.items
    total.value = result.total
  } catch (cause) {
    if (request === movementRequest) error.value = errorMessage(cause)
  } finally {
    if (request === movementRequest) loadingMovements.value = false
  }
}

async function loadLocation() {
  try {
    const detail = await api<{ location: LocationSummary; stock: StockRow[] }>(`/inventory/locations/${encodeURIComponent(props.code)}`)
    location.value = detail.location
    stock.value = detail.stock
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function setDirection(key: string) {
  direction.value = key
  page.value = 1
  void loadMovements()
}

function goTo(next: number) {
  page.value = next
  void loadMovements()
}

async function load() {
  await Promise.all([loadMovements(), loadLocation()])
}

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-flex justify-content-between align-items-center flex-wrap gap-2 mb-3">
    <h1 class="h4 mb-0">{{ location?.name ?? 'گدام' }}</h1>
    <RouterLink :to="{ name: 'inventory' }" class="btn btn-soft">بازگشت به گدام</RouterLink>
  </div>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>

  <dl v-if="location" class="xr-card xr-location-stats">
    <div><dt>اجناس موجود</dt><dd>{{ quantity(location.productCount) }}</dd></div>
    <div><dt>بچ‌ها</dt><dd>{{ quantity(location.batchCount) }}</dd></div>
    <div><dt>ارزش موجودی</dt><dd>{{ money(location.stockValue) }}</dd></div>
    <div><dt>آخرین گردش</dt><dd>{{ location.lastMovementAt ? formatDateTime(location.lastMovementAt) : '—' }}</dd></div>
  </dl>

  <InventoryStockList :title="`موجودی ${location?.name ?? 'گدام'}`" :rows="listRows" :loading="loading" :location="code" />
  <details class="location-stock-details">
    <summary>جزئیات بچ‌ها، انقضا و ارزش موجودی</summary>
    <div class="xr-table-wrap"><table class="xr-table"><thead><tr><th>جنس</th><th>بچ‌ها</th><th>نزدیک‌ترین انقضا</th><th>ارزش</th></tr></thead><tbody><tr v-for="row in stock" :key="row.productId"><td>{{ row.productName }}</td><td>{{ quantity(row.batchCount) }}</td><td>{{ row.nearestExpiry ? formatDate(row.nearestExpiry) : '—' }}</td><td>{{ money(row.value) }}</td></tr></tbody></table></div>
  </details>

  <section class="xr-card" aria-labelledby="location-history">
    <div class="xr-card__header flex-wrap">
      <h2 id="location-history" class="h6 mb-0">تاریخچه ورود و خروج</h2>
      <nav class="xr-tabs" aria-label="نوع گردش">
        <button v-for="d in directions" :key="d.key" type="button" class="xr-tab" :class="{ 'xr-tab--active': direction === d.key }" :aria-pressed="direction === d.key" @click="setDirection(d.key)">{{ d.label }}</button>
      </nav>
    </div>
    <div class="xr-card__body">
      <p v-if="loadingMovements && movements.length === 0" class="xr-state" role="status">در حال بارگذاری...</p>
      <p v-else-if="movements.length === 0" class="xr-state">گردشی ثبت نشده است.</p>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>زمان</th><th>جهت</th><th>نوع</th><th>جنس</th><th>بچ</th><th>مقدار</th><th>دلیل</th></tr></thead>
          <tbody>
            <tr v-for="row in movements" :key="row.id">
              <td>{{ formatDateTime(row.createdAt) }}</td>
              <td><span class="xr-badge" :class="row.quantity > 0 ? 'xr-badge--green' : 'xr-badge--red'">{{ row.quantity > 0 ? 'ورود' : 'خروج' }}</span></td>
              <td>{{ movementLabels[row.kind] ?? row.kind }}</td>
              <td>{{ row.productName }}</td>
              <td><span class="ltr-num">{{ row.lotNumber || '—' }}</span></td>
              <td>{{ quantity(Math.abs(row.quantity)) }}</td>
              <td>{{ row.reason ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <UiPager :page="page" :page-count="pageCount" @go="goTo" />
    </div>
  </section>
</template>

<style scoped>
.location-stock-details { margin-block: 20px; padding: 16px; border: 1px solid #eef0f4; border-radius: 5px; background: #fff; }
.location-stock-details summary { cursor: pointer; font-size: 12px; color: #5a6476; }
.xr-location-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
  gap: 1rem;
  padding: 1rem 1.25rem;
  margin-bottom: 1rem;
}

.xr-location-stats dt {
  font-weight: 400;
  font-size: 0.85rem;
  color: var(--xr-text-soft);
}

.xr-location-stats dd {
  margin: 0.25rem 0 0;
  font-weight: 600;
  color: var(--xr-text);
}

</style>
