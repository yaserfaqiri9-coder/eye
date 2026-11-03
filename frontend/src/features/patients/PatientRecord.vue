<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { anteriorRows, parseFindings, parseMeasurements, parsePlan, posteriorRows } from '@/features/clinical/examRecord'
import {
  diagnosticStatusLabels,
  diagnosticStatusTones,
  eyeLabels,
  rxStatusLabels,
  surgeryStatusLabels,
  type Chart,
  type Consultation,
  type Refraction,
  type SurgeryRow,
} from '@/features/clinical/types'
import { invoiceStatusLabels, invoiceStatusTones, type InvoiceRow } from '@/features/finance/types'
import { visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'
import type { Patient } from './types'

/** Each visit's chart is a separate audited read, so only the most recent ones open at a time. */
const chartLimit = 10

const props = defineProps<{ patient: Patient; visits: VisitRow[]; invoices: InvoiceRow[] }>()
const auth = useAuthStore()

const canSeeChart = computed(() => auth.can('consultation.view') && auth.can('clinicalrecord.view'))

const tabs = computed(() =>
  [
    { key: 'visits', label: 'مراجعات', visible: auth.can('visits.view') },
    { key: 'preexam', label: 'معاینات ابتدایی', visible: canSeeChart.value },
    { key: 'consult', label: 'معاینات داکتر', visible: canSeeChart.value },
    { key: 'refraction', label: 'بینایی‌سنجی', visible: auth.can('preexam.view') },
    { key: 'diagnoses', label: 'تشخیص‌ها', visible: canSeeChart.value },
    { key: 'lab', label: 'آزمایش‌ها', visible: canSeeChart.value },
    { key: 'rx', label: 'نسخه‌ها', visible: canSeeChart.value },
    { key: 'surgery', label: 'عملیات', visible: auth.can('surgery.view') },
    { key: 'money', label: 'وضعیت مالی', visible: auth.can('invoices.view') },
  ].filter((t) => t.visible),
)

const tab = ref(tabs.value[0]?.key ?? '')
const charts = ref<Chart[]>([])
const chartsLoaded = ref(false)
const refractions = ref<Refraction[]>([])
const surgeries = ref<SurgeryRow[]>([])
const loading = ref(false)
const error = ref('')

const openVisits = computed(() => props.visits.filter((v) => v.status !== 'Cancelled'))
const skippedCharts = computed(() => Math.max(0, openVisits.value.length - chartLimit))

async function loadCharts() {
  if (chartsLoaded.value || !canSeeChart.value) return
  chartsLoaded.value = true
  const results = await Promise.allSettled(openVisits.value.slice(0, chartLimit).map((v) => api<Chart>(`/consultations/${v.id}/chart`)))
  charts.value = results.flatMap((r) => (r.status === 'fulfilled' ? [r.value] : []))
}

async function loadTab(key: string) {
  error.value = ''
  loading.value = true
  try {
    if (['preexam', 'consult', 'diagnoses', 'lab', 'rx'].includes(key)) await loadCharts()
    if (key === 'refraction') refractions.value = await api<Refraction[]>(`/clinical/patients/${props.patient.id}/refractions`)
    if (key === 'surgery') {
      const page = await api<{ items: SurgeryRow[] }>('/surgeries?pageSize=100')
      surgeries.value = page.items.filter((s) => s.patientId === props.patient.id)
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

watch(tab, loadTab)

const preExams = computed(() =>
  charts.value.filter((c) => c.preExam).map((c) => ({ chart: c, pre: c.preExam!, extras: parseMeasurements(c.preExam!.measurements) })),
)

const consults = computed(() => charts.value.filter((c) => c.consultation))
const examRows = [...anteriorRows, ...posteriorRows]

function findings(consultation: Consultation) {
  const od = parseFindings(consultation.findingsOd)
  const os = parseFindings(consultation.findingsOs)
  return {
    rows: examRows.filter((row) => od.values[row.code] || os.values[row.code]).map((row) => ({ label: row.label, od: od.values[row.code] ?? '—', os: os.values[row.code] ?? '—' })),
    noteOd: od.note,
    noteOs: os.note,
  }
}

const diagnoses = computed(() =>
  charts.value.flatMap((c) => (c.consultation?.diagnoses ?? []).map((d, i) => ({ key: `${c.visit.id}-${i}`, date: c.visit.visitDate, visitId: c.visit.id, ...d }))),
)
const labOrders = computed(() => charts.value.flatMap((c) => c.diagnosticOrders))
const prescriptions = computed(() => charts.value.flatMap((c) => c.prescriptions))
const opticals = computed(() => charts.value.flatMap((c) => c.opticalPrescriptions))

const finalOf = (r: Refraction, eye: 'OD' | 'OS') => r.lines.find((l) => l.stage === 'Final' && l.eye === eye)
const rxText = (line?: { sphere: string | null; cylinder: string | null; axis: string | null; add: string | null }) =>
  line ? `${line.sphere ?? '—'} / ${line.cylinder ?? '—'} × ${line.axis ?? '—'} ADD ${line.add ?? '—'}` : '—'

const canPrint = computed(() => canSeeChart.value && auth.can('consultation.print'))
/** The paper file is one sheet per visit. A patient's full history stays here in the digital record. */
const latestVisit = computed(() => openVisits.value[0] ?? null)
</script>

<template>
  <section v-if="tabs.length" class="xr-card">
    <div class="xr-card__header">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های دوسیه">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
      <RouterLink v-if="canPrint && latestVisit" :to="`/print/dossier/${latestVisit.id}`" target="_blank" class="btn btn-soft">چاپ دوسیه آخرین مراجعه</RouterLink>
    </div>

    <div class="xr-card__body">
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <p v-if="skippedCharts && ['preexam', 'consult', 'diagnoses', 'lab', 'rx'].includes(tab)" class="form-hint">{{ chartLimit }} مراجعه آخر نمایش داده می‌شود. مراجعات قدیمی‌تر از تب مراجعات باز می‌شوند.</p>
      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>

      <template v-else-if="tab === 'visits'">
        <div v-if="visits.length === 0" class="xr-state">مراجعه‌ای ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr><th>شماره مراجعه</th><th>تاریخ</th><th>بخش</th><th>داکتر</th><th>علت</th><th>وضعیت</th><th v-if="canPrint">چاپ</th></tr>
            </thead>
            <tbody>
              <tr v-for="visit in visits" :key="visit.id">
                <td>
                  <RouterLink v-if="auth.can('consultation.view')" :to="`/chart/${visit.id}`"><span class="ltr-num">{{ visit.visitNumber }}</span></RouterLink>
                  <span v-else class="ltr-num">{{ visit.visitNumber }}</span>
                </td>
                <td>{{ formatDate(visit.visitDate) }}</td>
                <td>{{ visit.departmentName }}</td>
                <td>{{ visit.doctorName ?? '—' }}</td>
                <td>{{ visit.reason ?? '—' }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span></td>
                <td v-if="canPrint"><RouterLink v-if="visit.status !== 'Cancelled'" :to="`/print/dossier/${visit.id}`" target="_blank" class="btn btn-soft btn-sm text-nowrap">دوسیه</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'preexam'">
        <div v-if="preExams.length === 0" class="xr-state">معاینه ابتدایی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr>
                <th>تاریخ</th>
                <th><span class="xr-eye xr-eye--od">OD</span> VA</th>
                <th><span class="xr-eye xr-eye--od">OD</span> IOP</th>
                <th><span class="xr-eye xr-eye--os">OS</span> VA</th>
                <th><span class="xr-eye xr-eye--os">OS</span> IOP</th>
                <th>شکایت اصلی</th>
                <th>سوابق</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in preExams" :key="row.pre.id">
                <td class="text-nowrap"><RouterLink v-if="auth.can('consultation.view')" :to="`/chart/${row.chart.visit.id}`">{{ formatDate(row.chart.visit.visitDate) }}</RouterLink></td>
                <td class="ltr-num">{{ row.pre.visualAcuityOd ?? '—' }}<template v-if="row.extras.pinholeOd"> (PH {{ row.extras.pinholeOd }})</template></td>
                <td class="ltr-num">{{ row.pre.iopOd ?? '—' }}</td>
                <td class="ltr-num">{{ row.pre.visualAcuityOs ?? '—' }}<template v-if="row.extras.pinholeOs"> (PH {{ row.extras.pinholeOs }})</template></td>
                <td class="ltr-num">{{ row.pre.iopOs ?? '—' }}</td>
                <td>{{ row.pre.chiefComplaint ?? '—' }}</td>
                <td>{{ row.pre.history ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'consult'">
        <div v-if="consults.length === 0" class="xr-state">معاینه داکتر ثبت نشده است.</div>
        <details v-for="(c, index) in consults" :key="c.visit.id" class="xr-section" :open="index === 0">
          <summary>
            {{ formatDate(c.visit.visitDate) }} — {{ c.visit.doctorName ?? c.visit.departmentName }}
            <span class="xr-section__hint">{{ c.consultation!.finalizedAt ? 'نهایی‌شده' : 'پیش‌نویس' }}</span>
          </summary>
          <div class="xr-section__body">
            <p v-if="parsePlan(c.consultation!.treatmentPlan).history" class="pre-line"><strong>شرح حال:</strong> {{ parsePlan(c.consultation!.treatmentPlan).history }}</p>
            <div v-if="findings(c.consultation!).rows.length" class="xr-table-wrap mb-2">
              <table class="xr-clinical-table">
                <thead><tr><th>قسمت چشم</th><th><span class="xr-eye xr-eye--od">OD</span></th><th><span class="xr-eye xr-eye--os">OS</span></th></tr></thead>
                <tbody>
                  <tr v-for="row in findings(c.consultation!).rows" :key="row.label"><th scope="row" class="fw-normal">{{ row.label }}</th><td class="ltr-num">{{ row.od }}</td><td class="ltr-num">{{ row.os }}</td></tr>
                </tbody>
              </table>
            </div>
            <p v-if="findings(c.consultation!).noteOd" class="pre-line mb-1"><span class="xr-eye xr-eye--od">OD</span> {{ findings(c.consultation!).noteOd }}</p>
            <p v-if="findings(c.consultation!).noteOs" class="pre-line mb-1"><span class="xr-eye xr-eye--os">OS</span> {{ findings(c.consultation!).noteOs }}</p>
            <p v-if="parsePlan(c.consultation!.treatmentPlan).plan" class="pre-line mb-2"><strong>پلان:</strong> {{ parsePlan(c.consultation!.treatmentPlan).plan }}</p>
            <RouterLink :to="`/chart/${c.visit.id}`" class="btn btn-soft btn-sm">باز کردن پرونده این مراجعه</RouterLink>
          </div>
        </details>
      </template>

      <template v-else-if="tab === 'refraction'">
        <div v-if="refractions.length === 0" class="xr-state">بینایی‌سنجی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead>
              <tr><th>تاریخ</th><th><span class="xr-eye xr-eye--od">OD</span> نسخه نهایی</th><th><span class="xr-eye xr-eye--os">OS</span> نسخه نهایی</th><th>PD</th><th>وضعیت</th><th></th></tr>
            </thead>
            <tbody>
              <tr v-for="r in refractions" :key="r.id">
                <td class="text-nowrap">{{ formatDate(r.visitDate) }}</td>
                <td class="ltr-num">{{ rxText(finalOf(r, 'OD')) }}</td>
                <td class="ltr-num">{{ rxText(finalOf(r, 'OS')) }}</td>
                <td class="ltr-num">{{ r.pd ?? '—' }}</td>
                <td>{{ r.completedAt ? 'تکمیل' : 'در جریان' }}</td>
                <td><RouterLink :to="`/refraction/${r.visitId}`" class="btn btn-soft btn-sm">جزئیات</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'diagnoses'">
        <div v-if="diagnoses.length === 0" class="xr-state">تشخیصی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>تاریخ</th><th>چشم</th><th>تشخیص</th></tr></thead>
            <tbody>
              <tr v-for="d in diagnoses" :key="d.key">
                <td class="text-nowrap"><RouterLink :to="`/chart/${d.visitId}`">{{ formatDate(d.date) }}</RouterLink></td>
                <td>{{ eyeLabels[d.eye] }}</td>
                <td>{{ d.text }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'lab'">
        <div v-if="labOrders.length === 0" class="xr-state">آزمایشی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>آزمایش</th><th>وضعیت</th><th>نتیجه</th><th>زمان نتیجه</th></tr></thead>
            <tbody>
              <tr v-for="o in labOrders" :key="o.id">
                <td><span class="ltr-num">{{ o.orderNumber }}</span></td>
                <td>{{ o.testName }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[o.status]}`">{{ diagnosticStatusLabels[o.status] }}</span></td>
                <td>{{ o.resultText ?? '—' }}</td>
                <td>{{ formatDateTime(o.performedAt) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'rx'">
        <div v-if="prescriptions.length === 0 && opticals.length === 0" class="xr-state">نسخه‌ای ثبت نشده است.</div>
        <template v-if="prescriptions.length">
          <h3 class="h6">نسخه‌های دوا</h3>
          <div class="xr-table-wrap mb-4">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>تاریخ</th><th>دواها</th><th>وضعیت</th></tr></thead>
              <tbody>
                <tr v-for="p in prescriptions" :key="p.id">
                  <td><span class="ltr-num">{{ p.rxNumber }}</span></td>
                  <td class="text-nowrap">{{ formatDate(p.createdAt) }}</td>
                  <td>{{ p.items.map((i) => i.drugName).join('، ') }}</td>
                  <td>{{ rxStatusLabels[p.status] }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
        <template v-if="opticals.length">
          <h3 class="h6">نسخه‌های عینک</h3>
          <div class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>تاریخ</th><th><span class="xr-eye xr-eye--od">OD</span></th><th><span class="xr-eye xr-eye--os">OS</span></th><th>PD</th></tr></thead>
              <tbody>
                <tr v-for="o in opticals" :key="o.id">
                  <td><span class="ltr-num">{{ o.rxNumber }}</span></td>
                  <td class="text-nowrap">{{ formatDate(o.createdAt) }}</td>
                  <td class="ltr-num">{{ rxText({ sphere: o.sphereOd, cylinder: o.cylinderOd, axis: o.axisOd, add: o.addOd }) }}</td>
                  <td class="ltr-num">{{ rxText({ sphere: o.sphereOs, cylinder: o.cylinderOs, axis: o.axisOs, add: o.addOs }) }}</td>
                  <td class="ltr-num">{{ o.pd ?? '—' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </template>

      <template v-else-if="tab === 'surgery'">
        <div v-if="surgeries.length === 0" class="xr-state">پرونده عملیاتی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>نوع عملیات</th><th>چشم</th><th>تاریخ</th><th>وضعیت</th></tr></thead>
            <tbody>
              <tr v-for="s in surgeries" :key="s.id">
                <td><span class="ltr-num">{{ s.caseNumber }}</span></td>
                <td>{{ s.surgeryType }}</td>
                <td>{{ eyeLabels[s.eye] }}</td>
                <td>{{ formatDateTime(s.scheduledAt) }}</td>
                <td><span class="xr-badge xr-badge--primary">{{ surgeryStatusLabels[s.status] }}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'money'">
        <div v-if="invoices.length === 0" class="xr-state">فاکتوری ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>مجموع</th><th>تخفیف</th><th>پرداخت</th><th>مانده</th><th>وضعیت</th></tr></thead>
            <tbody>
              <tr v-for="row in invoices" :key="row.id">
                <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
                <td>{{ money(row.total) }}</td>
                <td>{{ money(row.discount) }}</td>
                <td>{{ money(row.paid) }}</td>
                <td>{{ money(row.balance) }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${invoiceStatusTones[row.status]}`">{{ invoiceStatusLabels[row.status] }}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </div>
  </section>

</template>

<style scoped>
.pre-line {
  white-space: pre-line;
}

.xr-section p {
  color: var(--xr-text);
}
</style>
