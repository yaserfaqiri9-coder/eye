<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
import StockActionModals from './StockActionModals.vue'
import { expiryTone, stockCategory, stockIcon, stockSearchText, stockStatus, stockStatusLabels, stockStatusTones } from './stockPresentation'
import { useAuthStore } from '@/stores/auth'
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
  salePrice: number
}

interface MovementRow {
  id: number
  productName: string
  lotNumber: string
  kind: string
  quantity: number
  reason: string | null
  createdAt: string
  reversible: boolean
}

const props = defineProps<{ code: string }>()
const auth = useAuthStore()

const tab = ref('stock')
const tabs = [
  { key: 'stock', label: 'موجودی' },
  { key: 'history', label: 'تاریخچه ورود و خروج' },
]
const directions = [
  { key: '', label: 'همه' },
  { key: 'in', label: 'ورود' },
  { key: 'out', label: 'خروج' },
]

const location = ref<LocationSummary | null>(null)
const stock = ref<StockRow[]>([])
const direction = ref('')
const loading = ref(true)
const error = ref('')
const actions = ref<InstanceType<typeof StockActionModals> | null>(null)

const movementList = usePagedList<MovementRow>(() => {
  const params = new URLSearchParams({ location: props.code })
  if (direction.value) params.set('direction', direction.value)
  return `/inventory/movements?${params}`
})

/** Each product in this store: amount, average purchase price, sale price and nearest expiry. */
const search = ref('')
const stockRows = computed(() => {
  const term = stockSearchText(search.value)
  return stock.value
    .map((row) => {
      const shape = { kind: row.kind, category: row.category, name: row.productName, quantity: row.quantity }
      return {
        ...row,
        form: stockCategory({ ...shape, id: row.productId, code: row.productCode, locations: [] }),
        icon: stockIcon(shape),
        status: stockStatus(shape),
        purchasePrice: row.quantity > 0 ? row.value / row.quantity : null,
      }
    })
    .filter((row) => !term || stockSearchText(`${row.productName} ${row.productCode} ${row.form}`).includes(term))
})

function run(task: Promise<void>) {
  task.catch((cause) => (error.value = errorMessage(cause)))
}

