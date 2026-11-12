<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { stockCategory, stockIcon, stockNumber, stockSearchText, stockStatus, stockStatusLabels, type InventoryListRow, type StockStatus } from './stockPresentation'
const props = withDefaults(defineProps<{ rows: InventoryListRow[]; title?: string; loading?: boolean; location?: string; error?: string }>(), { title: 'مدیریت گدام و موجودی', location: '', error: '' })
const search = ref('')
const filterOpen = ref(false)
const status = ref<StockStatus | ''>('')
const category = ref('')
const locationFilter = ref('')
const page = ref(1)
const pageSize = ref(8)
const categories = computed(() => [...new Set(props.rows.map(stockCategory))].sort())
const locations = computed(() => [...new Map(props.rows.flatMap(r => r.locations).map(l => [l.code, l])).values()])
const filtersActive = computed(() => !!(status.value || category.value || locationFilter.value))
const filtered = computed(() => props.rows.filter(row => {
  const term = stockSearchText(search.value)
  return (!term || stockSearchText(`${row.name} ${row.code} ${stockCategory(row)}`).includes(term))
    && (!status.value || stockStatus(row) === status.value)
    && (!category.value || stockCategory(row) === category.value)
    && (!locationFilter.value || row.locations.some(l => l.code === locationFilter.value))
}))
const pageCount = computed(() => Math.max(1, Math.ceil(filtered.value.length / pageSize.value)))
const visible = computed(() => filtered.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
const pageNumbers = computed(() => {
  const start = Math.max(1, Math.min(page.value - 2, pageCount.value - 4))
  return Array.from({ length: Math.min(5, pageCount.value) }, (_, i) => start + i)
})
watch([search, status, category, locationFilter, pageSize], () => { page.value = 1 })
watch(pageCount, count => { page.value = Math.min(page.value, count) })
function resetFilters() { status.value = ''; category.value = ''; locationFilter.value = '' }
</script>
<template>
  <section class="inventory-list" aria-labelledby="inventory-list-title" :aria-busy="loading">
    <header class="inventory-list__header">
      <h1 id="inventory-list-title">{{ title }}</h1>
      <div class="inventory-list__tools">
        <label class="inventory-search"><i class="ri-search-line" aria-hidden="true"></i><input v-model="search" type="search" placeholder="جستجوی اجناس..." aria-label="جستجوی نام یا کد جنس" /></label>
        <button type="button" class="inventory-filter-button" :class="{ 'is-active': filtersActive }" :aria-expanded="filterOpen" aria-controls="inventory-filters" aria-label="فیلتر موجودی" @click="filterOpen = !filterOpen"><i class="ri-filter-3-line" aria-hidden="true"></i></button>
      </div>
    </header>
    <div v-if="filterOpen" id="inventory-filters" class="inventory-filters">
      <label>وضعیت<select v-model="status" aria-label="فیلتر وضعیت"><option value="">همه وضعیت‌ها</option><option v-for="(label, key) in stockStatusLabels" :key="key" :value="key">{{ label }}</option></select></label>
      <label>دسته‌بندی<select v-model="category" aria-label="فیلتر دسته‌بندی"><option value="">همه دسته‌ها</option><option v-for="item in categories" :key="item">{{ item }}</option></select></label>
      <label v-if="!location">محل<select v-model="locationFilter" aria-label="فیلتر محل"><option value="">همه محل‌ها</option><option v-for="item in locations" :key="item.code" :value="item.code">{{ item.name }}</option></select></label>
      <button type="button" class="inventory-reset" @click="resetFilters">پاک‌کردن فیلترها</button>
    </div>
    <div v-if="error" class="inventory-list__message" role="alert">{{ error }}</div>
    <div class="inventory-list__scroll" role="region" aria-label="جدول موجودی؛ در صفحه کوچک به‌صورت افقی پیمایش کنید" tabindex="0">
      <table class="inventory-table" :class="{ 'inventory-table--actions': !!$slots.actions }">
        <caption class="visually-hidden">{{ title }}؛ نام، دسته‌بندی، موجودی، وضعیت و محل نگهداری</caption>
        <colgroup><col class="inventory-col-item" /><col class="inventory-col-category" /><col class="inventory-col-stock" /><col class="inventory-col-status" /><col class="inventory-col-location" /><col v-if="$slots.actions" class="inventory-col-actions" /></colgroup>
        <thead><tr><th scope="col">جنس</th><th scope="col">دسته‌بندی</th><th scope="col" class="inventory-table__number">موجودی</th><th scope="col">وضعیت</th><th scope="col">محل</th><th v-if="$slots.actions" scope="col"><span class="visually-hidden">عملیات</span></th></tr></thead>
        <tbody>
          <tr v-if="loading"><td :colspan="$slots.actions ? 6 : 5" class="inventory-table__empty" role="status">در حال بارگذاری موجودی...</td></tr>
          <tr v-else-if="!visible.length"><td :colspan="$slots.actions ? 6 : 5" class="inventory-table__empty" role="status">{{ rows.length ? 'هیچ جنسی با این جستجو و فیلترها پیدا نشد.' : 'هنوز جنسی ثبت نشده است.' }}</td></tr>
          <template v-else><tr v-for="row in visible" :key="row.id">
            <td><div class="inventory-item"><i :class="stockIcon(row)" class="inventory-item__icon" aria-hidden="true"></i><div class="inventory-item__identity"><slot name="name" :row="row"><span class="inventory-item__name">{{ row.name }}</span></slot><span v-if="row.isActive === false" class="inventory-item__inactive">غیرفعال</span></div></div></td>
            <td>{{ stockCategory(row) }}</td>
            <td class="inventory-table__number"><span class="inventory-number" dir="ltr" :title="row.unit" :aria-label="`${stockNumber(row.quantity)} ${row.unit ?? ''}`">{{ stockNumber(row.quantity) }}</span></td>
            <td><span class="inventory-status" :class="`inventory-status--${stockStatus(row)}`">{{ stockStatusLabels[stockStatus(row)] }}</span></td>
            <td><div class="inventory-locations"><template v-if="row.locations.length"><RouterLink v-for="item in row.locations" :key="item.code" :to="{ name: 'inventory-location', params: { code: item.code } }">{{ item.name }}</RouterLink></template><span v-else>—</span></div></td>
            <td v-if="$slots.actions" class="inventory-table__actions"><details v-if="$slots.actions" class="inventory-row-menu"><summary :aria-label="`عملیات ${row.name}`"><i class="ri-more-2-fill" aria-hidden="true"></i></summary><div><slot name="actions" :row="row" /></div></details></td>
          </tr></template>
        </tbody>
      </table>
    </div>
    <footer class="inventory-list__footer">
      <span class="inventory-list__count" aria-live="polite">{{ stockNumber(filtered.length) }} جنس</span>
      <nav class="inventory-pagination" aria-label="صفحه‌بندی موجودی">
        <button type="button" :disabled="page === 1 || loading" aria-label="صفحه قبلی" @click="page--"><i class="ri-arrow-right-s-line" aria-hidden="true"></i></button>
        <button v-for="number in pageNumbers" :key="number" type="button" :aria-label="`صفحه ${number}`" :aria-current="page === number ? 'page' : undefined" :class="{ 'is-current': page === number }" :disabled="loading" @click="page = number">{{ number }}</button>
        <button type="button" :disabled="page >= pageCount || loading" aria-label="صفحه بعدی" @click="page++"><i class="ri-arrow-left-s-line" aria-hidden="true"></i></button>
      </nav>
      <label class="inventory-page-size"><select v-model.number="pageSize" aria-label="تعداد اجناس در صفحه"><option :value="8">8</option><option :value="16">16</option><option :value="24">24</option></select><span>جنس در صفحه</span></label>
    </footer>
  </section>
</template>
<style scoped>
.inventory-list { --inv-ink: #202936; --inv-muted: #707887; --inv-line: #f0f1f4; background: #fff; color: var(--inv-ink); border: 0; border-radius: 0; min-width: 0; font-size: 14px; }
.inventory-list__header { display: flex; align-items: center; justify-content: space-between; gap: 24px; padding: 24px 24px 26px; }
.inventory-list__header h1 { font-size: 20px; line-height: 1.5; font-weight: 700; letter-spacing: -.35px; color: #20252e; }
.inventory-list__tools { display: flex; align-items: center; gap: 14px; }
.inventory-search { display: flex; align-items: center; gap: 10px; height: 36px; width: 240px; padding: 0 12px; color: #8a91a0; border: 1px solid #f4f4f7; border-radius: 4px; }
.inventory-search input { border: 0; outline: none; background: transparent; min-width: 0; width: 100%; font: inherit; font-size: 12px; color: var(--inv-ink); }
.inventory-search input::placeholder { color: #8a91a0; }
.inventory-search:focus-within { outline: 2px solid #315bd4; outline-offset: 2px; }
.inventory-search i { font-size: 17px; }
.inventory-filter-button { display: grid; place-items: center; width: 35px; height: 35px; border: 1px solid #f1f2f6; border-radius: 4px; background: #fff; color: #707887; font-size: 17px; }
.inventory-filter-button.is-active { color: #315bd4; background: #eef2ff; border-color: #cdd8ff; }
.inventory-filters { display: flex; align-items: end; flex-wrap: wrap; gap: 16px; padding: 0 24px 20px; font-size: 12px; }
.inventory-filters label { display: grid; gap: 6px; }
.inventory-filters select { background: #fff; border: 1px solid #e5e8ef; border-radius: 4px; padding: 7px 10px; min-width: 145px; color: var(--inv-ink); }
.inventory-reset { border: 0; background: none; color: #315bd4; font: inherit; padding: 8px; }
.inventory-list__scroll { position: relative; overflow-x: auto; min-width: 0; }
.inventory-table { table-layout: fixed; width: 100%; min-width: 720px; border-collapse: collapse; text-align: start; line-height: 1.5; }
.inventory-col-item { width: 32%; }.inventory-col-category { width: 20%; }.inventory-col-stock { width: 20%; }.inventory-col-status { width: 15%; }.inventory-col-location { width: 13%; }
.inventory-table--actions .inventory-col-item { width: 30%; }.inventory-table--actions .inventory-col-category { width: 18%; }.inventory-table--actions .inventory-col-stock { width: 18%; }.inventory-table--actions .inventory-col-actions { width: 6%; }

.inventory-table th { font-size: 13px; font-weight: 600; color: #303540; background: #f8f9fb; height: 44px; padding: 10px 16px; border: 0; text-align: start; }
.inventory-table td { height: 64px; padding: 12px 16px; border-bottom: 1px solid #f5f6f8; color: #555e6b; vertical-align: middle; overflow-wrap: anywhere; }
.inventory-table th:first-child, .inventory-table td:first-child { padding-inline-start: 24px; }
.inventory-table th:last-child, .inventory-table td:last-child { padding-inline-end: 24px; }
.inventory-table tbody tr:last-child td { border-bottom: 0; }
.inventory-table tbody tr:hover td { background: #fcfcfe; }
.inventory-table__number { text-align: end !important; padding-inline-end: 24px !important; }
.inventory-number, .inventory-code, .inventory-pagination button, .inventory-page-size select, .inventory-list__count { font-family: 'Poppins', sans-serif; font-variant-numeric: tabular-nums; }
.inventory-number { white-space: nowrap; color: #252a34; font-size: 16px; font-weight: 500; }

.inventory-item { display: flex; align-items: center; gap: 13px; min-width: 0; }
.inventory-item__icon { flex: 0 0 26px; width: 26px; font-size: 26px; line-height: 30px; color: #29343d; filter: drop-shadow(0 1px 1px #0002); }
.inventory-item__identity { min-width: 0; }
.inventory-item__name, .inventory-item__identity :deep(button) { font: inherit; font-weight: 500; color: #242b34; line-height: 1.7; }
.inventory-item__identity :deep(button) { text-align: start; border: 0; padding: 0; background: none; }
.inventory-item__inactive { display: block; color: #7b8290; font-size: 10px; }
.inventory-status { display: inline-flex; align-items: center; justify-content: center; white-space: nowrap; font-size: 11px; font-weight: 500; line-height: 18px; padding: 4px 9px; border-radius: 4px; }
.inventory-status--available { background: #e5f5ed; color: #24765c; }.inventory-status--low { background: #fff1de; color: #996019; }.inventory-status--out { background: #fce7eb; color: #a74054; }
.inventory-locations { display: flex; flex-direction: column; gap: 2px; font-size: 11px; }.inventory-locations a { color: #727987; text-decoration: none; }.inventory-locations a:hover { color: #315bd4; text-decoration: underline; }
.inventory-list__footer { display: flex; align-items: center; justify-content: end; gap: 24px; padding: 18px 24px 22px; font-size: 11px; color: #858b97; }
.inventory-list__count { margin-inline-end: auto; font-size: 11px; }
.inventory-pagination { display: flex; align-items: center; gap: 4px; }.inventory-pagination button { display: grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 3px; background: #fff; color: #767d89; font-size: 11px; }.inventory-pagination button.is-current { background: #eef1fb; color: #3856b4; }.inventory-pagination button:disabled { opacity: .35; cursor: default; }
.inventory-page-size { display: flex; align-items: center; gap: 5px; }.inventory-page-size select { color: #737c8c; font-size: 11px; border: 0; background: #fff; padding: 3px; }
.inventory-table__actions { padding-inline: 4px !important; text-align: center; }.inventory-row-menu { position: relative; width: 32px; margin-inline: auto; }.inventory-row-menu summary { display: grid; place-items: center; width: 32px; height: 32px; font-size: 18px; cursor: pointer; color: #9299a5; list-style: none; }.inventory-row-menu summary::-webkit-details-marker { display: none; }.inventory-row-menu > div { position: absolute; z-index: 5; top: 100%; inset-inline-end: 0; width: 140px; padding: 6px; border: 1px solid #e8ebf0; border-radius: 5px; box-shadow: 0 5px 20px #0e203516; background: #fff; }.inventory-row-menu :deep(button) { display: block; width: 100%; padding: 7px 10px; border: 0; background: #fff; text-align: start; font: inherit; color: #303744; }.inventory-row-menu :deep(button:hover) { background: #f2f5fc; }
.inventory-table__empty { text-align: center; height: 180px !important; color: #858b97 !important; }.inventory-list__message { padding: 12px 24px; color: #b73748; }
.inventory-list button:focus-visible, .inventory-list select:focus-visible, .inventory-list a:focus-visible, .inventory-row-menu summary:focus-visible { outline: 2px solid #315bd4; outline-offset: 3px; }
@media (max-width: 600px) { .inventory-list__header { flex-direction: column; align-items: stretch; gap: 14px; padding: 20px 16px; }.inventory-list__tools { width: 100%; }.inventory-search { width: 100%; }.inventory-filter-button { flex: 0 0 35px; }.inventory-list__footer { padding: 16px; gap: 10px; flex-wrap: wrap; }.inventory-filters { padding-inline: 16px; } }
</style>
