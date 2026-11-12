<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { departmentOptions, doctorOptions } from '@/api/lookups'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatTime, kabulToUtc, todayIso, utcToKabulParts } from '@/utils/date'
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
  doctorStaffId: number
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
  if (!window.confirm(`نوبت ${quantity(token.queueNumber)} لغو شود؟`)) return
  error.value = ''
  try {
    await api(`/visit-tokens/${token.id}/cancel`, { method: 'POST', body: { version: token.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await loadDesk()
}

async function cancelAdmission(row: Admission) {
  if (!window.confirm(`مراجعه «${row.patientName}» لغو شود؟`)) return
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

type Tab = 'pay' | 'active' | 'appointments' | 'done'

/** One queue with one tab per question the desk asks: who still pays, who is inside, who has an appointment, who is finished. */
const tab = ref<Tab>('active')
const tabChosen = ref(false)
const search = ref('')
const flowCard = ref<HTMLElement | null>(null)
const showPayTab = computed(() => canAdmit.value && auth.can('visitfees.view'))

function matches(...values: (string | number | null | undefined)[]) {
  const q = search.value.trim()
  if (!q) return true
  return values.some((v) => v !== null && v !== undefined && String(v).includes(q))
}

const isDone = (v: VisitRow) => ['Completed', 'Cancelled'].includes(v.status)
const activeVisits = computed(() => visits.value.filter((v) => !isDone(v) && !(showPayTab.value && v.status === 'AwaitingPayment')))
const doneVisits = computed(() => visits.value.filter(isDone))
const scheduled = computed(() => appointments.value.filter((a) => a.status === 'Scheduled'))

const tabs = computed(() => {
  const list: { key: Tab; label: string; count: number }[] = []
  if (showPayTab.value) list.push({ key: 'pay', label: 'منتظر صندوق', count: pending.value.length + tokens.value.length })
  list.push({ key: 'active', label: 'در جریان', count: activeVisits.value.length })
  if (auth.can('appointments.view')) list.push({ key: 'appointments', label: 'وقت ملاقات', count: scheduled.value.length })
  list.push({ key: 'done', label: 'تمام‌شده', count: doneVisits.value.length })
  return list
})

const shownPending = computed(() => pending.value.filter((a) => matches(a.patientName, a.patientFileNumber, a.queueNumber)))
const shownTokens = computed(() => tokens.value.filter((t) => matches(t.queueNumber, t.departmentName)))
const shownVisits = computed(() => (tab.value === 'done' ? doneVisits.value : activeVisits.value).filter((v) => matches(v.patientName, v.patientFileNumber, v.queueNumber)))
const shownAppointments = computed(() => appointments.value.filter((a) => matches(a.patientName, a.patientFileNumber)))

function chooseTab(key: Tab) {
  tab.value = key
  tabChosen.value = true
}

/** The first look lands where work waits; after that the desk keeps the tab it chose. */
function pickDefaultTab() {
  if (tabChosen.value) return
  tab.value = showPayTab.value && pending.value.length + tokens.value.length > 0 ? 'pay' : 'active'
}

async function onFlowChanged() {
  await refresh()
  // A visit just sent to the cashier is shown where it now waits.
  if (pending.value.length) tab.value = 'pay'
}

function openInFlow(row: Admission) {
  flow.value?.resume(row)
  flowCard.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function visitMenu(visit: VisitRow) {
  const items: { label: string; danger?: boolean; onSelect: () => void }[] = []
  if (!visit.nextStatuses.length) return items
  if (auth.can('visits.manage')) items.push({ label: 'انتقال داکتر', onSelect: () => (transferring.value = visit) })
  if (auth.can('visits.edit')) {
    items.push({ label: 'ویرایش دلیل مراجعه', onSelect: () => Object.assign(reasonEdit, { visit, reason: visit.reason ?? '', saving: false, error: '' }) })
    for (const next of visit.nextStatuses) {
      if (next === 'Cancelled') {
        if (auth.can('visits.cancel')) items.push({ label: 'لغو مراجعه', danger: true, onSelect: () => cancelVisit(visit) })
      } else {
        items.push({ label: `انتقال به: ${visitStatusLabels[next]}`, onSelect: () => changeStatus(visit, next) })
      }
    }
  }
  return items
}

function cancelVisit(visit: VisitRow) {
  if (window.confirm(`مراجعه «${visit.patientName}» لغو شود؟`)) void changeStatus(visit, 'Cancelled')
}

function appointmentMenu(row: AppointmentRow) {
  const items: { label: string; danger?: boolean; onSelect: () => void }[] = []
  if (auth.can('appointments.edit')) items.push({ label: 'ویرایش وقت', onSelect: () => editBooking(row) })
  if (auth.can('appointments.cancel')) items.push({ label: 'لغو وقت', danger: true, onSelect: () => cancelAppointment(row) })
  return items
}

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
    pickDefaultTab()
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

const reasonEdit = reactive({ visit: null as VisitRow | null, reason: '', saving: false, error: '' })

async function saveReason() {
  reasonEdit.saving = true
  reasonEdit.error = ''
  try {
    await api(`/visits/${reasonEdit.visit!.id}/reason`, { method: 'PUT', body: { reason: reasonEdit.reason, version: reasonEdit.visit!.version } })
    reasonEdit.visit = null
    await refresh()
  } catch (cause) {
    reasonEdit.error = errorMessage(cause)
  } finally {
    reasonEdit.saving = false
  }
}

async function cancelAppointment(row: AppointmentRow) {
  if (!window.confirm(`وقت ملاقات «${row.patientName}» لغو شود؟`)) return
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
  editing: null as AppointmentRow | null,
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
  Object.assign(booking, { open: true, editing: null, saving: false, error: '', key: newIdempotencyKey(), patientId: null, doctorStaffId: null, date: todayIso(), time: '09:00', note: '' })
}

function editBooking(row: AppointmentRow) {
  const { date, time } = utcToKabulParts(row.scheduledAt)
  Object.assign(booking, { open: true, editing: row, saving: false, error: '', key: '', patientId: row.patientId, doctorStaffId: row.doctorStaffId, date, time, note: row.note ?? '' })
}

async function submitBooking() {
  if (!booking.patientId || !booking.date) {
    booking.error = 'مریض و تاریخ را انتخاب کنید.'
    return
  }
  booking.saving = true
  booking.error = ''
  try {
    const body = { patientId: booking.patientId, doctorStaffId: booking.doctorStaffId, scheduledAt: kabulToUtc(booking.date, booking.time), note: booking.note }
    if (booking.editing) await api(`/appointments/${booking.editing.id}`, { method: 'PUT', body: { ...body, version: booking.editing.version } })
    else await api('/appointments', { method: 'POST', idempotencyKey: booking.key, body })
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
      <h1 class="h4 mb-0">پذیرش</h1>
      <button v-if="auth.can('appointments.create')" type="button" class="btn btn-soft" @click="openBooking"><i class="ri-calendar-line" aria-hidden="true"></i> ثبت وقت ملاقات</button>
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <div v-if="session" class="alert alert-warning mb-0 d-flex flex-wrap align-items-center justify-content-between gap-2" role="alert">
      <span>یک صندوق قدیمی به نام شما باز مانده ({{ session.registerName }}). پول آن را تحویل دهید و ببندید.</span>
      <button type="button" class="btn btn-soft text-nowrap" @click="cash.closing = true; cash.counted = 0; cash.error = ''">بستن صندوق</button>
    </div>

    <div v-if="canAdmit && auth.can('visits.create')" ref="flowCard">
      <UiCard title="پذیرش مریض">
        <AdmissionFlow ref="flow" :patient-id="patientFromLink" @changed="onFlowChanged" />
      </UiCard>
    </div>

    <section class="xr-card">
      <div class="xr-card__header flex-wrap gap-2">
        <h2 class="xr-card__title">صف امروز</h2>
        <div class="reception-filters">
          <input v-model="search" type="search" class="form-control" placeholder="جستجو: نام، شماره پرونده یا نوبت" aria-label="جستجو در صف" />
          <select v-model.number="departmentId" class="form-select" aria-label="فیلتر بخش">
            <option :value="null">همه بخش‌ها</option>
            <option v-for="d in departments" :key="d.value" :value="d.value">{{ d.label }}</option>
          </select>
        </div>
      </div>
      <div class="xr-card__body">
        <nav class="xr-tabs reception-tabs" role="tablist" aria-label="صف امروز">
          <button
            v-for="t in tabs"
            :key="t.key"
            type="button"
            role="tab"
            class="xr-tab"
            :class="{ 'xr-tab--active': tab === t.key }"
            :aria-selected="tab === t.key"
            @click="chooseTab(t.key)"
          >
            {{ t.label }} <span class="reception-tabs__count">{{ quantity(t.count) }}</span>
          </button>
        </nav>

        <UiListSkeleton v-if="loading" />

        <!-- Sent to the cashier: the fee is paid there, then the number comes by itself. -->
        <template v-else-if="tab === 'pay'">
          <CallNotice :desks="[calls]" />
          <div v-if="shownPending.length === 0 && shownTokens.length === 0" class="xr-state">مریضی منتظر صندوق نیست.</div>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>مریض</th><th>بخش</th><th>وضعیت</th><th v-if="tokens.length">فراخوان</th><th><span class="visually-hidden">عملیات</span></th></tr></thead>
              <tbody>
                <tr v-for="a in shownPending" :key="`a${a.visitId}`">
                  <td><PatientCell :name="a.patientName" :file-number="a.patientFileNumber" /></td>
                  <td>{{ a.departmentName }}<span v-if="a.visitDate !== todayIso()" class="text-muted small"> ({{ formatDate(a.visitDate) }})</span></td>
                  <td>
                    <span v-if="a.paid" class="xr-badge xr-badge--orange">پرداخت شد، نوبت صادر نشده</span>
                    <span v-else class="xr-badge xr-badge--red">باید {{ money(a.balance) }} در صندوق بپردازد</span>
                  </td>
                  <td v-if="tokens.length"></td>
                  <td class="text-nowrap text-end">
                    <button type="button" class="btn btn-sm" :class="a.paid ? 'btn-primary' : 'btn-soft'" @click="openInFlow(a)">{{ a.paid ? 'صدور نوبت' : 'باز کردن' }}</button>
                    <button v-if="!a.paid && auth.can('visits.cancel')" type="button" class="btn btn-quiet btn-sm ms-1 text-danger" @click="cancelAdmission(a)">لغو</button>
                  </td>
                </tr>
                <tr v-for="t in shownTokens" :key="`t${t.id}`">
                  <td>نوبت <span class="xr-queue">{{ quantity(t.queueNumber) }}</span> — بدون مشخصات مریض</td>
                  <td>{{ t.departmentName }}</td>
                  <td>
                    <span v-if="t.paid" class="xr-badge xr-badge--green">فیس گرفته شد</span>
                    <span v-else class="xr-badge xr-badge--red">باید {{ money(t.balance) }} در صندوق بپردازد</span>
                  </td>
                  <td><CallControls :desk="calls" :token-id="t.id" /></td>
                  <td class="text-nowrap text-end">
                    <button v-if="t.paid" type="button" class="btn btn-primary btn-sm" @click="activeToken = t">ثبت مشخصات</button>
                    <button v-else type="button" class="btn btn-quiet btn-sm text-danger" @click="cancelToken(t)">لغو</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>

        <template v-else-if="tab === 'appointments'">
          <div v-if="shownAppointments.length === 0" class="xr-state">برای امروز وقت ملاقات نیست.</div>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>ساعت</th><th>مریض</th><th>داکتر</th><th>وضعیت</th><th><span class="visually-hidden">عملیات</span></th></tr></thead>
              <tbody>
                <tr v-for="row in shownAppointments" :key="row.id">
                  <td><span class="ltr-num">{{ formatTime(row.scheduledAt) }}</span></td>
                  <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" :to="`/patients/${row.patientId}`" /></td>
                  <td>{{ row.doctorName }}</td>
                  <td>{{ appointmentLabels[row.status] }}</td>
                  <td class="text-nowrap text-end">
                    <template v-if="row.status === 'Scheduled'">
                      <button v-if="auth.can('visits.create') && auth.can('visitfees.create')" type="button" class="btn btn-primary btn-sm" @click="openCheckIn(row)">مریض آمد، پذیرش</button>
                      <UiRowMenu v-if="appointmentMenu(row).length" :label="`گزینه‌های وقت ${row.patientName}`" :items="appointmentMenu(row)" />
                    </template>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>

        <template v-else>
          <div v-if="shownVisits.length === 0" class="xr-state">{{ tab === 'done' ? 'هنوز مراجعه‌ای تمام نشده است.' : 'مریضی در جریان نیست.' }}</div>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>نوبت</th><th>مریض</th><th>بخش و داکتر</th><th>وضعیت</th><th><span class="visually-hidden">عملیات</span></th></tr></thead>
              <tbody>
                <tr v-for="visit in shownVisits" :key="visit.id">
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
                  <td class="text-nowrap text-end">
                    <RouterLink v-if="auth.can('visits.print') && visit.queueNumber !== null" :to="`/print/visit/${visit.id}`" target="_blank" class="btn btn-quiet btn-sm">
                      <i class="ri-printer-line" aria-hidden="true"></i> رسید
                    </RouterLink>
                    <UiRowMenu v-if="visitMenu(visit).length" :label="`گزینه‌های ${visit.patientName}`" :items="visitMenu(visit)" />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>
    </section>
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

  <UiModal v-if="reasonEdit.visit" :title="`دلیل مراجعه ${reasonEdit.visit.patientName}`" @close="reasonEdit.visit = null">
    <form @submit.prevent="saveReason">
      <p class="form-hint">مریض، بخش و نوبت تغییر نمی‌کند، چون فیس و شماره برای آن‌ها صادر شده است. اگر اشتباه است، مراجعه را لغو و دوباره ثبت کنید.</p>
      <label class="form-label" for="v-reason">دلیل مراجعه</label>
      <input id="v-reason" v-model="reasonEdit.reason" class="form-control mb-3" maxlength="300" />
      <div v-if="reasonEdit.error" class="alert alert-danger" role="alert">{{ reasonEdit.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="reasonEdit.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="booking.open" :title="booking.editing ? 'ویرایش وقت ملاقات' : 'ثبت وقت ملاقات'" @close="booking.open = false">
    <form @submit.prevent="submitBooking">
      <div class="mb-3"><PatientPicker v-model="booking.patientId" required :selected-label="booking.editing?.patientName" /></div>
      <div class="mb-3">
        <UiComboBox v-model="booking.doctorStaffId" label="داکتر" :options="doctors" :selected-label="booking.editing?.doctorName" required chip />
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
        <button type="submit" class="btn btn-primary" :disabled="booking.saving">{{ booking.editing ? 'ذخیره' : 'ثبت وقت' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.reception-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--xr-gap);
}

.reception-filters {
  display: flex;
  gap: 8px;
  margin-inline-start: auto;
}

.reception-filters .form-control {
  min-width: 260px;
}

.reception-filters .form-select {
  width: auto;
}

.reception-tabs {
  margin-bottom: 1rem;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--xr-border, #e5e7eb);
}

.reception-tabs .xr-tab {
  padding: 6px 14px;
  font-weight: 600;
}

.reception-tabs__count {
  display: inline-block;
  min-width: 22px;
  margin-inline-start: 4px;
  padding: 0 6px;
  border-radius: 10px;
  background: var(--xr-border, #e5e7eb);
  color: var(--xr-text);
  font-size: 12px;
  text-align: center;
}

.xr-tab--active .reception-tabs__count {
  background: var(--xr-primary);
  color: #fff;
}

@media (max-width: 575.98px) {
  .reception-filters {
    flex-direction: column;
    width: 100%;
  }

  .reception-filters .form-control,
  .reception-filters .form-select {
    min-width: 0;
    width: 100%;
  }
}
</style>
