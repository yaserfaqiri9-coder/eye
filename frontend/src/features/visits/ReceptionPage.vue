<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { departmentOptions, doctorOptions } from '@/api/lookups'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatTime, kabulToUtc, todayIso } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import AdmissionFlow from './AdmissionFlow.vue'
import NewVisitModal from './NewVisitModal.vue'
import TransferDoctorModal from './TransferDoctorModal.vue'
import { feeStatusLabels, visitStatusLabels, visitStatusTones, type Admission, type VisitRow, type VisitToken } from './types'

interface AppointmentRow {
  id: number
  patientId: number
  patientFileNumber: string
  patientName: string
  doctorName: string
  scheduledAt: string
  status: string
  note: string | null
  version: number
}

const appointmentLabels: Record<string, string> = { Scheduled: 'در انتظار حضور', CheckedIn: 'پذیرش‌شده', Cancelled: 'لغو شده', NoShow: 'نیامده' }

const auth = useAuthStore()
const route = useRoute()
const calls = useCallDesk('Reception')
const visits = ref<VisitRow[]>([])
const totalVisits = ref(0)
const appointments = ref<AppointmentRow[]>([])
const departments = ref<ComboOption[]>([])
const doctors = ref<ComboOption[]>([])
const departmentId = ref<number | null>(null)
const loading = ref(true)
const error = ref('')
const activeToken = ref<VisitToken | null>(null)
const tokens = ref<VisitToken[]>([])
const pending = ref<Admission[]>([])
const flow = ref<InstanceType<typeof AdmissionFlow> | null>(null)
const patientFromLink = computed(() => (route.query.patient ? Number(route.query.patient) : null))

interface CashSession {
  id: number
  registerName: string
  openingAmount: number
  cashCollected: number
  closedAt: string | null
  version: number
}

const canAdmit = computed(() => auth.can('visitfees.create'))
/** A register opened before all fees moved to the cashier. Reception opens no new ones and can only close this one. */
const session = ref<CashSession | null>(null)
const cash = reactive({ closing: false, counted: 0, saving: false, error: '' })

async function loadDesk() {
  if (!auth.can('visitfees.view')) return
  const [loadedTokens, loadedPending, sessions] = await Promise.all([
    api<VisitToken[]>('/visit-tokens'),
    api<Admission[]>('/admissions/pending'),
    auth.can('cash.view') ? api<CashSession[]>('/cash/sessions?mine=true') : Promise.resolve([]),
  ])
  tokens.value = loadedTokens
  pending.value = loadedPending
  session.value = sessions.find((s) => !s.closedAt) ?? null
  await calls.load()
}

async function closeCash() {
  cash.saving = true
  cash.error = ''
  try {
    await api(`/cash/sessions/${session.value!.id}/close`, { method: 'POST', body: { countedAmount: cash.counted, version: session.value!.version } })
    cash.closing = false
    await loadDesk()
  } catch (cause) {
    cash.error = errorMessage(cause)
  } finally {
    cash.saving = false
  }
}

