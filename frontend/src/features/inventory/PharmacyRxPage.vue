<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { rxStageLabels, rxStatusLabels } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { stageTone, stockLabels, type PharmacyBill, type PharmacyRxDetail, type PharmacyRxItem } from './pharmacy'

const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const router = useRouter()

const detail = ref<PharmacyRxDetail | null>(null)
const loading = ref(true)
const error = ref('')
const notice = ref('')

/** What the patient wants now: one row per prescription line, untouched lines stay on the prescription. */
const picks = reactive(new Map<number, { selected: boolean; quantity: number }>())
let billKey = newIdempotencyKey()

const rx = computed(() => detail.value?.prescription)
const open = computed(() => !!rx.value && !['Cancelled', 'Dispensed'].includes(rx.value.status))
const blocked = computed(() => !!detail.value && (detail.value.expired || rx.value?.pharmacyStatus === 'NeedsDoctorReview'))
const billable = (item: PharmacyRxItem) => open.value && !blocked.value && item.productId !== null && item.remaining > 0 && item.available > 0

const chosenLines = computed(() =>
  (detail.value?.items ?? []).filter((i) => billable(i) && picks.get(i.id)?.selected && (picks.get(i.id)?.quantity ?? 0) > 0),
)
/** Lines still owed that this pharmacy cannot give now (not in its list, out, or only expired stock). They never hold up the rest. */
const missing = computed(() =>
  open.value && !blocked.value ? (detail.value?.items ?? []).filter((i) => i.remaining > 0 && !billable(i)) : [],
)
const billTotal = computed(() => chosenLines.value.reduce((sum, i) => sum + (i.salePrice ?? 0) * (picks.get(i.id)?.quantity ?? 0), 0))

