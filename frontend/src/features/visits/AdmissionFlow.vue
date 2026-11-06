<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { departmentOptions, doctorOptions } from '@/api/lookups'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { patientName, type Patient } from '@/features/patients/types'
import { useAuthStore } from '@/stores/auth'
import { todayIso } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { visitStatusLabels, type Admission, type VisitRow } from './types'

const props = defineProps<{ patientId?: number | null }>()
const emit = defineEmits<{ changed: [] }>()

type Step = 'patient' | 'visit' | 'pay' | 'done'

const steps: { key: Step; label: string }[] = [
  { key: 'patient', label: 'پیدا کردن مریض' },
  { key: 'visit', label: 'بخش و داکتر' },
  { key: 'pay', label: 'پرداخت در صندوق' },
  { key: 'done', label: 'شماره نوبت' },
]

const auth = useAuthStore()
const router = useRouter()
const step = ref<Step>('patient')
const stepIndex = computed(() => steps.findIndex((s) => s.key === step.value))

const departments = ref<ComboOption[]>([])
const doctors = ref<ComboOption[]>([])
const services = ref<ComboOption[]>([])
const fee = ref<number | null>(null)

const patient = reactive({ id: null as number | null, label: null as string | null })
const openVisit = ref<VisitRow | null>(null)
const form = reactive({ departmentId: null as number | null, doctorStaffId: null as number | null, serviceId: null as number | null, reason: '' })
const admission = ref<Admission | null>(null)
const discount = reactive({ open: false, amount: null as number | null, reason: '' })
/** The extra paid service is rare, so it stays folded until asked for. */
const showService = ref(false)
const saving = ref(false)
const checking = ref(false)
const error = ref('')
const notice = ref('')
const visitKey = ref(newIdempotencyKey())

const canDiscount = computed(() => auth.can('invoices.approve'))
const needsRecovery = computed(() => admission.value?.paid === true && admission.value.queueNumber === null)
const defaultDepartment = computed(() => departments.value.find((d) => d.label === 'معاینه ابتدایی')?.value ?? departments.value[0]?.value ?? null)

function show(row: Admission) {
  admission.value = row
  step.value = row.paid && row.queueNumber !== null ? 'done' : 'pay'
}

