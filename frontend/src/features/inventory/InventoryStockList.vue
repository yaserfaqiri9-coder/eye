<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import UiFilterBar from '@/components/UiFilterBar.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import UiPager from '@/components/UiPager.vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import { formatDate } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { expiryDays, expiryTone, stockCategory, stockIcon, stockSearchText, stockStatus, stockStatusLabels, stockStatusTones, type InventoryListRow } from './stockPresentation'

/** The product list with search and filters, built from the same table, badges and pager as every other list. */
const props = defineProps<{
  rows: InventoryListRow[]
  loading?: boolean
  error?: string
  /** Rarely used row actions (edit, delete), kept in the row menu. */
  menu?: (row: InventoryListRow) => { label: string; icon?: string; danger?: boolean; onSelect: () => void }[]
}>()

const PAGE_SIZE = 20
const search = ref('')
const status = ref('')
// Stock level and expiry share one filter, so the bar stays short.
const statusOptions: Record<string, string> = { ...stockStatusLabels, expiring: 'انقضا تا 90 روز', expired: 'منقضی‌شده' }
function matchesStatus(row: InventoryListRow) {
  if (!status.value) return true
  const days = expiryDays(row.nearestExpiry)
  if (status.value === 'expired') return days != null && days < 0
  if (status.value === 'expiring') return days != null && days >= 0 && days <= 90
  return stockStatus(row) === status.value
}
const category = ref('')
const locationFilter = ref('')
const page = ref(1)

const categories = computed(() => [...new Set(props.rows.map(stockCategory))].sort())
const locations = computed(() => [...new Map(props.rows.flatMap((r) => r.locations).map((l) => [l.code, l])).values()])
const filtered = computed(() => {
  const term = stockSearchText(search.value)
  return props.rows.filter((row) =>
    (!term || stockSearchText(`${row.name} ${row.code} ${stockCategory(row)} ${row.genericName ?? ''}`).includes(term))
    && matchesStatus(row)
    && (!category.value || stockCategory(row) === category.value)
    && (!locationFilter.value || row.locations.some((l) => l.code === locationFilter.value)))
})
const pageCount = computed(() => Math.max(1, Math.ceil(filtered.value.length / PAGE_SIZE)))
const visible = computed(() => filtered.value.slice((page.value - 1) * PAGE_SIZE, page.value * PAGE_SIZE))

watch([search, status, category, locationFilter], () => (page.value = 1))
watch(pageCount, (count) => (page.value = Math.min(page.value, count)))
</script>

<template>
  <UiFilterBar
    v-model:search="search"
    flat
    search-label="جستجوی نام یا کد جنس"
    search-placeholder="جستجو با نام یا کد جنس"
    :count="`${quantity(filtered.length)} جنس`"
  >
    <select v-model="status" class="form-select" aria-label="فیلتر وضعیت">
      <option value="">همه وضعیت‌ها</option>
      <option v-for="(label, key) in statusOptions" :key="key" :value="key">{{ label }}</option>
    </select>
    <select v-model="category" class="form-select" aria-label="فیلتر نوعیت">
      <option value="">همه نوعیت‌ها</option>
      <option v-for="item in categories" :key="item">{{ item }}</option>
    </select>
    <select v-model="locationFilter" class="form-select" aria-label="فیلتر محل">
      <option value="">همه محل‌ها</option>
      <option v-for="item in locations" :key="item.code" :value="item.code">{{ item.name }}</option>
    </select>
  </UiFilterBar>

  <div v-if="error" class="alert alert-warning" role="alert">{{ error }}</div>
  <UiListSkeleton v-if="loading" />
  <p v-else-if="!visible.length" class="xr-state">{{ rows.length ? 'هیچ جنسی با این جستجو پیدا نشد.' : 'هنوز جنسی ثبت نشده است.' }}</p>
  <div v-else class="xr-table-wrap" role="region" aria-label="فهرست اجناس" tabindex="0">
    <table class="xr-table">
      <thead>
        <tr><th>نام محصول</th><th>نوعیت</th><th>مقدار</th><th>قیمت خرید</th><th>قیمت فروش</th><th>تاریخ انقضا</th><th v-if="$slots.actions || menu"></th></tr>
      </thead>
      <tbody>
        <tr v-for="row in visible" :key="row.id">
          <td>
            <div class="stock-item">
              <i :class="stockIcon(row)" class="stock-item__icon" aria-hidden="true"></i>
              <div class="stock-item__text">
                <slot name="name" :row="row"><span class="stock-item__name">{{ row.name }}</span></slot>
                <span class="stock-item__meta">
                  <span class="ltr-num">{{ row.code }}</span>
                  <span v-if="row.genericName" dir="ltr">({{ row.genericName }})</span>
                  <span v-if="row.isActive === false" class="xr-badge xr-badge--red">غیرفعال</span>
                </span>
              </div>
            </div>
          </td>
          <td>{{ stockCategory(row) }}</td>
          <td class="text-nowrap">
            <strong class="ltr-num">{{ quantity(row.quantity) }}</strong> <span class="text-muted">{{ row.unit }}</span>
            <span v-if="stockStatus(row) !== 'available'" class="xr-badge ms-1" :class="`xr-badge--${stockStatusTones[stockStatus(row)]}`">{{ stockStatusLabels[stockStatus(row)] }}</span>
          </td>
          <td class="text-nowrap" :title="(row.batchCount ?? 0) > 1 ? 'میانگین قیمت خرید بچ‌های موجود' : undefined">{{ row.purchasePrice != null ? money(row.purchasePrice) : '—' }}</td>
          <td class="text-nowrap">{{ row.salePrice ? money(row.salePrice) : '—' }}</td>
          <td class="text-nowrap">
            <span v-if="row.nearestExpiry" :class="expiryTone(row.nearestExpiry)">{{ formatDate(row.nearestExpiry) }}</span>
            <span v-else class="text-muted">—</span>
            <small v-if="(row.batchCount ?? 0) > 1" class="d-block text-muted">نزدیک‌ترین از {{ quantity(row.batchCount!) }} بچ</small>
          </td>
          <td v-if="$slots.actions || menu" class="text-nowrap text-end">
            <div class="stock-actions">
              <slot name="actions" :row="row" />
              <UiRowMenu v-if="menu" :label="`عملیات ${row.name}`" :items="menu(row)" />
            </div>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
  <UiPager :page="page" :page-count="pageCount" @go="page = $event" />
</template>

<style scoped>
.stock-item {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.stock-item__icon {
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

.stock-item__text {
  display: grid;
  min-width: 0;
}

.stock-item__name {
  font-weight: 600;
}

.stock-item__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--xr-text-soft);
  font-size: 12px;
}

.stock-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
</style>
