<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiAvatar from '@/components/UiAvatar.vue'
import { eyeLabels, type Chart } from '@/features/clinical/types'
import type { InvoiceRow } from '@/features/finance/types'
import { visitStatusLabels, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import { useRouter } from 'vue-router'
import PatientRecord from './PatientRecord.vue'
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
const router = useRouter()
const patient = ref<Patient | null>(null)
const visits = ref<VisitRow[]>([])
const visitTotal = ref(0)
const changes = ref<ChangeRow[]>([])
const invoices = ref<InvoiceRow[]>([])
const chart = ref<Chart | null>(null)
const error = ref('')
const canSeeMoney = computed(() => auth.can('invoices.view'))
const canSeeChart = computed(
  () => auth.can('consultation.view') && auth.can('clinicalrecord.view'),
)

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
    const [nextPatient, nextVisits, nextChanges, nextInvoices] =
      await Promise.all([
        api<Patient>(`/patients/${id}`),
        auth.can('visits.view')
          ? api<{ items: VisitRow[]; total: number }>(
              `/visits?patientId=${id}&pageSize=100`,
            )
          : { items: [], total: 0 },
        auth.can('patients.edit')
          ? api<ChangeRow[]>(`/patients/${id}/changes`)
          : [],
        canSeeMoney.value
          ? api<{ items: InvoiceRow[] }>(
              `/invoices?patientId=${id}&pageSize=100`,
            ).then((r) => r.items)
          : [],
      ])
    const latest = nextVisits.items.find(
      (visit) => visit.status !== 'Cancelled',
    )
    const nextChart = await loadChart(latest?.id)
    if (id !== props.id) return
    patient.value = nextPatient
    visits.value = nextVisits.items
    visitTotal.value = nextVisits.total
    changes.value = nextChanges
    invoices.value = nextInvoices.filter(
      (invoice) =>
        invoice.patientId === nextPatient.id && invoice.status !== 'Void',
    )
    chart.value = nextChart
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}
async function removePatient() {
  const p = patient.value
  if (
    !p ||
    !window.confirm(
      `پرونده «${patientName(p)}» حذف شود؟ این کار برگشت ندارد. پرونده‌ای که مراجعه یا سابقه دارد حذف نمی‌شود.`,
    )
  )
    return
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
const subtitle = computed(() =>
  patient.value
    ? [
        patient.value.ageYears !== null
          ? `${quantity(patient.value.ageYears)} ساله`
          : null,
        genderLabels[patient.value.gender],
      ]
        .filter(Boolean)
        .join('، ')
    : '',
)
const completedCount = computed(
  () => visits.value.filter((v) => v.status === 'Completed').length,
)
const cancelledCount = computed(
  () => visits.value.filter((v) => v.status === 'Cancelled').length,
)
const openCount = computed(
  () => visits.value.length - completedCount.value - cancelledCount.value,
)
const latestVisit = computed(
  () => visits.value.find((v) => v.status !== 'Cancelled') ?? null,
)
const exam = computed(() => chart.value?.preExam ?? null)
const eyes = computed(() => [
  {
    title: 'چشم راست',
    code: 'OD',
    vision: exam.value?.visualAcuityOd,
    pressure: exam.value?.iopOd,
  },
  {
    title: 'چشم چپ',
    code: 'OS',
    vision: exam.value?.visualAcuityOs,
    pressure: exam.value?.iopOs,
  },
])
const diagnosis = computed(
  () => chart.value?.consultation?.diagnoses[0] ?? null,
)
const followUp = computed(() => chart.value?.followUps[0] ?? null)
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="patient" class="patient-file">
    <aside class="patient-file__side" aria-label="مشخصات و عملیات مریض">
      <section class="xr-card identity">
        <div class="xr-card__body">
          <div class="identity__head">
            <UiAvatar
              :seed="patient.fileNumber"
              :gender="patient.gender"
              :size="72"
            />
            <div class="identity__name">
              <h1>{{ patientName(patient) }}</h1>
              <p>{{ subtitle }}</p>
            </div>
          </div>
          <dl class="identity__details">
            <div>
              <dt>شماره پرونده</dt>
              <dd class="ltr-num">{{ patient.fileNumber }}</dd>
            </div>
            <div>
              <dt>نام پدر</dt>
              <dd>{{ patient.fatherName ?? '—' }}</dd>
            </div>
            <div>
              <dt>شماره تماس</dt>
              <dd class="ltr-num">{{ patient.phone ?? '—' }}</dd>
            </div>
            <div>
              <dt>تاریخ تولد</dt>
              <dd>{{ formatDate(patient.birthDate) }}</dd>
            </div>
            <div class="identity__wide">
              <dt>آدرس</dt>
              <dd>{{ patient.address ?? '—' }}</dd>
            </div>
          </dl>
          <div class="identity__facts" aria-label="آمار مراجعات">
            <div>
              <strong>{{ quantity(visitTotal) }}</strong><span
                >مجموع مراجعات</span
              >
            </div>
            <div>
              <strong :class="{ identity__open: openCount > 0 }">{{
                quantity(openCount)
              }}</strong><span>در جریان</span>
            </div>
          </div>
          <div class="identity__actions">
            <RouterLink
              v-if="auth.can('visits.create')"
              class="btn btn-primary identity__primary"
              :to="{ name: 'reception', query: { patient: props.id } }"
              ><i class="ri-add-line" aria-hidden="true"></i> مراجعه
              جدید</RouterLink
            >
            <div class="identity__secondary">
              <RouterLink
                v-if="auth.can('patients.edit')"
                :to="`/patients/${patient.id}/edit`"
                class="btn btn-soft"
                >ویرایش</RouterLink
              ><RouterLink
                v-if="auth.can('patients.print')"
                :to="`/print/patient/${patient.id}`"
                target="_blank"
                class="btn btn-soft"
                >چاپ کارت</RouterLink
              >
            </div>
            <button
              v-if="auth.can('patients.edit')"
              type="button"
              class="identity__delete"
              title="حذف پرونده مریض"
              @click="removePatient"
            >
              حذف
            </button>
          </div>
        </div>
      </section>
    </aside>

    <section class="xr-card clinical" aria-labelledby="latest-clinical-title">
      <div class="clinical__header">
        <div>
          <p class="clinical__eyebrow">خلاصه برای داکتر</p>
          <h2 id="latest-clinical-title">آخرین وضعیت طبی</h2>
          <p v-if="latestVisit" class="clinical__visit">
            {{ formatDate(latestVisit.visitDate) }} ·
            {{ latestVisit.departmentName }}
            <span class="xr-badge xr-badge--primary">{{
              visitStatusLabels[latestVisit.status]
            }}</span>
          </p>
          <p v-else class="clinical__visit">هنوز مراجعه‌ای ثبت نشده است.</p>
        </div>
        <RouterLink
          v-if="latestVisit && auth.can('consultation.view')"
          :to="`/chart/${latestVisit.id}`"
          class="btn btn-primary"
          ><i class="ri-stethoscope-line" aria-hidden="true"></i> باز کردن
          پرونده طبی</RouterLink
        >
      </div>
      <div v-if="latestVisit" class="clinical__body">
        <div class="clinical__eyes" aria-label="وضعیت چشم‌ها">
          <article v-for="eye in eyes" :key="eye.code" class="eye-summary">
            <header>
              <span>{{ eye.title }}</span><strong class="ltr-num">{{
                eye.code
              }}</strong>
            </header>
            <dl>
              <div>
                <dt>دید (VA)</dt>
                <dd class="ltr-num">{{ eye.vision ?? '—' }}</dd>
              </div>
              <div>
                <dt>فشار (IOP)</dt>
                <dd class="ltr-num">{{ eye.pressure ?? '—' }}</dd>
              </div>
            </dl>
          </article>
        </div>
        <div class="clinical__notes">
          <section>
            <div class="clinical__label">
              <i class="ri-eye-line" aria-hidden="true"></i> تشخیص آخر
            </div>
            <h3>{{ diagnosis?.text ?? 'تشخیصی ثبت نشده است' }}</h3>
            <p v-if="diagnosis">{{ eyeLabels[diagnosis.eye] }}</p>
          </section>
          <section>
            <div class="clinical__label">
              <i class="ri-calendar-check-line" aria-hidden="true"></i> پیگیری
              بعدی
            </div>
            <h3>
              {{ followUp ? formatDate(followUp.dueDate) : 'تعیین نشده است' }}
            </h3>
            <p v-if="followUp?.note">{{ followUp.note }}</p>
          </section>
        </div>
        <p v-if="!chart" class="clinical__unavailable">
          جزئیات طبی برای شما قابل دسترس نیست یا هنوز ثبت نشده است.
        </p>
      </div>
      <div v-else class="clinical__empty">
        <i class="ri-file-list-3-line" aria-hidden="true"></i>
        <div>
          <h3>سابقه طبی موجود نیست</h3>
          <p>
            با ثبت مراجعه جدید، خلاصه آخرین معاینه در این بخش نمایش داده می‌شود.
          </p>
        </div>
      </div>
    </section>

    <div class="patient-file__record">
      <PatientRecord
        :key="patient.id"
        :patient="patient"
        :visits="visits"
        :invoices="invoices"
      />
    </div>
    <details
      v-if="auth.can('patients.edit') && changes.length"
      class="xr-card patient-file__changes"
    >
      <summary>
        تاریخچه تغییرات <span>{{ quantity(changes.length) }} مورد</span>
      </summary>
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr>
              <th>فیلد</th>
              <th>قبلی</th>
              <th>جدید</th>
              <th>کاربر</th>
              <th>زمان</th>
            </tr>
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
    </details>
  </div>
</template>

<style scoped>
.patient-file {
  display: grid;
  grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
  gap: var(--xr-gap-x);
}
.patient-file > * {
  min-width: 0;
}
.patient-file__side {
  align-self: start;
}
.patient-file__record,
.patient-file__changes {
  grid-column: 1/-1;
}
.identity__head {
  display: flex;
  align-items: center;
  gap: 14px;
}
.identity__name {
  min-width: 0;
}
.identity__name h1 {
  margin: 0;
  font-size: 1.18rem;
  line-height: 1.5;
  overflow-wrap: anywhere;
}
.identity__name p {
  margin: 2px 0 0;
  color: var(--xr-text-soft);
  font-size: 0.86rem;
}
.identity__details {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin: 20px 0 0;
  border-block: 1px solid var(--xr-row-border);
}
.identity__details > div {
  padding: 11px 0;
  min-width: 0;
}
.identity__details > div:nth-child(even) {
  padding-inline-start: 14px;
  border-inline-start: 1px solid var(--xr-row-border);
}
.identity__details .identity__wide {
  grid-column: 1/-1;
  border-block-start: 1px solid var(--xr-row-border);
}
.identity__details dt {
  margin-bottom: 3px;
  color: var(--xr-text-soft);
  font-size: 0.75rem;
}
.identity__details dd {
  margin: 0;
  font-weight: 600;
  overflow-wrap: anywhere;
}
.identity__facts {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  margin-top: 16px;
  padding: 12px 0;
  background: var(--xr-primary-light);
  border-radius: var(--xr-radius-control);
}
.identity__facts div {
  display: flex;
  flex-direction: column;
  align-items: center;
}
.identity__facts div + div {
  border-inline-start: 1px solid rgba(0, 119, 117, 0.18);
}
.identity__facts strong {
  color: var(--xr-primary);
  font-size: 1.25rem;
}
.identity__facts span {
  color: var(--xr-text-soft);
  font-size: 0.75rem;
}
.identity__facts .identity__open {
  color: var(--xr-yellow);
}
.identity__actions {
  display: grid;
  gap: 8px;
  margin-top: 16px;
}
.identity__primary {
  width: 100%;
}
.identity__secondary {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 8px;
}
.identity__delete {
  justify-self: center;
  border: 0;
  background: transparent;
  color: var(--xr-red);
  font: inherit;
  font-size: 0.78rem;
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}
.identity__delete:focus-visible {
  outline: 2px solid var(--xr-red);
  outline-offset: 3px;
}
.clinical {
  min-height: 100%;
  overflow: hidden;
}
.clinical__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 22px 24px;
  border-bottom: 1px solid var(--xr-row-border);
  background: var(--xr-primary-light);
}
.clinical__eyebrow {
  margin: 0 0 3px;
  color: var(--xr-primary);
  font-size: 0.75rem;
  font-weight: 700;
}
.clinical__header h2 {
  margin: 0;
  font-size: 1.28rem;
}
.clinical__visit {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 7px 0 0;
  color: var(--xr-text-soft);
  font-size: 0.85rem;
}
.clinical__body {
  padding: 24px;
}
.clinical__eyes {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
.eye-summary {
  border: 1px solid var(--xr-row-border);
  border-radius: var(--xr-radius-control);
  overflow: hidden;
}
.eye-summary header {
  display: flex;
  justify-content: space-between;
  padding: 10px 14px;
  background: #f7faf9;
  color: var(--xr-primary);
}
.eye-summary dl {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  margin: 0;
}
.eye-summary dl div {
  padding: 13px 14px;
}
.eye-summary dl div + div {
  border-inline-start: 1px solid var(--xr-row-border);
}
.eye-summary dt {
  color: var(--xr-text-soft);
  font-size: 0.75rem;
}
.eye-summary dd {
  margin: 4px 0 0;
  font-size: 1.05rem;
  font-weight: 700;
}
.clinical__notes {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin-top: 18px;
  border-block: 1px solid var(--xr-row-border);
}
.clinical__notes section {
  min-width: 0;
  padding: 17px 0;
}
.clinical__notes section + section {
  padding-inline-start: 20px;
  border-inline-start: 1px solid var(--xr-row-border);
}
.clinical__label {
  color: var(--xr-primary);
  font-size: 0.8rem;
  font-weight: 700;
}
.clinical__notes h3 {
  margin: 7px 0 0;
  font-size: 1rem;
  line-height: 1.7;
  overflow-wrap: anywhere;
}
.clinical__notes p {
  margin: 3px 0 0;
  color: var(--xr-text-soft);
  font-size: 0.8rem;
}
.clinical__unavailable {
  margin: 14px 0 0;
  color: var(--xr-text-soft);
  font-size: 0.78rem;
}
.clinical__empty {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 40px 24px;
  color: var(--xr-text-soft);
}
.clinical__empty > i {
  color: var(--xr-primary);
  font-size: 2rem;
}
.clinical__empty h3 {
  margin: 0;
  color: var(--xr-text);
  font-size: 1rem;
}
.clinical__empty p {
  margin: 4px 0 0;
}
.patient-file__changes {
  overflow: hidden;
}
.patient-file__changes summary {
  padding: 16px 20px;
  color: var(--xr-text);
  font-weight: 700;
  cursor: pointer;
}
.patient-file__changes summary span {
  margin-inline-start: 6px;
  color: var(--xr-text-soft);
  font-size: 0.76rem;
  font-weight: 400;
}
@media (max-width: 991.98px) {
  .patient-file {
    grid-template-columns: 1fr;
  }
}
@media (max-width: 575.98px) {
  .clinical__header {
    align-items: stretch;
    flex-direction: column;
    padding: 18px;
  }
  .clinical__body {
    padding: 18px;
  }
  .clinical__eyes,
  .clinical__notes {
    grid-template-columns: 1fr;
  }
  .clinical__notes section + section {
    padding-inline-start: 0;
    border-inline-start: 0;
    border-block-start: 1px solid var(--xr-row-border);
  }
}
</style>