/** A patient already in today's visit gets no second visit or fee: an unpaid one continues here and any other is only shown. */
async function checkPatient(id: number) {
  checking.value = true
  error.value = ''
  notice.value = ''
  openVisit.value = null
  try {
    const page = await api<{ items: VisitRow[] }>(`/visits?patientId=${id}&pageSize=10`)
    const open = page.items.find((v) => v.visitDate === todayIso() && !['Completed', 'Cancelled'].includes(v.status))
    if (open?.status === 'AwaitingPayment') {
      show(await api<Admission>(`/admissions/${open.id}`))
    } else if (open) {
      openVisit.value = open
    } else {
      form.departmentId ??= defaultDepartment.value as number | null
      step.value = 'visit'
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    checking.value = false
  }
}

function onPatient(option: ComboOption | null) {
  patient.label = option?.label ?? null
  if (option) void checkPatient(option.value as number)
}

async function createVisit() {
  saving.value = true
  error.value = ''
  try {
    const row = await api<Admission>('/admissions', {
      method: 'POST',
      idempotencyKey: visitKey.value,
      body: { patientId: patient.id, departmentId: form.departmentId, doctorStaffId: form.doctorStaffId, serviceId: form.serviceId, reason: form.reason },
    })
    show(row)
    emit('changed')
  } catch (cause) {
    error.value = errorMessage(cause)
    // A refusal may be retried with new values; an unknown outcome keeps the key so a retry opens one visit.
    if (cause instanceof ApiError && !cause.outcomeUnknown) visitKey.value = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}

async function reload() {
  if (admission.value) show(await api<Admission>(`/admissions/${admission.value.visitId}`))
}

async function recoverNumber() {
  saving.value = true
  error.value = ''
  try {
    show(await api<Admission>(`/admissions/${admission.value!.visitId}/issue-number`, { method: 'POST' }))
    emit('changed')
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

async function applyDiscount() {
  saving.value = true
  error.value = ''
  try {
    const row = admission.value!
    await api(`/invoices/${row.invoiceId}/discount`, { method: 'POST', body: { amount: discount.amount, reason: discount.reason, version: row.invoiceVersion } })
    Object.assign(discount, { open: false, amount: null, reason: '' })
    await reload()
  } catch (cause) {
    error.value = errorMessage(cause)
    await reload().catch(() => undefined)
  } finally {
    saving.value = false
  }
}

function reset() {
  Object.assign(patient, { id: null, label: null })
  Object.assign(form, { departmentId: null, doctorStaffId: null, serviceId: null, reason: '' })
  showService.value = false
  Object.assign(discount, { open: false, amount: null, reason: '' })
  admission.value = null
  openVisit.value = null
  error.value = ''
  notice.value = ''
  visitKey.value = newIdempotencyKey()
  step.value = 'patient'
  if (props.patientId) void router.replace({ name: 'reception' })
}

/** Continues a visit from the list below, or from an appointment arrival. */
function resume(row: Admission) {
  reset()
  Object.assign(patient, { id: row.patientId, label: `${row.patientFileNumber} — ${row.patientName}` })
  show(row)
}

/** The cashier's payment or a discount changes this screen: a settled fee moves it on to the number. */
function refresh() {
  if (step.value === 'pay' && !saving.value) reload().catch(() => undefined)
}

defineExpose({ resume, refresh })

async function startWith(id: number | null | undefined) {
  if (!id) return
  try {
    const row = await api<Patient>(`/patients/${id}`)
    Object.assign(patient, { id: row.id, label: `${row.fileNumber} — ${patientName(row)}` })
    await checkPatient(row.id)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

watch(() => props.patientId, startWith)

onMounted(async () => {
  try {
    const [loadedDepartments, loadedDoctors, loadedServices, loadedFee] = await Promise.all([
      departmentOptions(),
      doctorOptions(),
      api<{ id: number; name: string; code: string; isActive: boolean; currentPrice: number | null }[]>('/services'),
      api<{ price: number }>('/visit-tokens/fee').catch(() => null),
    ])
    departments.value = loadedDepartments
    doctors.value = loadedDoctors
    // The visit fee is charged by the flow itself, so it must not also be offered as an extra service.
    services.value = loadedServices.filter((s) => s.isActive && s.currentPrice !== null && s.code !== 'VISIT-FEE').map((s) => ({ value: s.id, label: s.name, hint: money(s.currentPrice) }))
    fee.value = loadedFee?.price ?? null
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await startWith(props.patientId)
})
</script>

<template>
  <ol class="admission-steps" aria-label="مراحل پذیرش">
    <li
      v-for="(item, index) in steps"
      :key="item.key"
      class="admission-steps__item"
      :class="{ 'is-done': index < stepIndex, 'is-current': index === stepIndex }"
      :aria-current="index === stepIndex ? 'step' : undefined"
    >
      <span class="admission-steps__mark">{{ quantity(index + 1) }}</span>{{ item.label }}
    </li>
  </ol>

  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-info" role="status">{{ notice }}</div>

  <div v-if="step === 'patient'" class="admission-pane">
    <div class="admission-find">
      <div class="admission-find__field"><PatientPicker v-model="patient.id" :selected-label="patient.label" required @select="onPatient" /></div>
      <RouterLink v-if="auth.can('patients.create')" :to="{ name: 'patient-new', query: { admit: '1' } }" class="btn btn-primary text-nowrap">
        <i class="ri-user-add-line" aria-hidden="true"></i> مریض جدید
      </RouterLink>
    </div>
    <p class="form-hint mt-2 mb-0">مریض قبلاً آمده؟ نامش را جستجو کنید. بار اول است؟ «مریض جدید» را بزنید.</p>
    <p v-if="checking" class="form-hint mt-3 mb-0" role="status">در حال بررسی مراجعه‌های امروز…</p>
    <div v-if="openVisit" class="alert alert-warning mt-3 mb-0" role="alert">
      این مریض امروز مراجعه فعال دارد ({{ visitStatusLabels[openVisit.status] }}، نوبت {{ openVisit.queueNumber === null ? '—' : quantity(openVisit.queueNumber) }}). کار در همان مراجعه ادامه می‌یابد و فیس دوباره گرفته نمی‌شود.
      <button type="button" class="btn btn-soft btn-sm ms-2" @click="reset">مریض دیگر</button>
    </div>
  </div>

  <form v-else-if="step === 'visit'" class="admission-pane" @submit.prevent="createVisit">
    <p class="admission-who">{{ patient.label }}</p>
    <div class="row g-3 mb-3">
      <div class="col-md-6"><UiComboBox v-model="form.departmentId" label="بخش" :options="departments" required chip /></div>
      <div class="col-md-6"><UiComboBox v-model="form.doctorStaffId" label="داکتر (اختیاری)" :options="doctors" chip placeholder="بدون انتخاب" /></div>
      <div class="col-md-6">
        <label class="form-label" for="a-reason">علت مراجعه (اختیاری)</label>
        <input id="a-reason" v-model="form.reason" class="form-control" maxlength="300" placeholder="مثلاً: تاری دید" />
      </div>
      <div v-if="showService || form.serviceId" class="col-md-6"><UiComboBox v-model="form.serviceId" label="خدمت اضافی قابل پرداخت (اختیاری)" :options="services" chip placeholder="بدون خدمت اضافی" /></div>
      <div v-else-if="services.length" class="col-md-6 d-flex align-items-end">
        <button type="button" class="btn btn-quiet" @click="showService = true"><i class="ri-add-line" aria-hidden="true"></i> افزودن خدمت اضافی</button>
      </div>
    </div>
    <div class="xr-form-actions align-items-center">
      <button type="submit" class="btn btn-primary" :disabled="saving || !form.departmentId">{{ saving ? 'در حال ثبت…' : 'ثبت مراجعه و ارسال به صندوق' }}</button>
      <button type="button" class="btn btn-soft" @click="reset">بازگشت</button>
      <span v-if="fee !== null" class="ms-auto">فیس معاینه: <strong>{{ money(fee) }}</strong></span>
    </div>
  </form>

  <div v-else-if="step === 'pay' && admission" class="admission-pane">
    <p class="admission-who">{{ admission.patientName }} <span class="ltr-num">{{ admission.patientFileNumber }}</span></p>
    <dl class="xr-facts mb-3">
      <div><dt>بخش</dt><dd>{{ admission.departmentName }}</dd></div>
      <div><dt>فاکتور</dt><dd><span class="ltr-num">{{ admission.invoiceNumber }}</span></dd></div>
      <div v-if="admission.discount"><dt>تخفیف</dt><dd>{{ money(admission.discount) }}</dd></div>
      <div><dt>قابل پرداخت</dt><dd><strong class="admission-amount">{{ money(admission.balance) }}</strong></dd></div>
    </dl>

    <template v-if="needsRecovery">
      <div class="alert alert-warning" role="alert">پرداخت این مراجعه ثبت شده و نوبت آن صادر نشده است. پولی دوباره گرفته نمی‌شود.</div>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="recoverNumber">صدور نوبت</button>
    </template>
    <template v-else>
      <div v-if="admission.visitDate !== todayIso()" class="alert alert-warning" role="alert">این مراجعه از روز گذشته مانده و پرداخت نمی‌شود. آن را در «صف امروز» لغو کنید و دوباره ثبت نمایید.</div>
      <div v-else class="alert alert-info admission-next" role="status">
        <i class="ri-arrow-left-circle-line" aria-hidden="true"></i>
        <span><strong>مریض را به صندوق بفرستید.</strong> پس از پرداخت، نوبت او خودکار همین‌جا نشان داده می‌شود. می‌توانید تا آن وقت مریض بعدی را پذیرش کنید.</span>
      </div>

      <form v-if="discount.open" class="row g-3 align-items-end mb-3" @submit.prevent="applyDiscount">
        <div class="col-md-3">
          <label class="form-label" for="a-discount">مبلغ تخفیف</label>
          <input id="a-discount" v-model.number="discount.amount" class="form-control" type="number" min="0.01" :max="admission.balance - 0.01" step="0.01" required />
        </div>
        <div class="col-md-6">
          <label class="form-label" for="a-discount-reason">دلیل تخفیف</label>
          <input id="a-discount-reason" v-model="discount.reason" class="form-control" minlength="3" maxlength="300" required />
        </div>
        <div class="col-md-3 d-flex gap-2">
          <button type="submit" class="btn btn-primary" :disabled="saving">ثبت تخفیف</button>
          <button type="button" class="btn btn-soft" @click="discount.open = false">انصراف</button>
        </div>
      </form>

      <div class="xr-form-actions">
        <button type="button" class="btn btn-primary" @click="reset">مریض بعدی</button>
        <button v-if="canDiscount && !discount.open" type="button" class="btn btn-soft" @click="discount.open = true">تخفیف</button>
      </div>
    </template>
  </div>

  <div v-else-if="step === 'done' && admission" class="admission-pane admission-done" role="status">
    <p class="admission-done__label">شماره نوبت</p>
    <p class="admission-done__number">{{ quantity(admission.queueNumber) }}</p>
    <p class="admission-who">{{ admission.patientName }} <span class="ltr-num">{{ admission.patientFileNumber }}</span></p>
    <dl class="xr-facts mb-3">
      <div><dt>بخش</dt><dd>{{ admission.departmentName }}</dd></div>
      <div><dt>داکتر</dt><dd>{{ admission.doctorName ?? '—' }}</dd></div>
      <div><dt>پرداخت</dt><dd><span class="xr-badge xr-badge--green">پرداخت‌شده</span></dd></div>
    </dl>
    <div class="xr-form-actions justify-content-center">
      <RouterLink v-if="auth.can('visits.print')" :to="`/print/visit/${admission.visitId}`" target="_blank" class="btn btn-soft">چاپ رسید نوبت</RouterLink>
      <button type="button" class="btn btn-primary" @click="reset">مریض بعدی</button>
    </div>
  </div>
</template>

<style scoped>
.admission-steps {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--xr-gap);
  list-style: none;
  padding: 0;
  margin: 0 0 1.25rem;
}

.admission-steps__item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-bottom: 8px;
  border-bottom: 3px solid var(--xr-border, #e5e7eb);
  color: var(--xr-text-muted, #6b7280);
  font-size: 0.9rem;
}

.admission-steps__item.is-done {
  border-color: var(--xr-status-green, #16a34a);
}

.admission-steps__item.is-current {
  border-color: var(--xr-primary);
  color: var(--xr-primary);
  font-weight: 700;
}

.admission-steps__mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  border: 1px solid currentColor;
  font-size: 0.8rem;
}

.admission-find {
  display: flex;
  align-items: flex-end;
  gap: var(--xr-gap);
}

.admission-find__field {
  flex: 1;
  min-width: 0;
}

.admission-amount {
  font-size: 1.25rem;
}

.admission-next {
  display: flex;
  align-items: center;
  gap: 10px;
}

.admission-next > i {
  font-size: 1.6rem;
}

.admission-who {
  margin-bottom: 1rem;
  font-weight: 700;
}

.admission-done {
  text-align: center;
}

.admission-done__label {
  margin: 0;
  color: var(--xr-text-muted, #6b7280);
}

.admission-done__number {
  margin: 0 0 0.5rem;
  font-size: 4rem;
  line-height: 1.1;
  font-weight: 700;
  color: var(--xr-primary);
}

@media (max-width: 575.98px) {
  .admission-steps {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .admission-find {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