async function load() {
  error.value = ''
  try {
    detail.value = await api<PharmacyRxDetail>(`/pharmacy/prescriptions/${props.id}`)
    for (const item of detail.value.items) {
      const max = Math.min(item.remaining, item.available)
      const current = picks.get(item.id)
      if (!current) picks.set(item.id, { selected: billable(item), quantity: max })
      else current.quantity = Math.min(current.quantity, max)
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const billing = ref(false)

async function createBill() {
  billing.value = true
  error.value = ''
  notice.value = ''
  try {
    const sale = await api<{ saleNumber: string; invoiceNumber: string }>('/pharmacy/sales', {
      method: 'POST',
      idempotencyKey: billKey,
      body: {
        patientId: detail.value!.patient.id,
        prescriptionId: rx.value!.id,
        items: chosenLines.value.map((i) => ({ productId: i.productId, quantity: picks.get(i.id)!.quantity, prescriptionItemId: i.id })),
      },
    })
    billKey = newIdempotencyKey()
    picks.clear()
    notice.value = `بل ${sale.saleNumber} ساخته شد. مریض فاکتور ${sale.invoiceNumber} را در صندوق مرکزی پرداخت کند؛ پس از پرداخت، دوا از همین صفحه تحویل داده می‌شود.`
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) billKey = newIdempotencyKey()
  } finally {
    billing.value = false
  }
}

// The patient's decision. It never changes the prescription and creates no bill.
// Only sending it back to the doctor needs a reason, so only that one asks in a dialog; the rest are one click.
const deciding = ref(false)

/** Saves the decision. One that closes the job for now (buying outside, not now) goes back to the pharmacy list. */
async function setStatus(status: string, leave: boolean) {
  deciding.value = true
  error.value = ''
  try {
    await api(`/pharmacy/prescriptions/${rx.value!.id}/status`, { method: 'POST', body: { status, note: null, version: rx.value!.version } })
    if (leave) await router.push({ name: 'pharmacy' })
    else await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    deciding.value = false
  }
}

const review = reactive({ open: false, note: '', saving: false, error: '' })

async function submitReview() {
  review.saving = true
  review.error = ''
  try {
    await api(`/pharmacy/prescriptions/${rx.value!.id}/status`, { method: 'POST', body: { status: 'NeedsDoctorReview', note: review.note, version: rx.value!.version } })
    review.open = false
    await router.push({ name: 'pharmacy' })
  } catch (cause) {
    review.error = errorMessage(cause)
  } finally {
    review.saving = false
  }
}

// Handing over a bill: after payment, or in an emergency with the approval right and a reason.
const handover = reactive({ bill: null as PharmacyBill | null, unpaid: false, reason: '', saving: false, error: '' })

async function deliver(bill: PharmacyBill, unpaidReason: string | null = null) {
  handover.saving = true
  handover.error = ''
  error.value = ''
  try {
    await api(`/pharmacy/sales/${bill.id}/deliver`, { method: 'POST', body: { version: bill.version, unpaidReason } })
    handover.bill = null
    notice.value = `بل ${bill.saleNumber} تحویل شد.`
    await load()
  } catch (cause) {
    if (handover.bill) handover.error = errorMessage(cause)
    else error.value = errorMessage(cause)
  } finally {
    handover.saving = false
  }
}

const cancelling = reactive({ bill: null as PharmacyBill | null, reason: '', saving: false, error: '' })

async function submitCancel() {
  cancelling.saving = true
  cancelling.error = ''
  try {
    await api(`/pharmacy/sales/${cancelling.bill!.id}/return`, { method: 'POST', body: { version: cancelling.bill!.version, reason: cancelling.reason } })
    cancelling.bill = null
    await load()
  } catch (cause) {
    cancelling.error = errorMessage(cause)
  } finally {
    cancelling.saving = false
  }
}

const billState = (b: PharmacyBill) =>
  b.isReturned ? { text: 'لغو شده', tone: 'red' }
  : b.deliveredAt ? { text: 'تحویل شده', tone: 'green' }
  : b.paid ? { text: 'پرداخت شده، آماده تحویل', tone: 'green' }
  : { text: 'منتظر پرداخت در صندوق', tone: 'orange' }

const eventLabel = (status: string) => rxStageLabels[status] ?? status

useLiveRefresh(['PrescriptionCreated', 'InventoryChanged', 'PaymentReceived'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <div class="d-flex gap-2 align-items-center">
      <RouterLink :to="{ name: 'pharmacy' }" class="btn btn-soft btn-sm">بازگشت به دواخانه</RouterLink>
      <RouterLink v-if="rx && auth.can('prescriptions.print')" :to="`/print/rx/${rx.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ نسخه برای مریض</RouterLink>
    </div>
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success mb-0" role="status">{{ notice }}</div>
    <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>

    <template v-if="detail && rx">
      <!-- One card for the whole job: who it is for, what is prescribed, and the bill to make. -->
      <UiCard :title="`نسخه ${rx.rxNumber}`">
        <template #actions>
          <span class="xr-badge" :class="`xr-badge--${stageTone[rx.stage] ?? 'primary'}`">{{ rxStageLabels[rx.stage] ?? rx.stage }}</span>
        </template>
        <div class="rx-who">
          <strong class="rx-who__name">{{ detail.patient.name }}</strong>
          <span v-if="detail.patient.fatherName">پدر: {{ detail.patient.fatherName }}</span>
          <span>پرونده <span class="ltr-num">{{ detail.patient.fileNumber }}</span></span>
          <span v-if="detail.patient.phone" class="ltr-num">{{ detail.patient.phone }}</span>
          <span v-if="detail.patient.age">سن: {{ detail.patient.age }}</span>
        </div>
        <div class="rx-who rx-who--meta">
          <span>داکتر: {{ rx.doctorName ?? '—' }}</span>
          <span>{{ formatDateTime(rx.createdAt) }}</span>
          <span>اعتبار: {{ rx.validUntil ? `تا ${formatDate(rx.validUntil)}` : 'بدون محدودیت' }}</span>
          <span>وضعیت بالینی: {{ rxStatusLabels[rx.status] }}</span>
        </div>
        <p v-if="detail.notes" class="rx-note"><b>توضیح داکتر:</b> {{ detail.notes }}</p>
        <div v-if="rx.status === 'Cancelled'" class="alert alert-danger mt-2 mb-0" role="status">داکتر این نسخه را لغو کرده است.</div>
        <div v-else-if="detail.expired" class="alert alert-warning mt-2 mb-0" role="status">اعتبار این نسخه که داکتر تعیین کرده تمام شده است. تحویل دوا تا تأیید دوباره داکتر ممکن نیست.</div>
        <div v-else-if="rx.pharmacyStatus === 'NeedsDoctorReview'" class="alert alert-warning mt-2 mb-0" role="status">این نسخه برای تأیید دوباره به داکتر فرستاده شده است.</div>

        <!-- A form, so Enter in a quantity box makes the bill straight away. -->
        <form @submit.prevent="auth.can('pharmacy.create') && chosenLines.length && !billing && createBill()">
        <div class="xr-table-wrap mt-3">
          <table class="xr-table">
            <thead>
              <tr><th></th><th>دوا</th><th>تجویز</th><th>موجودی</th><th>قیمت</th><th class="pick-qty">مقدار خرید</th><th>جمع</th></tr>
            </thead>
            <tbody>
              <tr v-for="item in detail.items" :key="item.id" :class="{ 'row-off': !billable(item) }">
                <td><input v-if="billable(item) && picks.get(item.id)" v-model="picks.get(item.id)!.selected" type="checkbox" class="form-check-input" :aria-label="`انتخاب ${item.drugName}`" /></td>
                <td>
                  <strong>{{ item.drugName }}</strong>
                  <div class="small">{{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join('، ') }}</div>
                </td>
                <td>
                  {{ quantity(item.quantity) }}
                  <!-- Only when part of it is already handed over or billed: what is left is what can be sold now. -->
                  <div v-if="item.dispensed || item.billed" class="small text-muted">
                    <span v-if="item.dispensed">تحویل {{ quantity(item.dispensed) }}</span>
                    <span v-if="item.billed"> · در بل {{ quantity(item.billed) }}</span>
                    · باقی {{ quantity(item.remaining) }}
                  </div>
                </td>
                <td>
                  <span class="xr-badge" :class="`xr-badge--${stockLabels[item.stock].tone}`">{{ stockLabels[item.stock].text }}</span>
                  <div v-if="item.productId" class="small">{{ quantity(item.available) }} {{ item.unit }}</div>
                </td>
                <td>{{ item.salePrice !== null ? money(item.salePrice) : '—' }}</td>
                <td class="pick-qty">
                  <input v-if="billable(item) && picks.get(item.id)" v-model.number="picks.get(item.id)!.quantity" type="number" class="form-control" min="0.001" :max="Math.min(item.remaining, item.available)" step="any" :disabled="!picks.get(item.id)!.selected" :aria-label="`مقدار ${item.drugName}`" />
                </td>
                <td>{{ billable(item) && picks.get(item.id)?.selected && item.salePrice !== null ? money(item.salePrice * (picks.get(item.id)?.quantity ?? 0)) : '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <p v-if="missing.length" class="rx-missing mt-2 mb-0" role="status">
          <i class="ri-information-line" aria-hidden="true"></i>
          {{ missing.map((i) => i.drugName).join('، ') }} در دواخانه نیست.
          <template v-if="detail.items.some((i) => billable(i))">بقیه را بل کنید؛ برای این {{ missing.length > 1 ? 'اقلام' : 'قلم' }} «خرید از بیرون» را بزنید.</template>
          <template v-else>مریض این نسخه را از بیرون بخرد: «خرید از بیرون» را بزنید.</template>
        </p>

        <div class="xr-form-actions mt-3">
          <span class="me-auto">مجموع بل: <strong>{{ money(billTotal) }}</strong></span>
          <button v-if="auth.can('pharmacy.create')" type="submit" class="btn btn-primary" :disabled="billing || chosenLines.length === 0">ایجاد بل و ارسال به صندوق</button>
        </div>
        </form>

        <div v-if="open && auth.can('pharmacy.edit')" class="d-flex flex-wrap gap-2 mt-3">
          <button v-if="rx.pharmacyStatus === 'AwaitingVisit'" type="button" class="btn btn-soft btn-sm" :disabled="deciding" @click="setStatus('AwaitingDecision', false)">مریض مراجعه کرد</button>
          <button type="button" class="btn btn-soft btn-sm" :disabled="deciding || rx.pharmacyStatus === 'NeedsDoctorReview'" @click="setStatus('BuyingOutside', true)">خرید از بیرون</button>
          <button type="button" class="btn btn-soft btn-sm" :disabled="deciding || rx.pharmacyStatus === 'NeedsDoctorReview'" @click="setStatus('NotNow', true)">فعلاً بدون خرید</button>
          <button v-if="rx.pharmacyStatus !== 'NeedsDoctorReview'" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(review, { open: true, note: '', error: '' })">نیازمند تأیید داکتر</button>
        </div>
      </UiCard>

      <UiCard v-if="detail.bills.length" title="بل‌های این نسخه">
        <div class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>بل</th><th>فاکتور</th><th>اقلام</th><th>مبلغ</th><th>مانده</th><th>وضعیت</th><th>زمان</th><th></th></tr></thead>
            <tbody>
              <tr v-for="b in detail.bills" :key="b.id">
                <td><span class="ltr-num">{{ b.saleNumber }}</span></td>
                <td><span class="ltr-num">{{ b.invoiceNumber }}</span></td>
                <td><div v-for="line in b.lines" :key="line" class="small">{{ line }}</div></td>
                <td>{{ money(b.total) }}</td>
                <td>{{ money(b.balance) }}</td>
                <td>
                  <span class="xr-badge" :class="`xr-badge--${billState(b).tone}`">{{ billState(b).text }}</span>
                  <div v-if="b.deliveredAt" class="small">{{ formatDateTime(b.deliveredAt) }} — {{ b.deliveredBy ?? '—' }}</div>
                  <div v-if="b.unpaidDeliveryReason" class="small">تحویل بدون پرداخت: {{ b.unpaidDeliveryReason }}</div>
                  <div v-if="b.reservationExpired" class="small">رزرو موجودی منقضی شده است.</div>
                </td>
                <td>{{ formatDateTime(b.createdAt) }}</td>
                <td class="text-nowrap">
                  <button v-if="!b.isReturned && !b.deliveredAt && b.paid && auth.can('pharmacy.create')" type="button" class="btn btn-primary btn-sm" :disabled="handover.saving" @click="deliver(b)">تحویل دوا</button>
                  <button v-if="!b.isReturned && !b.deliveredAt && !b.paid && auth.can('pharmacy.approve')" type="button" class="btn btn-soft btn-sm" @click="Object.assign(handover, { bill: b, unpaid: true, reason: '', error: '' })">تحویل اضطراری</button>
                  <button v-if="!b.isReturned && auth.can('pharmacy.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(cancelling, { bill: b, reason: '', error: '' })">{{ b.deliveredAt ? 'برگشت دوا' : 'لغو بل' }}</button>
                  <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${b.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">فاکتور</RouterLink>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </UiCard>

      <!-- The history is for checking afterwards, so it stays folded. -->
      <details v-if="detail.events.length" class="xr-section xr-card">
        <summary>سابقه تصمیم‌ها <span class="xr-section__hint">{{ quantity(detail.events.length) }} مورد</span></summary>
        <ul class="xr-section__body mb-0">
          <li v-for="(e, i) in detail.events" :key="i">{{ formatDateTime(e.createdAt) }} — {{ eventLabel(e.status) }}{{ e.note ? `: ${e.note}` : '' }} — {{ e.userName ?? '—' }}</li>
        </ul>
      </details>
    </template>
  </div>

  <UiModal v-if="review.open" title="درخواست تأیید دوباره داکتر" @close="review.open = false">
    <form @submit.prevent="submitReview">
      <label class="form-label" for="review-note">دلیل</label>
      <input id="review-note" v-model="review.note" class="form-control mb-3" maxlength="300" minlength="3" required autofocus />
      <div v-if="review.error" class="alert alert-danger" role="alert">{{ review.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="review.saving">ثبت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="handover.bill" :title="`تحویل اضطراری بل ${handover.bill.saleNumber}`" @close="handover.bill = null">
    <form @submit.prevent="deliver(handover.bill!, handover.reason)">
      <p class="small">این بل هنوز پرداخت نشده است. تحویل بدون پرداخت با نام شما و دلیل ثبت می‌شود و فاکتور در صندوق باز می‌ماند.</p>
      <label class="form-label" for="handover-reason">دلیل</label>
      <input id="handover-reason" v-model="handover.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="handover.error" class="alert alert-danger" role="alert">{{ handover.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="handover.saving">تحویل</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="cancelling.bill" :title="`${cancelling.bill.deliveredAt ? 'برگشت دوا' : 'لغو بل'} ${cancelling.bill.saleNumber}`" @close="cancelling.bill = null">
    <form @submit.prevent="submitCancel">
      <p class="small">{{ cancelling.bill.deliveredAt ? 'با تأیید، تمام دوای این بل را از مریض تحویل بگیرید؛ دوا به موجودی برمی‌گردد.' : 'بل لغو و موجودی نگه‌داشته‌شده آزاد می‌شود.' }} مبلغی که هنوز از مریض نزد شفاخانه است، هم‌زمان برگشت وجه ثبت می‌شود؛ پول را به مریض پس بدهید. رسید اصلی در سابقه باقی می‌ماند.</p>
      <label class="form-label" for="cancel-reason">دلیل</label>
      <input id="cancel-reason" v-model="cancelling.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="cancelling.saving">{{ cancelling.bill.deliveredAt ? 'تأیید برگشت دوا و وجه' : 'تأیید لغو و برگشت وجه' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.pick-qty {
  width: 120px;
}

/* A line that cannot be sold here now (none left, no stock, not in the list) steps back, so the sellable ones stand out. */
.row-off td {
  opacity: 0.6;
}

.rx-missing {
  padding: 6px 10px;
  border-radius: 6px;
  background: #fff8e6;
  color: #7a4b00;
  font-size: 13px;
}

.pick-qty .form-control {
  min-height: 34px;
  padding-block: 3px;
}

/* Who and which prescription, as two short lines instead of a block of ten labelled boxes. */
.rx-who {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 2px 16px;
  color: var(--xr-text);
}

.rx-who__name {
  font-size: 16px;
}

.rx-who--meta {
  margin-top: 2px;
  color: var(--xr-text-soft);
  font-size: 13px;
}

.rx-note {
  margin: 6px 0 0;
  font-size: 13.5px;
}
</style>
