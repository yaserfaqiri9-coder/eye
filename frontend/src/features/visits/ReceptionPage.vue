<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { patientName, type PatientListItem } from '@/features/patients/types'
import { useAuthStore } from '@/stores/auth'
import { formatTime, kabulToUtc, todayShamsi } from '@/utils/date'
import { quantity } from '@/utils/money'
import NewVisitModal from './NewVisitModal.vue'
import { visitStatusLabels, visitStatusTones, type LookupItem, type VisitRow } from './types'

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

const appointmentLabels: Record<string, string> = { Scheduled: 'نوبت', CheckedIn: 'پذیرش‌شده', Cancelled: 'لغو شده', NoShow: 'نیامده' }

const auth = useAuthStore()
const visits = ref<VisitRow[]>([])
const totalVisits = ref(0)
const appointments = ref<AppointmentRow[]>([])
const departments = ref<LookupItem[]>([])
const doctors = ref<LookupItem[]>([])
const departmentId = ref<number | null>(null)
const loading = ref(true)
const error = ref('')
const addingVisit = ref(false)

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
    await Promise.all([loadVisits(), loadAppointments()])
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

function onVisitSaved() {
  addingVisit.value = false
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
  Object.assign(checkIn, { row, departmentId: null, saving: false, error: '', key: newIdempotencyKey() })
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
  } finally {
    checkIn.saving = false
  }
}

const booking = reactive({
  open: false,
  saving: false,
  error: '',
  key: '',
  search: '',
  results: [] as PatientListItem[],
  patient: null as PatientListItem | null,
  doctorStaffId: null as number | null,
  date: null as string | null,
  time: '09:00',
  note: '',
})

function openBooking() {
  Object.assign(booking, { open: true, saving: false, error: '', key: newIdempotencyKey(), search: '', results: [], patient: null, doctorStaffId: null, date: null, time: '09:00', note: '' })
}

let bookingDebounce: ReturnType<typeof setTimeout> | undefined
watch(
  () => booking.search,
  (value) => {
    clearTimeout(bookingDebounce)
    if (!value.trim()) {
      booking.results = []
      return
    }
    bookingDebounce = setTimeout(async () => {
      try {
        booking.results = (await api<{ items: PatientListItem[] }>(`/patients?pageSize=8&q=${encodeURIComponent(value.trim())}`)).items
      } catch (cause) {
        booking.error = errorMessage(cause)
      }
    }, 300)
  },
)