async function cancelToken(token: VisitToken) {
  error.value = ''
  try {
    await api(`/visit-tokens/${token.id}/cancel`, { method: 'POST', body: { version: token.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await loadDesk()
}

async function cancelAdmission(row: Admission) {
  error.value = ''
  try {
    const visit = await api<VisitRow>(`/visits/${row.visitId}`)
    await api(`/visits/${row.visitId}/status`, { method: 'POST', body: { status: 'Cancelled', version: visit.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
}

function closeVisitModal() {
  activeToken.value = null
  loadDesk().catch((cause) => (error.value = errorMessage(cause)))
}

const counts = computed(() => ({
  awaiting: visits.value.filter((v) => v.status === 'AwaitingPayment').length,
  waiting: visits.value.filter((v) => !['Completed', 'Cancelled', 'AwaitingPayment'].includes(v.status)).length,
  completed: visits.value.filter((v) => v.status === 'Completed').length,
  cancelled: visits.value.filter((v) => v.status === 'Cancelled').length,
}))

async function loadVisits() {
  const params = new URLSearchParams({ pageSize: '100' })
  if (departmentId.value) params.set('departmentId', String(departmentId.value))
  // The whole day: a busy day past the first page must not hide the newest patient or undercount the cards above.
  const loaded: VisitRow[] = []
  let total = 0
  for (let number = 1; ; number++) {
    params.set('page', String(number))
    const page = await api<{ items: VisitRow[]; total: number }>(`/visits?${params}`)
    loaded.push(...page.items)
    total = page.total
    if (!page.items.length || loaded.length >= total) break
  }
  visits.value = loaded
  totalVisits.value = total
}

async function loadAppointments() {
  if (auth.can('appointments.view')) {
    appointments.value = await api<AppointmentRow[]>('/appointments')
  }
}

async function refresh() {
  try {
    await Promise.all([loadVisits(), loadAppointments(), loadDesk()])
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function changeStatus(visit: VisitRow, status: string) {
  error.value = ''
  try {
    await api(`/visits/${visit.id}/status`, { method: 'POST', body: { status, version: visit.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
}

const transferring = ref<VisitRow | null>(null)

function onTransferred() {
  transferring.value = null
  refresh()
}

function onVisitSaved() {
  activeToken.value = null
  refresh()
}

async function cancelAppointment(row: AppointmentRow) {
  error.value = ''
  try {
    await api(`/appointments/${row.id}/cancel?version=${row.version}`, { method: 'POST' })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
}

const checkIn = reactive({ row: null as AppointmentRow | null, departmentId: null as number | null, saving: false, error: '', key: '' })

function openCheckIn(row: AppointmentRow) {
  const defaultDepartment = departments.value.find((d) => d.label === 'معاینه ابتدایی')?.value ?? departments.value[0]?.value ?? null
  Object.assign(checkIn, { row, departmentId: defaultDepartment, saving: false, error: '', key: newIdempotencyKey() })
}

async function submitCheckIn() {
  checkIn.saving = true
  checkIn.error = ''
  try {
    const admission = await api<Admission>(`/appointments/${checkIn.row!.id}/check-in`, {
      method: 'POST',
      idempotencyKey: checkIn.key,
      body: { departmentId: checkIn.departmentId, version: checkIn.row!.version },
    })
    checkIn.row = null
    flow.value?.resume(admission)
    await refresh()
  } catch (cause) {
    checkIn.error = errorMessage(cause)
    // Keep the key when the result is unknown so retry cannot charge twice.
    if (cause instanceof ApiError && !cause.outcomeUnknown) checkIn.key = newIdempotencyKey()
  } finally {
    checkIn.saving = false
  }
}

const booking = reactive({
  open: false,
  saving: false,
  error: '',
  key: '',
  patientId: null as number | null,
  doctorStaffId: null as number | null,
  date: null as string | null,
  time: '09:00',
  note: '',
})

function openBooking() {
  Object.assign(booking, { open: true, saving: false, error: '', key: newIdempotencyKey(), patientId: null, doctorStaffId: null, date: todayIso(), time: '09:00', note: '' })
}

async function submitBooking() {
  if (!booking.patientId || !booking.date) {
    booking.error = 'مریض و تاریخ را انتخاب کنید.'
    return
  }
  booking.saving = true
  booking.error = ''
  try {
    await api('/appointments', {
      method: 'POST',
      idempotencyKey: booking.key,
      body: { patientId: booking.patientId, doctorStaffId: booking.doctorStaffId, scheduledAt: kabulToUtc(booking.date, booking.time), note: booking.note },
    })
    booking.open = false
    await refresh()
  } catch (cause) {
    booking.error = errorMessage(cause)
    if (cause instanceof ApiError && cause.status !== 0) booking.key = newIdempotencyKey()
  } finally {
    booking.saving = false
  }
}

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'ConsultationCompleted', 'InvoiceIssued', 'PaymentReceived'], () => {
  flow.value?.refresh()
  return refresh()
})

onMounted(async () => {
  try {
    const [loadedDepartments, loadedDoctors] = await Promise.all([departmentOptions(), doctorOptions()])
    departments.value = loadedDepartments
    doctors.value = loadedDoctors
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
})

watch(departmentId, refresh)
</script>

<template>
  <div class="d-grid gap-4">
    <div class="reception-heading">
      <div>
        <h1 class="h4 mb-1">پذیرش امروز</h1>
        <p class="text-muted mb-0">مریض را انتخاب کنید و مراجعه را ثبت کنید. فیس در صندوق گرفته می‌شود و با پرداخت آن، شماره نوبت خودکار صادر می‌شود.</p>
      </div>
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <div v-if="session" class="alert alert-warning mb-0 d-flex flex-wrap align-items-center justify-content-between gap-2" role="alert">
      <span>شما از قبل یک صندوق باز دارید ({{ session.registerName }}). پذیرش دیگر پول نمی‌گیرد؛ پول آن را تحویل دهید و این صندوق را ببندید.</span>
      <button type="button" class="btn btn-soft text-nowrap" @click="cash.closing = true; cash.counted = 0; cash.error = ''">بستن صندوق</button>
    </div>

    <UiCard v-if="canAdmit && auth.can('visits.create')" title="پذیرش مریض">
      <AdmissionFlow ref="flow" :patient-id="patientFromLink" @changed="refresh" />
    </UiCard>

    <div class="reception-stats">
      <StatCard label="مراجعات امروز" :value="totalVisits" icon="ri-group-fill" />
      <StatCard label="منتظر پرداخت" :value="counts.awaiting" icon="ri-money-dollar-circle-fill" tone="red" />
      <StatCard label="در جریان" :value="counts.waiting" icon="ri-hourglass-fill" tone="orange" />
      <StatCard label="تکمیل‌شده" :value="counts.completed" icon="ri-check-line" tone="cyan" />
      <StatCard label="لغو شده" :value="counts.cancelled" icon="ri-close-line" tone="red" />
    </div>

    <UiCard v-if="canAdmit" title="منتظر پرداخت در صندوق">
      <CallNotice :desks="[calls]" />
      <div v-if="tokens.length === 0 && pending.length === 0" class="xr-state">مریضی منتظر پرداخت نیست.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>شماره نوبت</th><th>نوع کار</th><th>مریض</th><th>وضعیت پرداخت</th><th v-if="tokens.length">فراخوان</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="a in pending" :key="`a${a.visitId}`">
              <td><span class="xr-queue">{{ quantity(a.queueNumber) }}</span></td>
              <td>فیس معاینه — {{ a.departmentName }}<span v-if="a.visitDate !== todayIso()" class="text-muted small"> ({{ formatDate(a.visitDate) }})</span></td>
              <td><PatientCell :name="a.patientName" :file-number="a.patientFileNumber" /></td>
              <td>{{ a.paid ? 'پرداخت‌شده، نوبت صادر نشده' : `${money(a.balance)}، پرداخت در صندوق` }}</td>
              <td v-if="tokens.length"></td>
              <td class="text-nowrap">
                <button type="button" class="btn btn-soft btn-sm" @click="flow?.resume(a)">{{ a.paid ? 'صدور نوبت' : 'مشاهده' }}</button>
                <button v-if="!a.paid && auth.can('visits.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelAdmission(a)">لغو</button>
              </td>
            </tr>
            <tr v-for="t in tokens" :key="`t${t.id}`">
              <td><span class="xr-queue">{{ quantity(t.queueNumber) }}</span></td>
              <td>نوبت قدیمی — {{ t.departmentName }}</td>
              <td>مشخصات ثبت نشده</td>
              <td>{{ t.paid ? 'فیس دریافت شده' : `${money(t.balance)}، پرداخت در صندوق` }}</td>
              <td><CallControls :desk="calls" :token-id="t.id" /></td>
              <td class="text-nowrap">
                <button v-if="t.paid" type="button" class="btn btn-soft btn-sm" @click="activeToken = t">ثبت مشخصات</button>
                <button v-else type="button" class="btn btn-soft-danger btn-sm" @click="cancelToken(t)">لغو</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="مراجعات امروز">
      <template #actions>
        <div class="d-flex gap-2">
          <select v-model.number="departmentId" class="form-select" aria-label="فیلتر بخش">
            <option :value="null">همه بخش‌ها</option>
            <option v-for="d in departments" :key="d.value" :value="d.value">{{ d.label }}</option>
          </select>
        </div>
      </template>

      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="visits.length === 0" class="xr-state">امروز مراجعه‌ای ثبت نشده است.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>نوبت</th><th>مریض</th><th>بخش و داکتر</th><th>وضعیت</th><th v-if="auth.can('visits.edit') || auth.can('visits.manage')">عملیات</th></tr>
          </thead>
          <tbody>
            <tr v-for="visit in visits" :key="visit.id">
              <td><span class="xr-queue">{{ quantity(visit.queueNumber) }}</span></td>
              <td><PatientCell :name="visit.patientName" :file-number="visit.patientFileNumber" :to="`/patients/${visit.patientId}`" /></td>
              <td>
                <div>{{ visit.departmentName }}</div>
                <div v-if="visit.doctorName" class="xr-meta">{{ visit.doctorName }}</div>
              </td>
              <td>
                <div class="xr-stack">
                  <span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span>
                  <span v-if="feeStatusLabels[visit.feeStatus]" class="xr-note" :class="`xr-note--${feeStatusLabels[visit.feeStatus].tone}`">{{ feeStatusLabels[visit.feeStatus].label }}</span>
                </div>
              </td>
              <td v-if="auth.can('visits.edit') || auth.can('visits.manage')">
                <div class="xr-row-actions">
                  <RouterLink v-if="auth.can('visits.print') && visit.queueNumber !== null" :to="`/print/visit/${visit.id}`" target="_blank" class="btn btn-quiet btn-sm text-nowrap">رسید</RouterLink>
                  <button v-if="auth.can('visits.manage') && visit.nextStatuses.length" type="button" class="btn btn-quiet btn-sm text-nowrap" @click="transferring = visit">انتقال داکتر</button>
                  <select
                    v-if="auth.can('visits.edit') && visit.nextStatuses.length"
                    class="form-select form-select-sm"
                    :aria-label="`تغییر وضعیت ${visit.patientName}`"
                    @change="changeStatus(visit, ($event.target as HTMLSelectElement).value)"
                  >
                    <option value="" selected disabled>انتخاب</option>
                    <option v-for="next in visit.nextStatuses.filter((s) => s !== 'Cancelled' || auth.can('visits.cancel'))" :key="next" :value="next">{{ visitStatusLabels[next] }}</option>
                  </select>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard v-if="auth.can('appointments.view')" title="وقت‌های ملاقات امروز">
      <template #actions>
        <button v-if="auth.can('appointments.create')" type="button" class="btn btn-soft" @click="openBooking">ثبت وقت</button>
      </template>
      <p class="form-hint">وقت ملاقات فقط زمان حضور است و شماره نوبت نیست؛ در روز حضور، فیس به صندوق فرستاده می‌شود و شماره نوبت پس از پرداخت در صندوق صادر می‌شود.</p>
      <div v-if="appointments.length === 0" class="xr-state">برای امروز وقت ملاقات ثبت نشده است.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>ساعت</th><th>مریض</th><th>داکتر</th><th>وضعیت</th><th>عملیات</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in appointments" :key="row.id">
              <td><span class="ltr-num">{{ formatTime(row.scheduledAt) }}</span></td>
              <td><RouterLink :to="`/patients/${row.patientId}`">{{ row.patientName }}</RouterLink></td>
              <td>{{ row.doctorName }}</td>
              <td>{{ appointmentLabels[row.status] }}</td>
              <td class="text-nowrap">
                <template v-if="row.status === 'Scheduled'">
                  <button v-if="auth.can('visits.create') && auth.can('visitfees.create')" type="button" class="btn btn-primary btn-sm" @click="openCheckIn(row)">پذیرش</button>
                  <button v-if="auth.can('appointments.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelAppointment(row)">لغو</button>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <NewVisitModal v-if="activeToken" :token="activeToken" @close="closeVisitModal" @saved="onVisitSaved" />

  <TransferDoctorModal v-if="transferring" :visit="transferring" @close="transferring = null" @saved="onTransferred" />

  <UiModal v-if="cash.closing && session" title="بستن صندوق" @close="cash.closing = false">
    <form @submit.prevent="closeCash">
      <p>مبلغ مورد انتظار: {{ money(session.openingAmount + session.cashCollected) }}</p>
      <label class="form-label" for="r-counted">مبلغ شمارش‌شده</label>
      <input id="r-counted" v-model.number="cash.counted" class="form-control mb-3" type="number" min="0" step="0.01" required />
      <div v-if="cash.error" class="alert alert-danger" role="alert">{{ cash.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="cash.saving">بستن صندوق</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="checkIn.row" :title="`پذیرش وقت ملاقات — ${checkIn.row.patientName}`" @close="checkIn.row = null">
    <form @submit.prevent="submitCheckIn">
      <dl class="xr-facts mb-3">
        <div><dt>مریض</dt><dd>{{ checkIn.row.patientName }} <span class="ltr-num">{{ checkIn.row.patientFileNumber }}</span></dd></div>
        <div><dt>داکتر</dt><dd>{{ checkIn.row.doctorName }}</dd></div>
        <div><dt>ساعت</dt><dd><span class="ltr-num">{{ formatTime(checkIn.row.scheduledAt) }}</span></dd></div>
      </dl>
      <div class="mb-3"><UiComboBox v-model="checkIn.departmentId" label="بخش شروع مراجعه" :options="departments" required chip /></div>
      <p class="form-hint">با تأیید، مراجعه ثبت می‌شود و فیس برای پرداخت به صندوق فرستاده می‌شود.</p>
      <div v-if="checkIn.error" class="alert alert-danger" role="alert">{{ checkIn.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="checkIn.saving || !checkIn.departmentId">{{ checkIn.saving ? 'در حال ثبت…' : 'ثبت مراجعه و ارسال فیس به صندوق' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="booking.open" title="ثبت وقت ملاقات" @close="booking.open = false">
    <form @submit.prevent="submitBooking">
      <div class="mb-3"><PatientPicker v-model="booking.patientId" required /></div>
      <div class="mb-3">
        <UiComboBox v-model="booking.doctorStaffId" label="داکتر" :options="doctors" required chip />
      </div>
      <div class="row g-3 mb-3">
        <div class="col-8"><ShamsiDateInput v-model="booking.date" label="تاریخ" required :min="todayIso()" /></div>
        <div class="col-4">
          <label class="form-label" for="b-time">ساعت</label>
          <input id="b-time" v-model="booking.time" class="form-control" type="time" dir="ltr" required />
        </div>
      </div>
      <div class="mb-3">
        <label class="form-label" for="b-note">یادداشت</label>
        <input id="b-note" v-model="booking.note" class="form-control" maxlength="300" />
      </div>
      <div v-if="booking.error" class="alert alert-danger" role="alert">{{ booking.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="booking.saving">ثبت وقت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.reception-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--xr-gap);
}

.reception-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--xr-gap);
}

@media (max-width: 575.98px) {
  .reception-stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .reception-heading {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
