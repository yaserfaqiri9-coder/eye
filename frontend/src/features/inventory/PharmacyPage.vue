<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { rxStageLabels } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'
import { stageTone, type PharmacyPatient, type PharmacyPatientHit, type PharmacyQueueBill, type PharmacyQueues, type PharmacyRxRow } from './pharmacy'

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
  deliveredAt: string | null
  version: number
}

const auth = useAuthStore()
const calls = useCallDesk('Pharmacy')
const error = ref('')
const notice = ref('')

// Search: one box for name, father's name, phone, file number or any number on the patient's papers.
const q = ref('')
const searching = ref(false)
const hits = ref<PharmacyPatientHit[] | null>(null)
const chosen = ref<{ patient: PharmacyPatient; prescriptions: PharmacyRxRow[] } | null>(null)

// A newer search cancels the older one, so a slow answer for an earlier term never replaces the current list.
let searchController: AbortController | null = null

async function search() {
  const term = q.value.trim()
  searchController?.abort()
  chosen.value = null
  if (term.length < 2) {
    hits.value = null
    searching.value = false
    return
  }

  const controller = new AbortController()
  searchController = controller
  searching.value = true
  try {
    const found = await api<PharmacyPatientHit[]>(`/pharmacy/search?q=${encodeURIComponent(term)}`, { signal: controller.signal })
    hits.value = found
    if (found.length === 1) await choose(found[0].id, controller.signal)
  } catch (cause) {
    if (controller.signal.aborted) return
    error.value = errorMessage(cause)
  } finally {
    if (searchController === controller) searching.value = false
  }
}

async function choose(id: number, signal?: AbortSignal) {
  try {
    const result = await api<{ patient: PharmacyPatient; prescriptions: PharmacyRxRow[] }>(`/pharmacy/patients/${id}/prescriptions`, { signal })
    if (!signal?.aborted) chosen.value = result
  } catch (cause) {
    if (signal?.aborted) return
    error.value = errorMessage(cause)
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(search, 350)
})

// Work lists.
type TabKey = 'new' | 'awaitingPayment' | 'readyToDeliver' | 'partial' | 'needsDoctor' | 'sales'
const tab = ref<TabKey>('new')
const queues = ref<PharmacyQueues | null>(null)
const sales = ref<SaleRow[]>([])
const loading = ref(true)

const tabs = computed(() => [
  { key: 'new' as const, label: 'نسخه‌های جدید', count: queues.value?.new.length },
  { key: 'awaitingPayment' as const, label: 'منتظر پرداخت', count: queues.value?.awaitingPayment.length },
  { key: 'readyToDeliver' as const, label: 'آماده تحویل', count: queues.value?.readyToDeliver.length },
  { key: 'partial' as const, label: 'خرید قسمتی', count: queues.value?.partial.length },
  { key: 'needsDoctor' as const, label: 'نیازمند تأیید داکتر', count: queues.value?.needsDoctor.length },
  { key: 'sales' as const, label: 'سوابق فروش', count: undefined },
])

const rxRows = computed(() => (tab.value === 'new' || tab.value === 'partial' || tab.value === 'needsDoctor' ? queues.value?.[tab.value] ?? [] : []))
const billRows = computed(() => (tab.value === 'awaitingPayment' || tab.value === 'readyToDeliver' ? queues.value?.[tab.value] ?? [] : []))