async function submitBooking() {
  if (!booking.patient || !booking.date) {
    booking.error = 'مریض و تاریخ را انتخاب کنید.'
    return
  }
  booking.saving = true
  booking.error = ''
  try {
    await api('/appointments', {
      method: 'POST',
      idempotencyKey: booking.key,
      body: { patientId: booking.patient.id, doctorStaffId: booking.doctorStaffId, scheduledAt: kabulToUtc(booking.date, booking.time), note: booking.note },
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

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'ConsultationCompleted'], refresh)

onMounted(async () => {
  try {
    ;[departments.value, doctors.value] = await Promise.all([api<LookupItem[]>('/lookups/departments'), api<LookupItem[]>('/lookups/doctors')])
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
})

watch(departmentId, refresh)
</script>

<template>
  <div class="d-grid gap-4">
    <div class="reception-stats">
      <StatCard label="مراجعات امروز" :value="totalVisits" icon="ri-group-fill" />
      <StatCard label="در جریان" :value="counts.waiting" icon="ri-hourglass-fill" tone="orange" />
      <StatCard label="تکمیل‌شده" :value="counts.completed" icon="ri-check-line" tone="cyan" />
      <StatCard label="لغو شده" :value="counts.cancelled" icon="ri-close-line" tone="red" />
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <UiCard title="صف امروز">
      <template #actions>
        <div class="d-flex gap-2">
          <select v-model.number="departmentId" class="form-select" aria-label="فیلتر بخش">
            <option :value="null">همه بخش‌ها</option>
            <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
          </select>
          <button v-if="auth.can('visits.create')" type="button" class="btn btn-primary text-nowrap" @click="addingVisit = true">مراجعه جدید</button>
        </div>
      </template>

      <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
      <div v-else-if="visits.length === 0" class="py-4 text-center text-muted">امروز مراجعه‌ای ثبت نشده است.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>نوبت</th><th>مریض</th><th>شماره پرونده</th><th>بخش</th><th>داکتر</th><th>وضعیت</th><th v-if="auth.can('visits.edit')">تغییر وضعیت</th></tr>
          </thead>
          <tbody>
            <tr v-for="visit in visits" :key="visit.id">
              <td>{{ quantity(visit.queueNumber) }}</td>
              <td><RouterLink :to="`/patients/${visit.patientId}`">{{ visit.patientName }}</RouterLink></td>
              <td><span class="ltr-num">{{ visit.patientFileNumber }}</span></td>
              <td>{{ visit.departmentName }}</td>
              <td>{{ visit.doctorName ?? '—' }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span></td>
              <td v-if="auth.can('visits.edit')" class="d-flex gap-2">
                <RouterLink v-if="auth.can('visits.print')" :to="`/print/visit/${visit.id}`" target="_blank" class="btn btn-soft btn-sm text-nowrap">رسید</RouterLink>
                <select
                  v-if="visit.nextStatuses.length"
                  class="form-select form-select-sm"
                  :aria-label="`تغییر وضعیت ${visit.patientName}`"
                  @change="changeStatus(visit, ($event.target as HTMLSelectElement).value)"
                >
                  <option value="" selected disabled>انتخاب</option>
                  <option v-for="next in visit.nextStatuses.filter((s) => s !== 'Cancelled' || auth.can('visits.cancel'))" :key="next" :value="next">{{ visitStatusLabels[next] }}</option>
                </select>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard v-if="auth.can('appointments.view')" title="نوبت‌های امروز">
      <template #actions>
        <button v-if="auth.can('appointments.create')" type="button" class="btn btn-soft" @click="openBooking">ثبت نوبت</button>
      </template>
      <div v-if="appointments.length === 0" class="py-3 text-center text-muted">برای امروز نوبتی ثبت نشده است.</div>
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
                  <button v-if="auth.can('visits.create')" type="button" class="btn btn-soft btn-sm" @click="openCheckIn(row)">پذیرش</button>
                  <button v-if="auth.can('appointments.cancel')" type="button" class="btn btn-soft btn-sm ms-2" @click="cancelAppointment(row)">لغو</button>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <NewVisitModal v-if="addingVisit" @close="addingVisit = false" @saved="onVisitSaved" />

  <UiModal v-if="checkIn.row" :title="`پذیرش ${checkIn.row.patientName}`" @close="checkIn.row = null">
    <form @submit.prevent="submitCheckIn">
      <div class="mb-3">
        <label class="form-label" for="ci-dept">بخش</label>
        <select id="ci-dept" v-model.number="checkIn.departmentId" class="form-select" required>
          <option :value="null" disabled>انتخاب کنید</option>
          <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div v-if="checkIn.error" class="alert alert-danger" role="alert">{{ checkIn.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="checkIn.saving">ثبت پذیرش</button>
    </form>
  </UiModal>

  <UiModal v-if="booking.open" title="ثبت نوبت" @close="booking.open = false">
    <form @submit.prevent="submitBooking">
      <div class="mb-3">
        <template v-if="booking.patient">
          <div class="form-label">مریض</div>
          <div class="d-flex align-items-center justify-content-between">
            <strong>{{ booking.patient.fileNumber }} — {{ patientName(booking.patient) }}</strong>
            <button type="button" class="btn btn-soft btn-sm" @click="booking.patient = null">تغییر</button>
          </div>
        </template>
        <template v-else>
          <label class="form-label" for="b-search">جستجوی مریض</label>
          <input id="b-search" v-model="booking.search" type="search" class="form-control" autocomplete="off" />
          <div v-if="booking.results.length" class="list-group mt-1">
            <button v-for="r in booking.results" :key="r.id" type="button" class="list-group-item list-group-item-action" @click="booking.patient = r; booking.results = []; booking.search = ''">
              <span class="ltr-num">{{ r.fileNumber }}</span> — {{ patientName(r) }}
            </button>
          </div>
        </template>
      </div>
      <div class="mb-3">
        <label class="form-label" for="b-doctor">داکتر</label>
        <select id="b-doctor" v-model.number="booking.doctorStaffId" class="form-select" required>
          <option :value="null" disabled>انتخاب کنید</option>
          <option v-for="d in doctors" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div class="row g-3 mb-3">
        <div class="col-8"><ShamsiDateInput v-model="booking.date" label="تاریخ" :initial="todayShamsi()" /></div>
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
      <button type="submit" class="btn btn-primary" :disabled="booking.saving">ثبت نوبت</button>
    </form>
  </UiModal>
</template>

<style scoped>
.reception-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--xr-gap);
}
</style>
