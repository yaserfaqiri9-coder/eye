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
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useAuthStore } from '@/stores/auth'
import { formatTime, kabulToUtc, todayIso } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import NewVisitModal from './NewVisitModal.vue'
import TransferDoctorModal from './TransferDoctorModal.vue'
import { visitStatusLabels, visitStatusTones, type ReceptionFee, type VisitRow, type VisitToken } from './types'

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
const calls = useCallDesk('Reception')
const visits = ref<VisitRow[]>([])
const totalVisits = ref(0)
const appointments = ref<AppointmentRow[]>([])
const departments = ref<ComboOption[]>([])
const doctors = ref<ComboOption[]>([])
const visitFee = ref<number | null>(null)
const departmentId = ref<number | null>(null)
const loading = ref(true)
const error = ref('')
const addingVisit = ref(false)
const activeToken = ref<VisitToken | null>(null)
const tokens = ref<VisitToken[]>([])
const fees = ref<ReceptionFee[]>([])

interface CashSession {
  id: number
  registerName: string
  openingAmount: number
  cashCollected: number
  closedAt: string | null
  version: number
}

const canCollect = computed(() => auth.can('visitfees.create'))
const session = ref<CashSession | null>(null)
const registers = ref<{ id: number; name: string }[]>([])
const cash = reactive({ registerId: null as number | null, opening: 0, closing: false, counted: 0, saving: false, error: '' })

async function loadDesk() {
  if (!auth.can('visitfees.view')) return
  const [loadedTokens, loadedFees, sessions] = await Promise.all([
    api<VisitToken[]>('/visit-tokens'),
    api<ReceptionFee[]>('/reception-fees'),
    api<CashSession[]>('/cash/sessions?mine=true'),
  ])
  tokens.value = loadedTokens
  fees.value = loadedFees
  session.value = sessions.find((s) => !s.closedAt) ?? null
  await calls.load()
}