async function load() {
  error.value = ''
  try {
    queues.value = await api<PharmacyQueues>('/pharmacy/queues')
    sales.value = (await api<{ items: SaleRow[] }>('/pharmacy/sales?pageSize=50')).items
    if (chosen.value) await choose(chosen.value.patient.id)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const delivering = ref<number | null>(null)

async function deliver(bill: PharmacyQueueBill) {
  delivering.value = bill.saleId
  error.value = ''
  try {
    await api(`/pharmacy/sales/${bill.saleId}/deliver`, { method: 'POST', body: { version: bill.version } })
    notice.value = `بل ${bill.saleNumber} تحویل شد و از موجودی کم شد.`
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    delivering.value = null
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

useLiveRefresh(['PrescriptionCreated', 'InventoryChanged', 'PaymentReceived'], load)
onMounted(load)
onBeforeUnmount(() => {
  clearTimeout(debounce)
  searchController?.abort()
})
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success mb-0" role="status">{{ notice }}</div>
    <div v-if="calls.error.value || !calls.stationOnline.value"><CallNotice :desks="[calls]" /></div>

    <UiCard title="جستجوی مریض و نسخه">
      <template #actions>
        <RouterLink v-if="auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale' }" class="btn btn-soft text-nowrap">فروش مستقل</RouterLink>
      </template>
      <input v-model="q" type="search" class="form-control form-control-lg" placeholder="نام، نام پدر، شماره تماس، شماره پرونده، نسخه یا مراجعه" aria-label="جستجوی مریض و نسخه" autofocus />
      <p class="form-hint mb-0 mt-1">اعداد فارسی و انگلیسی هر دو پذیرفته می‌شوند. نسخه‌های قدیمی هم پیدا می‌شوند.</p>

      <p v-if="searching" class="xr-state" role="status">در حال جستجو...</p>
      <p v-else-if="hits && hits.length === 0" class="xr-state">مریضی پیدا نشد.</p>
      <div v-else-if="hits && !chosen" class="xr-table-wrap mt-3">
        <table class="xr-table">
          <thead><tr><th>مریض</th><th>نام پدر</th><th>شماره پرونده</th><th>شماره تماس</th><th>سن</th><th>نسخه‌ها</th><th>آخرین نسخه</th><th></th></tr></thead>
          <tbody>
            <tr v-for="p in hits" :key="p.id">
              <td><span class="xr-person__name">{{ p.name }}</span></td>
              <td>{{ p.fatherName ?? '—' }}</td>
              <td><span class="ltr-num">{{ p.fileNumber }}</span></td>
              <td><span class="ltr-num">{{ p.phone ?? '—' }}</span></td>
              <td>{{ p.age ?? '—' }}</td>
              <td>{{ p.prescriptionCount }}</td>
              <td>{{ p.lastPrescriptionAt ? formatDate(p.lastPrescriptionAt) : '—' }}</td>
              <td><button type="button" class="btn btn-soft btn-sm" @click="choose(p.id)">انتخاب</button></td>
            </tr>
          </tbody>
        </table>
      </div>

      <section v-if="chosen" class="mt-3">
        <dl class="xr-facts">
          <div><dt>مریض</dt><dd>{{ chosen.patient.name }}</dd></div>
          <div><dt>نام پدر</dt><dd>{{ chosen.patient.fatherName ?? '—' }}</dd></div>
          <div><dt>شماره پرونده</dt><dd><span class="ltr-num">{{ chosen.patient.fileNumber }}</span></dd></div>
          <div><dt>شماره تماس</dt><dd><span class="ltr-num">{{ chosen.patient.phone ?? '—' }}</span></dd></div>
          <div><dt>سن</dt><dd>{{ chosen.patient.age ?? '—' }}</dd></div>
        </dl>
        <button v-if="hits && hits.length > 1" type="button" class="btn btn-soft btn-sm mb-2" @click="chosen = null">بازگشت به نتایج</button>
        <p v-if="chosen.prescriptions.length === 0" class="xr-state">برای این مریض نسخه‌ای ثبت نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>نسخه</th><th>تاریخ</th><th>داکتر</th><th>وضعیت</th><th></th></tr></thead>
            <tbody>
              <tr v-for="rx in chosen.prescriptions" :key="rx.id">
                <td><span class="ltr-num">{{ rx.rxNumber }}</span></td>
                <td>{{ formatDateTime(rx.createdAt) }}</td>
                <td>{{ rx.doctorName ?? '—' }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${stageTone[rx.stage] ?? 'primary'}`">{{ rxStageLabels[rx.stage] ?? rx.stage }}</span></td>
                <td><RouterLink :to="{ name: 'pharmacy-rx', params: { id: rx.id } }" class="btn btn-soft btn-sm">باز کردن</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </UiCard>

    <section class="xr-card">
      <div class="xr-card__header">
        <nav class="xr-tabs" role="tablist" aria-label="کارهای دواخانه">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">
            {{ t.label }}<span v-if="t.count" class="ms-1">({{ t.count }})</span>
          </button>
        </nav>
      </div>
      <div class="xr-card__body">
        <UiListSkeleton v-if="loading" />

        <template v-else-if="tab === 'new' || tab === 'partial' || tab === 'needsDoctor'">
          <p v-if="rxRows.length === 0" class="xr-state">{{ tab === 'new' ? 'نسخه تازه‌ای در هفت روز گذشته نیست. نسخه‌های قدیمی را جستجو کنید.' : 'موردی نیست.' }}</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>نسخه</th><th>مریض</th><th>نام پدر</th><th>داکتر</th><th>تاریخ</th><th>وضعیت</th><th v-if="tab === 'new'">فراخوان</th><th></th></tr></thead>
              <tbody>
                <tr v-for="rx in rxRows" :key="rx.id">
                  <td><span class="ltr-num">{{ rx.rxNumber }}</span></td>
                  <td><span class="xr-person__name">{{ rx.patientName }}</span> <span class="ltr-num small">{{ rx.fileNumber }}</span></td>
                  <td>{{ rx.fatherName ?? '—' }}</td>
                  <td>{{ rx.doctorName ?? '—' }}</td>
                  <td>{{ formatDateTime(rx.createdAt) }}</td>
                  <td><span class="xr-badge" :class="`xr-badge--${stageTone[rx.stage] ?? 'primary'}`">{{ rxStageLabels[rx.stage] ?? rx.stage }}</span></td>
                  <td v-if="tab === 'new'"><CallControls :desk="calls" :visit-id="rx.visitId" /></td>
                  <td><RouterLink :to="{ name: 'pharmacy-rx', params: { id: rx.id } }" class="btn btn-soft btn-sm">باز کردن</RouterLink></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>

        <template v-else-if="tab === 'awaitingPayment' || tab === 'readyToDeliver'">
          <p v-if="billRows.length === 0" class="xr-state">{{ tab === 'awaitingPayment' ? 'بلی منتظر پرداخت نیست.' : 'بل پرداخت‌شده‌ای منتظر تحویل نیست.' }}</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>بل</th><th>مریض</th><th>نسخه</th><th>فاکتور</th><th>مبلغ</th><th>مانده</th><th>زمان</th><th></th></tr></thead>
              <tbody>
                <tr v-for="b in billRows" :key="b.saleId">
                  <td><span class="ltr-num">{{ b.saleNumber }}</span></td>
                  <td>{{ b.patientName ?? 'فروش مستقل' }} <span v-if="b.fileNumber" class="ltr-num small">{{ b.fileNumber }}</span></td>
                  <td><span class="ltr-num">{{ b.rxNumber ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ b.invoiceNumber }}</span></td>
                  <td>{{ money(b.total) }}</td>
                  <td>{{ money(b.balance) }}</td>
                  <td>
                    {{ formatDateTime(b.createdAt) }}
                    <span v-if="b.reservationExpired && tab === 'awaitingPayment'" class="xr-badge xr-badge--orange ms-1" title="موجودی دیگر برای این بل نگه داشته نمی‌شود">رزرو منقضی</span>
                  </td>
                  <td class="text-nowrap">
                    <button v-if="tab === 'readyToDeliver' && auth.can('pharmacy.create')" type="button" class="btn btn-primary btn-sm" :disabled="delivering === b.saleId" @click="deliver(b)">تحویل دوا</button>
                    <RouterLink v-if="b.prescriptionId" :to="{ name: 'pharmacy-rx', params: { id: b.prescriptionId } }" class="btn btn-soft btn-sm ms-2">نسخه</RouterLink>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>

        <template v-else>
          <p v-if="sales.length === 0" class="xr-state">فروشی ثبت نشده است.</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>مریض</th><th>فاکتور</th><th>مبلغ</th><th>مانده</th><th>وضعیت</th><th>زمان</th><th v-if="auth.can('pharmacy.cancel')">عملیات</th></tr></thead>
              <tbody>
                <tr v-for="row in sales" :key="row.id">
                  <td><span class="ltr-num">{{ row.saleNumber }}</span></td>
                  <td>{{ row.patientName ?? 'فروش مستقل' }}</td>
                  <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
                  <td>{{ row.isReturned ? 'لغو / برگشت شده' : money(row.total) }}</td>
                  <td>{{ money(row.balance) }}</td>
                  <td>{{ row.isReturned ? 'لغو شده' : row.deliveredAt ? 'تحویل شده' : row.balance > 0 ? 'منتظر پرداخت' : 'آماده تحویل' }}</td>
                  <td>{{ formatDateTime(row.createdAt) }}</td>
                  <td v-if="auth.can('pharmacy.cancel')" class="text-nowrap">
                    <button v-if="!row.isReturned" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(returning, { row, reason: '', error: '' })">{{ row.deliveredAt ? 'برگشت' : 'لغو بل' }}</button>
                    <RouterLink v-if="!row.isReturned && !row.deliveredAt && auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale', query: { correct: row.id } }" class="btn btn-soft btn-sm ms-2">اصلاح</RouterLink>
                    <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${row.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">فاکتور</RouterLink>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>
    </section>
  </div>

  <UiModal v-if="returning.row" :title="`${returning.row.deliveredAt ? 'برگشت فروش' : 'لغو بل'} ${returning.row.saleNumber}`" @close="returning.row = null">
    <form @submit.prevent="submitReturn">
      <p v-if="!returning.row.deliveredAt" class="small">بل پرداخت‌نشده لغو می‌شود و موجودی نگه‌داشته‌شده آزاد می‌شود. نسخه مریض تغییر نمی‌کند.</p>
      <label class="form-label" for="ret-reason">دلیل</label>
      <input id="ret-reason" v-model="returning.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="returning.error" class="alert alert-danger" role="alert">{{ returning.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="returning.saving">ثبت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
