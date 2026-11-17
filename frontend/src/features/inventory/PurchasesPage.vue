<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'

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

/** Every purchase in one list, newest first, with the store it went into; correcting and cancelling happen here. */
const auth = useAuthStore()
const location = ref('')
const locations = ref<{ code: string; name: string }[]>([])
const loading = ref(true)
const error = ref('')
const list = usePagedList<PurchaseRow>(() => (location.value ? `/inventory/purchases?location=${encodeURIComponent(location.value)}` : '/inventory/purchases'))

function run(task: Promise<void>) {
  error.value = ''
  task.catch((cause) => (error.value = errorMessage(cause)))
}

async function load() {
  error.value = ''
  try {
    await Promise.all([list.load(), api<{ code: string; name: string }[]>('/inventory/locations').then((rows) => (locations.value = rows))])
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const cancel = reactive({ row: null as PurchaseRow | null, reason: '', saving: false, error: '' })

async function submitCancel() {
  cancel.saving = true
  cancel.error = ''
  try {
    await api(`/inventory/purchases/${cancel.row!.id}/cancel`, { method: 'POST', body: { reason: cancel.reason, version: cancel.row!.version } })
    cancel.row = null
    await load()
  } catch (cause) {
    cancel.error = errorMessage(cause)
  } finally {
    cancel.saving = false
  }
}

useLiveRefresh(['InventoryChanged'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <section class="xr-card">
      <div class="xr-card__header flex-wrap">
        <h2 class="xr-card__title">خریدها</h2>
        <div class="d-flex flex-wrap gap-2">
          <select v-model="location" class="form-select purchases-filter" aria-label="فیلتر گدام" @change="run(list.first())">
            <option value="">همه گدام‌ها</option>
            <option v-for="row in locations" :key="row.code" :value="row.code">{{ row.name }}</option>
          </select>
          <template v-if="auth.can('inventory.create')">
            <RouterLink :to="{ name: 'inventory-purchase', query: { opening: '1' } }" class="btn btn-soft text-nowrap">موجودی اول دوره</RouterLink>
            <RouterLink :to="{ name: 'inventory-purchase' }" class="btn btn-primary text-nowrap">ثبت خرید</RouterLink>
          </template>
        </div>
      </div>

      <div class="xr-card__body">
        <UiListSkeleton v-if="loading" />
        <p v-else-if="list.rows.value.length === 0" class="xr-state">خریدی ثبت نشده است.</p>
        <div v-else class="xr-table-wrap" role="region" aria-label="فهرست خریدها" tabindex="0">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>تأمین‌کننده</th><th>گدام</th><th>مجموع</th><th>زمان</th><th>وضعیت</th><th v-if="auth.can('inventory.cancel')"></th></tr></thead>
            <tbody>
              <tr v-for="row in list.rows.value" :key="row.id">
                <td><span class="ltr-num">{{ row.purchaseNumber }}</span></td>
                <td>{{ row.supplierName ?? '—' }}</td>
                <td>{{ row.locationName }}</td>
                <td class="text-nowrap">{{ money(row.total) }}</td>
                <td class="text-nowrap">{{ formatDateTime(row.createdAt) }}</td>
                <td>
                  <span class="xr-badge" :class="row.isCancelled ? 'xr-badge--red' : 'xr-badge--green'">{{ row.isCancelled ? 'لغو شده' : 'ثبت‌شده' }}</span>
                  <div v-if="row.cancelReason" class="small text-muted">{{ row.cancelReason }}</div>
                </td>
                <td v-if="auth.can('inventory.cancel')" class="text-nowrap text-end">
                  <template v-if="!row.isCancelled">
                    <RouterLink v-if="auth.can('inventory.create')" :to="{ name: 'inventory-purchase', query: { correct: row.id } }" class="btn btn-soft btn-sm">اصلاح</RouterLink>
                    <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(cancel, { row, reason: '', saving: false, error: '' })">لغو</button>
                  </template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="list.page.value" :page-count="list.pageCount.value" @go="run(list.goTo($event))" />
      </div>
    </section>
  </div>

  <UiModal v-if="cancel.row" :title="`لغو خرید ${cancel.row.purchaseNumber}`" @close="cancel.row = null">
    <form @submit.prevent="submitCancel">
      <p>اجناس این خرید از موجودی کم می‌شوند و ثبت مالی آن برمی‌گردد. اگر بخشی از آن فروخته یا منتقل شده باشد، لغو انجام نمی‌شود.</p>
      <label class="form-label" for="cp-reason">دلیل</label>
      <input id="cp-reason" v-model="cancel.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancel.error" class="alert alert-danger" role="alert">{{ cancel.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="cancel.saving">لغو خرید</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.purchases-filter {
  width: 200px;
  max-width: 100%;
}
</style>