async function openCash() {
  cash.saving = true
  cash.error = ''
  try {
    await api('/cash/sessions', { method: 'POST', body: { registerId: cash.registerId, openingAmount: cash.opening } })
    await loadDesk()
  } catch (cause) {
    cash.error = errorMessage(cause)
  } finally {
    cash.saving = false
  }
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

const collecting = reactive({ fee: null as ReceptionFee | null, saving: false, error: '', key: '' })

function openFee(fee: ReceptionFee) {
  Object.assign(collecting, { fee, saving: false, error: '', key: newIdempotencyKey() })
}

async function submitFee() {
  collecting.saving = true
  collecting.error = ''
  try {
    const fee = collecting.fee!
    await api('/reception-fees/pay', { method: 'POST', idempotencyKey: collecting.key, body: { invoiceId: fee.invoiceId, amount: fee.balance, method: 'Cash', invoiceVersion: fee.invoiceVersion } })
    collecting.fee = null
    await loadDesk()
  } catch (cause) {
    collecting.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) collecting.key = newIdempotencyKey()
    await loadDesk()
  } finally {
    collecting.saving = false
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

function closeVisitModal() {
  addingVisit.value = false
  activeToken.value = null
  loadDesk().catch((cause) => (error.value = errorMessage(cause)))
}

const counts = computed(() => ({
  waiting: visits.value.filter((v) => !['Completed', 'Cancelled'].includes(v.status)).length,
  completed: visits.value.filter((v) => v.status === 'Completed').length,
  cancelled: visits.value.filter((v) => v.status === 'Cancelled').length,
}))

async function loadVisits() {
  const params = new URLSearchParams({ pageSize: '100' })
  if (departmentId.value) params.set('departmentId', String(departmentId.value))
  const page = await api<{ items: VisitRow[]; total: number }>(`/visits?${params}`)
  visits.value = page.items
  totalVisits.value = page.total
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
  addingVisit.value = false
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
    await api(`/appointments/${checkIn.row!.id}/check-in`, {
      method: 'POST',
      idempotencyKey: checkIn.key,
      body: { departmentId: checkIn.departmentId, version: checkIn.row!.version },
    })
    checkIn.row = null
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

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'ConsultationCompleted', 'InvoiceIssued', 'PaymentReceived'], refresh)

onMounted(async () => {
  try {
    const [loadedDepartments, loadedDoctors, loadedFee] = await Promise.all([
      departmentOptions(),
      doctorOptions(),
      auth.can('visitfees.view') ? api<{ price: number }>('/visit-tokens/fee') : Promise.resolve(null),
    ])
    departments.value = loadedDepartments
    doctors.value = loadedDoctors
    visitFee.value = loadedFee?.price ?? null
    if (auth.can('cash.create')) {
      registers.value = await api<typeof registers.value>('/cash/registers')
      cash.registerId = registers.value.find((r) => r.name === 'صندوق پذیرش')?.id ?? registers.value[0]?.id ?? null
    }
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
        <p class="text-muted mb-0">مریض حضوری را از اینجا پذیرش کنید؛ وقت‌های قبلی در بخش پایین با یک مرحله تکمیل می‌شوند.</p>
      </div>
      <button v-if="auth.can('visits.create')" type="button" class="btn btn-primary text-nowrap" @click="addingVisit = true">پذیرش مریض حضوری</button>
    </div>

    <div class="reception-stats">
      <StatCard label="مراجعات امروز" :value="totalVisits" icon="ri-group-fill" />
      <StatCard label="در جریان" :value="counts.waiting" icon="ri-hourglass-fill" tone="orange" />
      <StatCard label="تکمیل‌شده" :value="counts.completed" icon="ri-check-line" tone="cyan" />
      <StatCard label="لغو شده" :value="counts.cancelled" icon="ri-close-line" tone="red" />
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <UiCard v-if="canCollect" title="در انتظار تکمیل پذیرش">
      <template #actions>
        <button v-if="session" type="button" class="btn btn-soft text-nowrap" @click="cash.closing = true; cash.counted = 0; cash.error = ''">بستن صندوق</button>
      </template>
      <form v-if="!session" class="row g-3 align-items-end" @submit.prevent="openCash">
        <div class="col-md-4">
          <label class="form-label" for="r-register">صندوق</label>
          <select id="r-register" v-model.number="cash.registerId" class="form-select" required>
            <option v-for="r in registers" :key="r.id" :value="r.id">{{ r.name }}</option>
          </select>
        </div>
        <div class="col-md-4">
          <label class="form-label" for="r-opening">موجودی شروع</label>
          <input id="r-opening" v-model.number="cash.opening" class="form-control" type="number" min="0" step="0.01" required />
        </div>
        <div class="col-md-4"><button type="submit" class="btn btn-primary xr-field-height" :disabled="cash.saving">باز کردن صندوق</button></div>
        <div v-if="cash.error" class="col-12"><div class="alert alert-danger mb-0" role="alert">{{ cash.error }}</div></div>
      </form>
      <dl v-else class="xr-facts mb-3">
        <div><dt>صندوق باز</dt><dd>{{ session.registerName }}</dd></div>
        <div><dt>دریافت نقدی</dt><dd>{{ money(session.cashCollected) }}</dd></div>
      </dl>

      <CallNotice :desks="[calls]" />
      <div v-if="tokens.length === 0 && fees.length === 0" class="xr-state">موردی در انتظار تکمیل پذیرش نیست.</div>
      <div v-if="tokens.length || fees.length" class="xr-table-wrap" :class="{ 'mt-3': !session }">
        <table class="xr-table">
          <thead><tr><th>شماره صف</th><th>نوع کار</th><th>مریض</th><th>وضعیت فیس</th><th>فراخوان</th><th>عملیات</th></tr></thead>
          <tbody>
            <tr v-for="t in tokens" :key="`t${t.id}`">
              <td>{{ quantity(t.queueNumber) }}</td>
              <td>پذیرش حضوری — {{ t.departmentName }}</td>
              <td>مشخصات ثبت نشده</td>
              <td>{{ t.paid ? 'فیس دریافت شده' : money(t.balance) }}</td>
              <td><CallControls :desk="calls" :token-id="t.id" /></td>
              <td class="text-nowrap">
                <button type="button" class="btn btn-soft btn-sm" :disabled="!t.paid && !session" @click="activeToken = t">{{ t.paid ? 'ثبت مشخصات' : 'دریافت فیس' }}</button>
                <button v-if="!t.paid" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelToken(t)">لغو</button>
              </td>
            </tr>
            <tr v-for="f in fees" :key="`f${f.invoiceId}`">
              <td>—</td>
              <td>فیس خدمت — {{ f.description }}</td>
              <td>{{ f.patientName }} <span class="ltr-num">{{ f.patientFileNumber }}</span></td>
              <td>{{ money(f.balance) }}</td>
              <td></td>
              <td><button type="button" class="btn btn-soft btn-sm" :disabled="!session" @click="openFee(f)">دریافت فیس</button></td>
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
            <tr><th>شماره صف</th><th>مریض</th><th>شماره پرونده</th><th>بخش</th><th>داکتر</th><th>وضعیت</th><th v-if="auth.can('visits.edit') || auth.can('visits.manage')">تغییر وضعیت</th></tr>
          </thead>
          <tbody>
            <tr v-for="visit in visits" :key="visit.id">
              <td>{{ quantity(visit.queueNumber) }}</td>
              <td><RouterLink :to="`/patients/${visit.patientId}`">{{ visit.patientName }}</RouterLink></td>
              <td><span class="ltr-num">{{ visit.patientFileNumber }}</span></td>
              <td>{{ visit.departmentName }}</td>
              <td>{{ visit.doctorName ?? '—' }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span></td>
              <td v-if="auth.can('visits.edit') || auth.can('visits.manage')">
                <div class="xr-row-actions">
                  <RouterLink v-if="auth.can('visits.print')" :to="`/print/visit/${visit.id}`" target="_blank" class="btn btn-soft btn-sm text-nowrap">رسید</RouterLink>
                  <button v-if="auth.can('visits.manage') && visit.nextStatuses.length" type="button" class="btn btn-soft btn-sm text-nowrap" @click="transferring = visit">انتقال داکتر</button>
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

    <UiCard v-if="auth.can('appointments.view')" title="وقت‌های قبلی امروز">
      <template #actions>
        <button v-if="auth.can('appointments.create')" type="button" class="btn btn-soft" @click="openBooking">ثبت وقت</button>
      </template>
      <div v-if="!session && appointments.some((row) => row.status === 'Scheduled')" class="alert alert-warning" role="status">برای پذیرش وقت قبلی و دریافت فیس، ابتدا صندوق را باز کنید.</div>
      <div v-if="appointments.length === 0" class="xr-state">برای امروز وقت قبلی ثبت نشده است.</div>
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
                  <button v-if="auth.can('visits.create') && auth.can('visitfees.create')" type="button" class="btn btn-primary btn-sm" :disabled="!session" :title="session ? undefined : 'ابتدا صندوق را باز کنید'" @click="openCheckIn(row)">پذیرش و دریافت فیس</button>
                  <button v-if="auth.can('appointments.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelAppointment(row)">لغو</button>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <NewVisitModal v-if="addingVisit || activeToken" :token="activeToken ?? undefined" @close="closeVisitModal" @changed="loadDesk" @saved="onVisitSaved" />

  <UiModal v-if="collecting.fee" :title="`دریافت ${collecting.fee.description}`" @close="collecting.fee = null">
    <form @submit.prevent="submitFee">
      <p>{{ collecting.fee.patientName }}، مبلغ کامل: <strong>{{ money(collecting.fee.balance) }}</strong></p>
      <div v-if="collecting.error" class="alert alert-danger" role="alert">{{ collecting.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="collecting.saving">ثبت دریافت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

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

  <UiModal v-if="checkIn.row" :title="`پذیرش و دریافت فیس — ${checkIn.row.patientName}`" @close="checkIn.row = null">
    <form @submit.prevent="submitCheckIn">
      <dl class="xr-facts mb-3">
        <div><dt>مریض</dt><dd>{{ checkIn.row.patientName }} <span class="ltr-num">{{ checkIn.row.patientFileNumber }}</span></dd></div>
        <div><dt>داکتر</dt><dd>{{ checkIn.row.doctorName }}</dd></div>
        <div><dt>ساعت</dt><dd><span class="ltr-num">{{ formatTime(checkIn.row.scheduledAt) }}</span></dd></div>
        <div><dt>فیس ویزیت</dt><dd>{{ visitFee === null ? 'در حال دریافت قیمت…' : money(visitFee) }}</dd></div>
      </dl>
      <div class="mb-3"><UiComboBox v-model="checkIn.departmentId" label="بخش شروع مراجعه" :options="departments" required chip /></div>
      <p class="form-hint">با تأیید، فیس نقدی دریافت، شماره صف ساخته و مریض مستقیماً وارد مراجعات امروز می‌شود.</p>
      <div v-if="checkIn.error" class="alert alert-danger" role="alert">{{ checkIn.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="checkIn.saving || !checkIn.departmentId || visitFee === null">{{ checkIn.saving ? 'در حال ثبت…' : 'تأیید دریافت فیس و پذیرش' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="booking.open" title="ثبت وقت" @close="booking.open = false">
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
  .reception-heading {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
