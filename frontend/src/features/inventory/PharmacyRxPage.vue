<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
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
const decision = reactive({ status: '' as string, title: '', note: '', noteRequired: false, saving: false, error: '' })

function decide(status: string, title: string, noteRequired = false) {
  Object.assign(decision, { status, title, note: '', noteRequired, error: '' })
}

async function submitDecision() {
  decision.saving = true
  decision.error = ''
  try {
    await api(`/pharmacy/prescriptions/${rx.value!.id}/status`, { method: 'POST', body: { status: decision.status, note: decision.note || null, version: rx.value!.version } })
    decision.status = ''
    await load()
  } catch (cause) {
    decision.error = errorMessage(cause)
  } finally {
    decision.saving = false
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
      <UiCard :title="`نسخه ${rx.rxNumber}`">
        <template #actions>
          <span class="xr-badge" :class="`xr-badge--${stageTone[rx.stage] ?? 'primary'}`">{{ rxStageLabels[rx.stage] ?? rx.stage }}</span>
        </template>
        <dl class="xr-facts">
          <div><dt>مریض</dt><dd>{{ detail.patient.name }}</dd></div>
          <div><dt>نام پدر</dt><dd>{{ detail.patient.fatherName ?? '—' }}</dd></div>
          <div><dt>شماره پرونده</dt><dd><span class="ltr-num">{{ detail.patient.fileNumber }}</span></dd></div>
          <div><dt>شماره تماس</dt><dd><span class="ltr-num">{{ detail.patient.phone ?? '—' }}</span></dd></div>
          <div><dt>سن</dt><dd>{{ detail.patient.age ?? '—' }}</dd></div>
          <div><dt>داکتر</dt><dd>{{ rx.doctorName ?? '—' }}</dd></div>
          <div><dt>تاریخ تجویز</dt><dd>{{ formatDateTime(rx.createdAt) }}</dd></div>
          <div><dt>اعتبار</dt><dd>{{ rx.validUntil ? `تا ${formatDate(rx.validUntil)}` : 'داکتر محدودیتی تعیین نکرده' }}</dd></div>
          <div><dt>وضعیت بالینی</dt><dd>{{ rxStatusLabels[rx.status] }}</dd></div>
          <div v-if="detail.notes"><dt>توضیح داکتر</dt><dd>{{ detail.notes }}</dd></div>
        </dl>
        <div v-if="rx.status === 'Cancelled'" class="alert alert-danger mt-3 mb-0" role="status">داکتر این نسخه را لغو کرده است.</div>
        <div v-else-if="detail.expired" class="alert alert-warning mt-3 mb-0" role="status">اعتبار این نسخه که داکتر تعیین کرده تمام شده است. تحویل دوا تا تأیید دوباره داکتر ممکن نیست.</div>
        <div v-else-if="rx.pharmacyStatus === 'NeedsDoctorReview'" class="alert alert-warning mt-3 mb-0" role="status">این نسخه برای تأیید دوباره به داکتر فرستاده شده است.</div>
      </UiCard>

      <UiCard title="اقلام نسخه">
        <div class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr><th></th><th>دوا</th><th>تجویز</th><th>تحویل‌شده</th><th>در بل</th><th>باقی‌مانده</th><th>موجودی</th><th>قیمت</th><th class="pick-qty">مقدار خرید</th></tr>
            </thead>
            <tbody>
              <tr v-for="item in detail.items" :key="item.id">
                <td><input v-if="billable(item) && picks.get(item.id)" v-model="picks.get(item.id)!.selected" type="checkbox" class="form-check-input" :aria-label="`انتخاب ${item.drugName}`" /></td>
                <td>
                  <strong>{{ item.drugName }}</strong>
                  <div class="small">{{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join('، ') }}</div>
                </td>
                <td>{{ quantity(item.quantity) }}</td>
                <td>{{ quantity(item.dispensed) }}</td>
                <td>{{ item.billed ? quantity(item.billed) : '—' }}</td>
                <td>{{ quantity(item.remaining) }}</td>
                <td>
                  <span class="xr-badge" :class="`xr-badge--${stockLabels[item.stock].tone}`">{{ stockLabels[item.stock].text }}</span>
                  <div v-if="item.productId" class="small">{{ quantity(item.available) }} {{ item.unit }}</div>
                </td>
                <td>{{ item.salePrice !== null ? money(item.salePrice) : '—' }}</td>
                <td class="pick-qty">
                  <input v-if="billable(item) && picks.get(item.id)" v-model.number="picks.get(item.id)!.quantity" type="number" class="form-control" min="0.001" :max="Math.min(item.remaining, item.available)" step="any" :disabled="!picks.get(item.id)!.selected" :aria-label="`مقدار ${item.drugName}`" />
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="form-hint mt-2 mb-0">فقط اقلام انتخاب‌شده بل می‌شوند؛ بقیه در نسخه باقی می‌مانند. دوای دیگر به جای دوای نسخه فقط با تأیید داکتر داده می‌شود.</p>

        <div class="xr-form-actions mt-3">
          <span class="me-auto">مجموع بل: <strong>{{ money(billTotal) }}</strong></span>
          <button v-if="auth.can('pharmacy.create')" type="button" class="btn btn-primary" :disabled="billing || chosenLines.length === 0" @click="createBill">ایجاد بل و ارسال به صندوق</button>
        </div>

        <div v-if="open && auth.can('pharmacy.edit')" class="d-flex flex-wrap gap-2 mt-3">
          <button v-if="rx.pharmacyStatus === 'AwaitingVisit'" type="button" class="btn btn-soft btn-sm" @click="decide('AwaitingDecision', 'مریض مراجعه کرد')">مریض مراجعه کرد</button>
          <button type="button" class="btn btn-soft btn-sm" :disabled="rx.pharmacyStatus === 'NeedsDoctorReview'" @click="decide('BuyingOutside', 'خرید از بیرون')">خرید از بیرون</button>
          <button type="button" class="btn btn-soft btn-sm" :disabled="rx.pharmacyStatus === 'NeedsDoctorReview'" @click="decide('NotNow', 'فعلاً بدون خرید')">فعلاً بدون خرید</button>
          <button v-if="rx.pharmacyStatus !== 'NeedsDoctorReview'" type="button" class="btn btn-soft-danger btn-sm" @click="decide('NeedsDoctorReview', 'درخواست تأیید دوباره داکتر', true)">نیازمند تأیید داکتر</button>
        </div>
      </UiCard>

      <UiCard title="بل‌های این نسخه">
        <p v-if="detail.bills.length === 0" class="xr-state">برای این نسخه بلی ساخته نشده است.</p>
        <div v-else class="xr-table-wrap">
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
                  <button v-if="!b.isReturned && !b.deliveredAt && b.balance === b.total && auth.can('pharmacy.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(cancelling, { bill: b, reason: '', error: '' })">لغو بل</button>
                  <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${b.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">فاکتور</RouterLink>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </UiCard>

      <UiCard title="سابقه تصمیم‌ها">
        <p v-if="detail.events.length === 0" class="xr-state">تصمیمی ثبت نشده است.</p>
        <ul v-else class="mb-0">
          <li v-for="(e, i) in detail.events" :key="i">{{ formatDateTime(e.createdAt) }} — {{ eventLabel(e.status) }}{{ e.note ? `: ${e.note}` : '' }} — {{ e.userName ?? '—' }}</li>
        </ul>
      </UiCard>
    </template>
  </div>

  <UiModal v-if="decision.status" :title="decision.title" @close="decision.status = ''">
    <form @submit.prevent="submitDecision">
      <p class="small">این تصمیم نسخه را تغییر نمی‌دهد و بل یا بدهی نمی‌سازد. نام شما و زمان ثبت می‌شود.</p>
      <label class="form-label" for="decision-note">{{ decision.noteRequired ? 'دلیل' : 'توضیح (اختیاری)' }}</label>
      <input id="decision-note" v-model="decision.note" class="form-control mb-3" maxlength="300" :minlength="decision.noteRequired ? 3 : undefined" :required="decision.noteRequired" />
      <div v-if="decision.error" class="alert alert-danger" role="alert">{{ decision.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="decision.saving">ثبت</button>
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

  <UiModal v-if="cancelling.bill" :title="`لغو بل ${cancelling.bill.saleNumber}`" @close="cancelling.bill = null">
    <form @submit.prevent="submitCancel">
      <p class="small">بل لغو و موجودی نگه‌داشته‌شده آزاد می‌شود. نسخه و اقلام آن باقی می‌مانند.</p>
      <label class="form-label" for="cancel-reason">دلیل</label>
      <input id="cancel-reason" v-model="cancelling.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="cancelling.saving">لغو بل</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.pick-qty {
  width: 120px;
}
</style>
