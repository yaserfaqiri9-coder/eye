<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiAvatar from '@/components/UiAvatar.vue'
import UiCard from '@/components/UiCard.vue'
import { eyeLabels, type Chart } from '@/features/clinical/types'
import type { InvoiceRow } from '@/features/finance/types'
import { visitStatusLabels, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { useRouter } from 'vue-router'
import PatientRecord from './PatientRecord.vue'
import ProgressRing from './ProgressRing.vue'
import VisitsChart from './VisitsChart.vue'
import { genderLabels, patientName, type Patient } from './types'

interface ChangeRow {
  field: string
  oldValue: string | null
  newValue: string | null
  changedAt: string
  changedBy: string | null
}

const fieldLabels: Record<string, string> = {
  FirstName: 'نام',
  LastName: 'تخلص',
  FatherName: 'نام پدر',
  Gender: 'جنسیت',
  BirthDate: 'تاریخ تولد',
  EstimatedBirthYear: 'سال تولد تخمینی',
  Phone: 'تماس',
  Address: 'آدرس',
}

const props = defineProps<{ id: string }>()
const auth = useAuthStore()

const patient = ref<Patient | null>(null)
const visits = ref<VisitRow[]>([])
const visitTotal = ref(0)
const changes = ref<ChangeRow[]>([])
const invoices = ref<InvoiceRow[]>([])
const chart = ref<Chart | null>(null)
const error = ref('')

const canSeeMoney = computed(() => auth.can('invoices.view'))
const canSeeChart = computed(() => auth.can('consultation.view') && auth.can('clinicalrecord.view'))

/** The medical record may be off limits for this user or patient; the rest of the file still shows. */
async function loadChart(visitId: number | undefined) {
  if (!visitId || !canSeeChart.value) return null
  try {
    return await api<Chart>(`/consultations/${visitId}/chart`)
  } catch {
    return null
  }
}

async function load() {
  error.value = ''
  const id = props.id
  try {
    const nextPatient = await api<Patient>(`/patients/${id}`)
    const [nextVisits, nextChanges, nextInvoices] = await Promise.all([
      auth.can('visits.view') ? api<{ items: VisitRow[]; total: number }>(`/visits?patientId=${id}&pageSize=100`) : { items: [], total: 0 },
      auth.can('patients.edit') ? api<ChangeRow[]>(`/patients/${id}/changes`) : [],
      canSeeMoney.value ? api<{ items: InvoiceRow[] }>(`/invoices?q=${encodeURIComponent(nextPatient.fileNumber)}&pageSize=100`).then((r) => r.items) : [],
    ])
    const latest = nextVisits.items.find((visit) => visit.status !== 'Cancelled')
    const nextChart = await loadChart(latest?.id)
    if (id !== props.id) return
    patient.value = nextPatient
    visits.value = nextVisits.items
    visitTotal.value = nextVisits.total
    changes.value = nextChanges
    invoices.value = nextInvoices.filter((invoice) => invoice.patientId === nextPatient.id && invoice.status !== 'Void')
    chart.value = nextChart
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

function onVisitSaved() {
  load()
}

const router = useRouter()

async function removePatient() {
  const p = patient.value
  if (!p || !window.confirm(`پرونده «${patientName(p)}» حذف شود؟ این کار برگشت ندارد. پرونده‌ای که مراجعه یا سابقه دارد حذف نمی‌شود.`)) return
  error.value = ''
  try {
    await api(`/patients/${p.id}?version=${p.version}`, { method: 'DELETE' })
    await router.push('/patients')
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

watch(() => props.id, load)
onMounted(load)

const subtitle = computed(() => {
  const p = patient.value
  if (!p) return ''
  return [p.ageYears !== null ? `${quantity(p.ageYears)} ساله` : null, genderLabels[p.gender], p.address].filter(Boolean).join('، ')
})

function share(part: number, whole: number) {
  return whole > 0 ? Math.round((part / whole) * 100) : 0
}

const completedCount = computed(() => visits.value.filter((v) => v.status === 'Completed').length)
const cancelledCount = computed(() => visits.value.filter((v) => v.status === 'Cancelled').length)
const openCount = computed(() => visits.value.length - completedCount.value - cancelledCount.value)

const visitBars = computed(() => [
  { label: 'تکمیل', tone: 'primary', percent: share(completedCount.value, visits.value.length) },
  { label: 'لغو', tone: 'red', percent: share(cancelledCount.value, visits.value.length) },
  { label: 'در جریان', tone: 'cyan', percent: share(openCount.value, visits.value.length) },
])

const billed = computed(() => invoices.value.reduce((sum, row) => sum + row.total, 0))
const settled = computed(() => invoices.value.reduce((sum, row) => sum + row.paid - row.refunded, 0))
const discounted = computed(() => invoices.value.reduce((sum, row) => sum + row.discount, 0))
const balance = computed(() => invoices.value.reduce((sum, row) => sum + row.balance, 0))

const moneyBars = computed(() => [
  { label: 'پرداخت', tone: 'primary', percent: share(settled.value, billed.value) },
  { label: 'باقی', tone: 'red', percent: share(balance.value, billed.value) },
  { label: 'تخفیف', tone: 'cyan', percent: share(discounted.value, billed.value) },
])

const latestVisit = computed(() => visits.value.find((v) => v.status !== 'Cancelled') ?? null)

const advice = computed(() => {
  const visit = latestVisit.value
  if (!visit) return 'این مریض هنوز مراجعه‌ای ندارد.'
  return `آخرین مراجعه: ${formatDate(visit.visitDate)}، ${visit.departmentName}، ${visitStatusLabels[visit.status]}.`
})

const exam = computed(() => chart.value?.preExam ?? null)

const eyes = computed(() => [
  { title: 'چشم راست', code: 'OD', vision: exam.value?.visualAcuityOd, pressure: exam.value?.iopOd },
  { title: 'چشم چپ', code: 'OS', vision: exam.value?.visualAcuityOs, pressure: exam.value?.iopOs },
])

const diagnosis = computed(() => chart.value?.consultation?.diagnoses[0] ?? null)
const followUp = computed(() => chart.value?.followUps[0] ?? null)
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="patient" class="d-grid gap-4">
    <div class="file">
      <div class="file__side">
        <section class="xr-card profile">
          <div class="xr-card__body">
            <div class="profile__avatar"><UiAvatar :seed="patient.fileNumber" :gender="patient.gender" :size="130" /></div>
            <h1 class="profile__name">{{ patientName(patient) }}</h1>
            <p class="profile__sub">{{ subtitle }}</p>
            <ul class="profile__stats">
              <li>
                <h6>سن</h6>
                <h3>{{ patient.ageYears !== null ? quantity(patient.ageYears) : '—' }}<span>سال</span></h3>
              </li>
              <li>
                <h6>مراجعات</h6>
                <h3>{{ quantity(visitTotal) }}<span>بار</span></h3>
              </li>
              <li>
                <h6>در جریان</h6>
                <h3 class="profile__open">{{ quantity(openCount) }}<span>مورد</span></h3>
              </li>
            </ul>
            <div class="profile__actions">
              <RouterLink v-if="auth.can('visits.create')" class="btn btn-primary" :to="{ name: 'reception', query: { patient: props.id } }">مراجعه جدید</RouterLink>
              <RouterLink v-if="auth.can('patients.edit')" :to="`/patients/${patient.id}/edit`" class="btn btn-soft">ویرایش</RouterLink>
              <button v-if="auth.can('patients.edit')" type="button" class="btn btn-soft-danger" @click="removePatient">حذف</button>
              <RouterLink v-if="auth.can('patients.print')" :to="`/print/patient/${patient.id}`" target="_blank" class="btn btn-soft">چاپ کارت</RouterLink>
            </div>
          </div>
        </section>

        <section class="xr-card">
          <div class="xr-card__body">
            <div class="block">
              <div class="block__data">
                <p>شماره پرونده</p>
                <h5><span class="ltr-num">{{ patient.fileNumber }}</span></h5>
                <p class="mt-3">نام پدر</p>
                <h5>{{ patient.fatherName ?? '—' }}</h5>
              </div>
              <ProgressRing :fraction="visits.length ? completedCount / visits.length : 0" :value="quantity(completedCount)" label="تکمیل" />
            </div>
            <ul class="bars">
              <li v-for="bar in visitBars" :key="bar.label">
                <h6>{{ bar.label }}</h6>
                <div class="bars__track"><span :class="`bars__fill--${bar.tone}`" :style="{ width: `${bar.percent}%` }"></span></div>
              </li>
            </ul>

            <template v-if="canSeeMoney">
              <hr />
              <div class="block">
                <div class="block__data">
                  <p>پرداخت‌شده</p>
                  <h5>{{ money(settled) }}</h5>
                  <p class="mt-3">کل فاکتورها</p>
                  <h5>{{ money(billed) }}</h5>
                </div>
                <ProgressRing :fraction="billed ? settled / billed : 0" :value="`${quantity(share(settled, billed))}٪`" label="پرداخت" tone="yellow" />
              </div>
              <ul class="bars">
                <li v-for="bar in moneyBars" :key="bar.label">
                  <h6>{{ bar.label }}</h6>
                  <div class="bars__track"><span :class="`bars__fill--${bar.tone}`" :style="{ width: `${bar.percent}%` }"></span></div>
                </li>
              </ul>
            </template>
          </div>
        </section>
      </div>

      <section class="xr-card overview">
        <div class="advice">
          <p>{{ advice }}</p>
          <RouterLink v-if="latestVisit && auth.can('consultation.view')" :to="`/chart/${latestVisit.id}`" class="advice__icon" aria-label="باز کردن پرونده طبی آخرین مراجعه" title="پرونده طبی">
            <i class="ri-stethoscope-fill" aria-hidden="true"></i>
          </RouterLink>
          <span v-else class="advice__icon"><i class="ri-stethoscope-fill" aria-hidden="true"></i></span>
        </div>

        <h4 class="overview__title">مشخصات مریض</h4>
        <div class="tiles">
          <div class="tile">
            <div class="circle"><i class="ri-phone-fill" aria-hidden="true"></i></div>
            <div class="tile__text">
              <h5>شماره تماس</h5>
              <p><span class="ltr-num">{{ patient.phone ?? '—' }}</span></p>
            </div>
          </div>
          <div class="tile">
            <div class="circle"><i class="ri-cake-2-fill" aria-hidden="true"></i></div>
            <div class="tile__text">
              <h5>تاریخ تولد</h5>
              <p>{{ formatDate(patient.birthDate) }}</p>
            </div>
          </div>
          <div class="tile">
            <div class="circle"><i class="ri-folder-user-fill" aria-hidden="true"></i></div>
            <div class="tile__text">
              <h5>تاریخ ثبت</h5>
              <p>{{ formatDate(patient.createdAt) }}</p>
            </div>
          </div>
        </div>

        <div class="middle">
          <div>
            <div class="overview__head"><h4>آمار مراجعات</h4></div>
            <VisitsChart :dates="visits.map((visit) => visit.visitDate)" />
          </div>
          <div>
            <div class="overview__head">
              <h4>معاینه آخر</h4>
              <RouterLink v-if="auth.can('visits.create')" class="btn btn-soft overview__add" aria-label="افزودن مراجعه" title="افزودن مراجعه" :to="{ name: 'reception', query: { patient: props.id } }">
                <i class="ri-add-line" aria-hidden="true"></i>
              </RouterLink>
            </div>
            <table v-for="eye in eyes" :key="eye.code" class="eye">
              <thead>
                <tr><th>{{ eye.title }}</th><th class="ltr-num">{{ eye.code }}</th></tr>
              </thead>
              <tbody>
                <tr><td>دید</td><td class="ltr-num">{{ eye.vision ?? '—' }}</td></tr>
                <tr><td>فشار</td><td class="ltr-num">{{ eye.pressure ?? '—' }}</td></tr>
              </tbody>
            </table>
            <p v-if="!chart" class="overview__note">پرونده طبی برای شما باز نیست یا هنوز ثبت نشده است.</p>
          </div>
        </div>

        <div class="bottom">
          <div>
            <h4 class="bottom__title">تشخیص آخر</h4>
            <div class="bottom__row">
              <div>
                <h5>{{ diagnosis?.text ?? '—' }}</h5>
                <span class="bottom__note">{{ diagnosis ? eyeLabels[diagnosis.eye] : 'تشخیصی ثبت نشده' }}</span>
              </div>
              <div class="circle"><i class="ri-eye-fill" aria-hidden="true"></i></div>
            </div>
          </div>
          <div>
            <h4 class="bottom__title">پیگیری بعدی</h4>
            <div class="bottom__row">
              <div>
                <p class="mb-0">{{ followUp?.note ?? 'تاریخ' }}</p>
                <h5>{{ followUp ? formatDate(followUp.dueDate) : '—' }}</h5>
              </div>
              <div class="circle"><i class="ri-calendar-event-fill" aria-hidden="true"></i></div>
            </div>
          </div>
        </div>
      </section>
    </div>

    <PatientRecord :key="patient.id" :patient="patient" :visits="visits" :invoices="invoices" />

    <UiCard v-if="auth.can('patients.edit') && changes.length" title="تاریخچه تغییرات">
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>فیلد</th><th>قبلی</th><th>جدید</th><th>کاربر</th><th>زمان</th></tr>
          </thead>
          <tbody>
            <tr v-for="(change, index) in changes" :key="index">
              <td>{{ fieldLabels[change.field] ?? change.field }}</td>
              <td>{{ change.oldValue ?? '—' }}</td>
              <td>{{ change.newValue ?? '—' }}</td>
              <td>{{ change.changedBy ?? '—' }}</td>
              <td>{{ formatDateTime(change.changedAt) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

</template>

<style scoped>
/* The template's col-lg-4 and col-lg-8 card widths at 1600px: 413 and 849, 24 apart. */
.file {
  display: grid;
  grid-template-columns: minmax(0, 412.67fr) minmax(0, 849.33fr);
  gap: var(--xr-gap-x);
}

.file__side {
  align-content: start;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--xr-gap-y);
  padding-top: 65px;
}

.profile__avatar {
  display: flex;
  justify-content: center;
  margin-top: -85px;
}

.profile__name {
  margin-top: 16px;
  font-size: 19.6px;
  font-weight: 700;
  line-height: 29.4px;
  text-align: center;
}

.profile__sub {
  margin: 0;
  text-align: center;
}

.profile__stats {
  display: flex;
  margin: 24px 0 0;
  padding: 0;
  list-style: none;
}

.profile__stats li {
  flex: 1;
  min-width: 0;
  text-align: center;
}

.profile__stats li + li {
  border-inline-start: 1px solid #d8d8d8;
}

.profile__stats h6 {
  color: var(--xr-primary);
}

.profile__stats h3 span {
  margin-inline-start: 3px;
  font-size: 20px;
  line-height: 30px;
}

.profile__stats .profile__open {
  color: var(--xr-yellow);
}

.profile__actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 8px;
  margin-top: 20px;
}

.block {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.block__data {
  flex: 1;
  min-width: 0;
}

.block__data p {
  margin-bottom: 0;
}

.block__data h5 {
  overflow-wrap: anywhere;
}

.block__data .ltr-num {
  white-space: nowrap;
}

/* The template's ring position needs its 413px card; narrower cards give the room back to the text. */
@media (min-width: 1500px) {
  .block :deep(.ring) {
    margin-inline: 43px;
  }
}

.bars {
  display: flex;
  margin: 24px 0 0;
  padding: 0;
  list-style: none;
}

.bars li {
  flex: 1;
  min-width: 0;
  margin-inline: 10px;
}

.bars h6 {
  color: var(--xr-primary);
}

.bars__track {
  height: 6px;
  margin-block: 11px 8px;
  border-radius: 4px;
  background: var(--xr-track);
}

.bars__track span {
  display: block;
  height: 100%;
  border-radius: 4px;
}

.bars__fill--primary { background-image: var(--xr-gradient-primary); }
.bars__fill--red { background-image: var(--xr-gradient-red); }
.bars__fill--cyan { background-image: var(--xr-gradient-cyan); }

/* Stretches to the side column's height, with the last row kept at the bottom edge. */
.overview {
  display: flex;
  flex-direction: column;
  padding: 20px;
}

.advice {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  min-height: 76px;
  margin-bottom: 30px;
  padding-block: 8px;
  padding-inline: 20px 8px;
  border-radius: var(--xr-radius-tile);
  background-image: linear-gradient(to left, var(--xr-primary) 0%, var(--xr-primary-end) 100%);
  color: #fff;
}

.advice p {
  margin: 0;
}

.advice__icon {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 60px;
  height: 60px;
  border-radius: var(--xr-radius-tile);
  background: var(--xr-surface);
  color: var(--xr-primary);
  font-size: 22px;
}

.overview h4 {
  color: var(--xr-primary);
}

.overview__title {
  margin-bottom: 32px;
}

.circle {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 60px;
  height: 60px;
  border-radius: 50%;
  background: var(--xr-primary-light);
  color: var(--xr-primary);
  font-size: 22px;
}

.tiles {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--xr-gap-x);
  margin-bottom: 30px;
}

.tile {
  display: flex;
  align-items: center;
  gap: 16px;
}

.tile__text {
  min-width: 0;
}

.tile__text p {
  margin: 0;
}

.middle {
  display: grid;
  grid-template-columns: minmax(0, 532fr) minmax(0, 254fr);
  gap: var(--xr-gap-x);
  margin-bottom: 30px;
}

.overview__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 60px;
}

.overview__add {
  width: 48px;
  font-size: 18px;
  line-height: 22px;
}

.eye {
  width: 100%;
  border-radius: var(--xr-radius-card);
  box-shadow: var(--xr-shadow-table);
  color: var(--xr-text-table);
}

.eye + .eye {
  margin-top: 24px;
}

.eye th,
.eye td {
  width: 50%;
  padding: 8px;
  font-weight: 400;
  text-align: start;
}

.eye thead {
  border-bottom: 1px solid rgba(0, 0, 0, 0.05);
}

.overview__note {
  margin: 12px 0 0;
  font-size: 12px;
  line-height: 18px;
}

.bottom {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--xr-gap-x);
  margin-top: auto;
}

.bottom__title {
  margin-bottom: 4px;
}

.bottom__row {
  display: flex;
  align-items: center;
  gap: 16px;
}

.bottom__row > div:first-child {
  min-width: 0;
}

.bottom__note {
  color: var(--xr-primary);
}

@media (max-width: 1199.98px) {
  .file {
    grid-template-columns: minmax(0, 1fr);
  }
}

@media (max-width: 767.98px) {
  .tiles,
  .middle,
  .bottom {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