function setDirection(key: string) {
  direction.value = key
  run(movementList.first())
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

async function load() {
  error.value = ''
  await Promise.all([
    loadLocation(),
    movementList.load().catch((cause) => (error.value = errorMessage(cause))),
  ])
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

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
  <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

  <UiCard :title="location?.name ?? 'گدام'">
    <template #actions>
      <RouterLink :to="{ name: 'inventory' }" class="btn btn-soft">بازگشت به گدام</RouterLink>
    </template>
    <dl v-if="location" class="xr-facts mb-0">
      <div><dt>اجناس موجود</dt><dd>{{ quantity(location.productCount) }}</dd></div>
      <div><dt>بچ‌ها</dt><dd>{{ quantity(location.batchCount) }}</dd></div>
      <div><dt>ارزش موجودی</dt><dd>{{ money(location.stockValue) }}</dd></div>
      <div><dt>آخرین گردش</dt><dd>{{ formatDateTime(location.lastMovementAt) }}</dd></div>
    </dl>
  </UiCard>

  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های گدام">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
      <input v-if="tab === 'stock'" v-model="search" type="search" class="form-control location-search" placeholder="جستجو با نام یا کد جنس" aria-label="جستجوی نام یا کد جنس" />
      <nav v-else-if="tab === 'history'" class="xr-tabs" aria-label="نوع گردش">
        <button v-for="d in directions" :key="d.key" type="button" class="xr-tab" :class="{ 'xr-tab--active': direction === d.key }" :aria-pressed="direction === d.key" @click="setDirection(d.key)">{{ d.label }}</button>
      </nav>
    </div>

    <div class="xr-card__body">
      <template v-if="tab === 'stock'">
        <UiListSkeleton v-if="loading" />
        <p v-else-if="stockRows.length === 0" class="xr-state">{{ stock.length ? 'هیچ جنسی با این جستجو پیدا نشد.' : 'در این گدام جنسی نیست.' }}</p>
        <div v-else class="xr-table-wrap" role="region" aria-label="موجودی فعلی" tabindex="0">
          <table class="xr-table">
            <thead><tr><th>نام محصول</th><th>نوعیت</th><th>مقدار</th><th>قیمت خرید</th><th>قیمت فروش</th><th>تاریخ انقضا</th><th v-if="auth.can('inventory.edit') || auth.can('inventory.approve')"></th></tr></thead>
            <tbody>
              <tr v-for="row in stockRows" :key="row.productId">
                <td>
                  <div class="location-item">
                    <i :class="row.icon" class="location-item__icon" aria-hidden="true"></i>
                    <div class="d-grid"><strong>{{ row.productName }}</strong><small class="ltr-num text-muted">{{ row.productCode }}</small></div>
                  </div>
                </td>
                <td>{{ row.form }}</td>
                <td class="text-nowrap">
                  <strong>{{ quantity(row.quantity) }}</strong> <span class="text-muted">{{ row.unit }}</span>
                  <span v-if="row.status !== 'available'" class="xr-badge ms-1" :class="`xr-badge--${stockStatusTones[row.status]}`">{{ stockStatusLabels[row.status] }}</span>
                </td>
                <td class="text-nowrap" :title="row.batchCount > 1 ? 'میانگین قیمت خرید بچ‌های موجود' : undefined">{{ row.purchasePrice != null ? money(row.purchasePrice) : '—' }}</td>
                <td class="text-nowrap">{{ row.salePrice ? money(row.salePrice) : '—' }}</td>
                <td class="text-nowrap">
                  <span v-if="row.nearestExpiry" :class="expiryTone(row.nearestExpiry)">{{ formatDate(row.nearestExpiry) }}</span>
                  <span v-else class="text-muted">—</span>
                  <small v-if="row.batchCount > 1" class="d-block text-muted">نزدیک‌ترین از {{ quantity(row.batchCount) }} بچ</small>
                </td>
                <td v-if="auth.can('inventory.edit') || auth.can('inventory.approve')" class="text-nowrap text-end">
                  <div class="location-actions">
                    <button v-if="auth.can('inventory.edit')" type="button" class="btn btn-soft btn-sm" @click="actions?.open('transfer', row.productId, row.productName, code)">انتقال</button>
                    <button v-if="auth.can('inventory.approve')" type="button" class="btn btn-soft btn-sm" @click="actions?.open('adjust', row.productId, row.productName, code)">تعدیل</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else>
        <p v-if="movementList.rows.value.length === 0" class="xr-state">گردشی ثبت نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>زمان</th><th>جهت</th><th>نوع</th><th>جنس</th><th>بچ</th><th>مقدار</th><th>دلیل</th><th></th></tr></thead>
            <tbody>
              <tr v-for="row in movementList.rows.value" :key="row.id">
                <td>{{ formatDateTime(row.createdAt) }}</td>
                <td><span class="xr-badge" :class="row.quantity > 0 ? 'xr-badge--green' : 'xr-badge--red'">{{ row.quantity > 0 ? 'ورود' : 'خروج' }}</span></td>
                <td>{{ movementLabels[row.kind] ?? row.kind }}</td>
                <td>{{ row.productName }}</td>
                <td><span class="ltr-num">{{ row.lotNumber || '—' }}</span></td>
                <td>{{ quantity(Math.abs(row.quantity)) }}</td>
                <td>{{ row.reason ?? '—' }}</td>
                <td><button v-if="canReverse(row)" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(reverse, { row, reason: '', saving: false, error: '' })">برگشت</button></td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="movementList.page.value" :page-count="movementList.pageCount.value" @go="run(movementList.goTo($event))" />
      </template>
    </div>
  </section>
  </div>

  <StockActionModals ref="actions" @done="load" />

  <UiModal v-if="reverse.row" :title="`برگشت ${movementLabels[reverse.row.kind]}: ${reverse.row.productName}`" @close="reverse.row = null">
    <form @submit.prevent="submitReverse">
      <p>گردشی برعکس این مورد ثبت می‌شود و موجودی به حالت قبل برمی‌گردد. سابقه هر دو می‌ماند.</p>
      <label class="form-label" for="rv-reason">دلیل</label>
      <input id="rv-reason" v-model="reverse.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="reverse.error" class="alert alert-danger" role="alert">{{ reverse.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="reverse.saving">ثبت برگشت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.location-search {
  width: 260px;
  max-width: 100%;
}

.location-item {
  display: flex;
  align-items: center;
  gap: 10px;
}

.location-item__icon {
  flex: none;
  display: inline-grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 10px;
  background: var(--xr-tone-primary-bg);
  color: var(--xr-primary);
  font-size: 17px;
}

.location-actions {
  display: inline-flex;
  gap: 6px;
}
</style>
