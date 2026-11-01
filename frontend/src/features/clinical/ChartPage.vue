<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import {
  diagnosticStatusLabels,
  diagnosticStatusTones,
  eyeLabels,
  rxStatusLabels,
  surgeryStatusLabels,
  type Chart,
  type Consultation,
  type DiagnosticOrder,
  type HistoryItem,
  type OpticalRx,
  type Prescription,
  type SurgeryRow,
} from './types'

const props = defineProps<{ visitId: string }>()
const auth = useAuthStore()

const chart = ref<Chart | null>(null)
const error = ref('')
const notice = ref('')
const needsEmergency = ref(false)
const tab = ref('exam')

const tabs = [
  { key: 'exam', label: 'معاینه' },
  { key: 'rx', label: 'نسخه دوا' },
  { key: 'optical', label: 'نسخه عینک' },
  { key: 'lab', label: 'آزمایش' },
  { key: 'surgery', label: 'عملیات' },
  { key: 'history', label: 'سوابق' },
]

const canEdit = computed(() => auth.can('consultation.edit'))
const finalized = computed(() => !!chart.value?.consultation?.finalizedAt)

const exam = reactive({
  findingsOd: '',
  findingsOs: '',
  treatmentPlan: '',
  confidentialNote: '',
  diagnoses: [] as { eye: string; text: string }[],
  version: null as number | null,
  saving: false,
})

function fillExam(c: Consultation | null) {
  Object.assign(exam, {
    findingsOd: c?.findingsOd ?? '',
    findingsOs: c?.findingsOs ?? '',
    treatmentPlan: c?.treatmentPlan ?? '',
    confidentialNote: c?.confidentialNote ?? '',
    diagnoses: c?.diagnoses.map((d) => ({ ...d })) ?? [],
    version: c?.version ?? null,
  })
}

async function load() {
  error.value = ''
  needsEmergency.value = false
  try {
    chart.value = await api<Chart>(`/consultations/${props.visitId}/chart`)
    fillExam(chart.value.consultation)
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'clinical_access_required') needsEmergency.value = true
    error.value = errorMessage(cause)
  }
}

const emergency = reactive({ reason: '', patientId: '' , saving: false })

async function requestEmergency() {
  emergency.saving = true
  try {
    const visit = await api<{ patientId: number }>(`/visits/${props.visitId}`)
    await api(`/clinical/patients/${visit.patientId}/emergency-access`, { method: 'POST', body: { reason: emergency.reason } })
    emergency.reason = ''
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    emergency.saving = false
  }
}

function explain(cause: unknown) {
  error.value = errorMessage(cause)
  notice.value = ''
}

async function saveExam() {
  exam.saving = true
  error.value = ''
  try {
    const saved = await api<Consultation>(`/consultations/${props.visitId}`, {
      method: 'PUT',
      body: {
        findingsOd: exam.findingsOd,
        findingsOs: exam.findingsOs,
        treatmentPlan: exam.treatmentPlan,
        confidentialNote: exam.confidentialNote,
        diagnoses: exam.diagnoses.filter((d) => d.text.trim()),
        version: exam.version,
      },
    })
    exam.version = saved.version
    notice.value = 'ذخیره شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    exam.saving = false
  }
}

async function finalize(closeVisit: boolean) {
  error.value = ''
  try {
    await api(`/consultations/${props.visitId}/finalize`, { method: 'POST', body: { version: exam.version, closeVisit } })
    notice.value = closeVisit ? 'معاینه نهایی و مراجعه تکمیل شد.' : 'معاینه نهایی شد.'
    await load()
  } catch (cause) {
    explain(cause)
  }
}

const amend = reactive({ field: 'TreatmentPlan', value: '', reason: '', saving: false })
const amendFields: Record<string, string> = {
  FindingsOd: 'یافته‌های چشم راست',
  FindingsOs: 'یافته‌های چشم چپ',
  TreatmentPlan: 'پلان درمان',
  ConfidentialNote: 'یادداشت محرمانه',
}

async function submitAmend() {
  amend.saving = true
  error.value = ''
  try {
    await api(`/consultations/${props.visitId}/amend`, {
      method: 'POST',
      body: { field: amend.field, value: amend.value, reason: amend.reason, version: chart.value!.consultation!.version },
    })
    Object.assign(amend, { value: '', reason: '' })
    notice.value = 'اصلاحیه ثبت شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    amend.saving = false
  }
}

const rx = reactive({ notes: '', items: [{ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 }], key: newIdempotencyKey(), saving: false })

async function submitRx() {
  rx.saving = true
  error.value = ''
  try {
    await api('/prescriptions', {
      method: 'POST',
      idempotencyKey: rx.key,
      body: { visitId: Number(props.visitId), notes: rx.notes, items: rx.items },
    })
    Object.assign(rx, { notes: '', items: [{ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 }], key: newIdempotencyKey() })
    notice.value = 'نسخه ثبت و برای دواخانه ارسال شد.'
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) rx.key = newIdempotencyKey()
  } finally {
    rx.saving = false
  }
}

const blankOptical = { sphereOd: '', cylinderOd: '', axisOd: '', addOd: '', sphereOs: '', cylinderOs: '', axisOs: '', addOs: '', pd: '', notes: '' }
const optical = reactive({ ...blankOptical, key: newIdempotencyKey(), saving: false })

async function submitOptical() {
  optical.saving = true
  error.value = ''
  try {
    const { key, saving, ...values } = optical
    await api('/optical-prescriptions', { method: 'POST', idempotencyKey: key, body: { visitId: Number(props.visitId), ...values } })
    Object.assign(optical, { ...blankOptical, key: newIdempotencyKey() })
    notice.value = 'نسخه عینک ثبت و برای بخش عینک ارسال شد.'
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) optical.key = newIdempotencyKey()
  } finally {
    optical.saving = false
  }
}

const tests = ref<{ id: number; name: string; isActive: boolean }[]>([])
const order = reactive({ testId: null as number | null, note: '', key: newIdempotencyKey(), saving: false })

async function submitOrder() {
  order.saving = true
  error.value = ''
  try {
    await api('/diagnostic-orders', {
      method: 'POST',
      idempotencyKey: order.key,
      body: { patientId: chart.value!.visit.patientId, visitId: Number(props.visitId), testId: order.testId, note: order.note },
    })
    Object.assign(order, { testId: null, note: '', key: newIdempotencyKey() })
    notice.value = 'درخواست برای آزمایشگاه ارسال شد.'
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) order.key = newIdempotencyKey()
  } finally {
    order.saving = false
  }
}

const attachmentsFor = ref<number | null>(null)

async function cancelItem(path: string, body?: unknown) {
  error.value = ''
  try {
    await api(path, { method: 'POST', body })
    notice.value = 'لغو شد.'
    await load()
  } catch (cause) {
    explain(cause)
  }
}

const cancelRx = (p: Prescription) => cancelItem(`/prescriptions/${p.id}/cancel?version=${p.version}`)
const cancelOptical = (o: OpticalRx) => cancelItem(`/optical-prescriptions/${o.id}/cancel?version=${o.version}`)
const cancelOrder = (o: DiagnosticOrder) => cancelItem(`/diagnostic-orders/${o.id}/cancel`, { version: o.version })

const surgeries = ref<SurgeryRow[]>([])
const services = ref<{ id: number; name: string; isActive: boolean; currentPrice: number | null }[]>([])
const surgery = reactive({ surgeryType: '', eye: 'OD', serviceId: null as number | null, key: newIdempotencyKey(), saving: false })

async function loadSurgeries() {
  if (!auth.can('surgery.view')) return
  const page = await api<{ items: SurgeryRow[] }>('/surgeries?pageSize=100')
  surgeries.value = page.items.filter((s) => s.visitId === Number(props.visitId))
}

async function submitSurgery() {
  surgery.saving = true
  error.value = ''
  try {
    await api('/surgeries', {
      method: 'POST',
      idempotencyKey: surgery.key,
      body: { visitId: Number(props.visitId), surgeryType: surgery.surgeryType, eye: surgery.eye, serviceId: surgery.serviceId },
    })
    Object.assign(surgery, { surgeryType: '', serviceId: null, key: newIdempotencyKey() })
    notice.value = 'پرونده عملیات ایجاد شد.'
    await loadSurgeries()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) surgery.key = newIdempotencyKey()
  } finally {
    surgery.saving = false
  }
}

const followUp = reactive({ dueDate: null as string | null, note: '', saving: false })

async function submitFollowUp() {
  if (!followUp.dueDate) {
    error.value = 'تاریخ پیگیری را وارد کنید.'
    return
  }
  followUp.saving = true
  error.value = ''
  try {
    await api('/follow-ups', { method: 'POST', body: { visitId: Number(props.visitId), dueDate: followUp.dueDate, note: followUp.note } })
    followUp.note = ''
    notice.value = 'مراجعه بعدی ثبت شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    followUp.saving = false
  }
}

const history = ref<HistoryItem[]>([])

async function loadHistory() {
  try {
    history.value = await api<HistoryItem[]>(`/clinical/patients/${chart.value!.visit.patientId}/history`)
  } catch (cause) {
    explain(cause)
  }
}

watch(tab, (value) => {
  if (value === 'history' && chart.value) loadHistory()
  if (value === 'surgery') {
    loadSurgeries().catch(explain)
    if (services.value.length === 0 && auth.can('surgery.create')) {
      api<typeof services.value>('/services').then((rows) => (services.value = rows.filter((s) => s.isActive && s.currentPrice !== null))).catch(explain)
    }
  }
  if (value === 'lab' && tests.value.length === 0 && auth.can('diagnostics.create')) {
    api<typeof tests.value>('/diagnostic-tests').then((rows) => (tests.value = rows.filter((t) => t.isActive))).catch(explain)
  }
})

onMounted(load)
watch(() => props.visitId, load)
</script>

<template>
  <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success py-2" role="status">{{ notice }}</div>

  <UiCard v-if="needsEmergency" title="دسترسی اضطراری">
    <p>این مریض به شما مربوط نیست. در حالت اضطرار می‌توانید با ثبت دلیل، برای ۴ ساعت دسترسی بگیرید. این دسترسی ثبت و قابل بررسی است.</p>
    <form @submit.prevent="requestEmergency">
      <label class="form-label" for="em-reason">دلیل</label>
      <input id="em-reason" v-model="emergency.reason" class="form-control mb-3" minlength="5" maxlength="500" required />
      <button type="submit" class="btn btn-primary" :disabled="emergency.saving">ثبت دلیل و باز کردن پرونده</button>
    </form>
  </UiCard>

  <div v-if="chart" class="d-grid gap-4">
    <UiCard :title="chart.visit.patientName">
      <dl class="row mb-0">
        <dt class="col-sm-2">شماره پرونده</dt>
        <dd class="col-sm-4"><RouterLink :to="`/patients/${chart.visit.patientId}`"><span class="ltr-num">{{ chart.visit.patientFileNumber }}</span></RouterLink></dd>
        <dt class="col-sm-2">مراجعه</dt>
        <dd class="col-sm-4"><span class="ltr-num">{{ chart.visit.visitNumber }}</span> — {{ formatDate(chart.visit.visitDate) }}</dd>
        <dt class="col-sm-2">علت</dt>
        <dd class="col-sm-10 mb-0">{{ chart.visit.reason ?? '—' }}</dd>
      </dl>
    </UiCard>

    <section class="xr-card">
      <div class="xr-card__header flex-wrap">
        <nav class="xr-tabs" role="tablist" aria-label="بخش‌های پرونده">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
        </nav>
      </div>
      <div class="xr-card__body">
        <template v-if="tab === 'exam'">
          <h3 class="h6">معاینه ابتدایی</h3>
          <p v-if="!chart.preExam" class="text-muted">معاینه ابتدایی ثبت نشده است.</p>
          <dl v-else class="row small mb-4">
            <dt class="col-sm-2">شکایت</dt><dd class="col-sm-10">{{ chart.preExam.chiefComplaint ?? '—' }}</dd>
            <dt class="col-sm-2">سابقه</dt><dd class="col-sm-10">{{ chart.preExam.history ?? '—' }}</dd>
            <dt class="col-sm-2">VA راست / چپ</dt><dd class="col-sm-4"><span class="ltr-num">{{ chart.preExam.visualAcuityOd ?? '—' }} / {{ chart.preExam.visualAcuityOs ?? '—' }}</span></dd>
            <dt class="col-sm-2">IOP راست / چپ</dt><dd class="col-sm-4"><span class="ltr-num">{{ chart.preExam.iopOd ?? '—' }} / {{ chart.preExam.iopOs ?? '—' }}</span></dd>
            <dt class="col-sm-2">یادداشت</dt><dd class="col-sm-10 mb-0">{{ chart.preExam.notes ?? '—' }}</dd>
          </dl>

          <form @submit.prevent="saveExam">
            <fieldset :disabled="!canEdit || finalized">
              <div class="row g-3 mb-3">
                <div class="col-md-6">
                  <label class="form-label" for="c-od">یافته‌های چشم راست (OD)</label>
                  <textarea id="c-od" v-model="exam.findingsOd" class="form-control" rows="3" maxlength="4000"></textarea>
                </div>
                <div class="col-md-6">
                  <label class="form-label" for="c-os">یافته‌های چشم چپ (OS)</label>
                  <textarea id="c-os" v-model="exam.findingsOs" class="form-control" rows="3" maxlength="4000"></textarea>
                </div>
              </div>
              <div class="mb-3">
                <div class="form-label">تشخیص</div>
                <div v-for="(d, i) in exam.diagnoses" :key="i" class="row g-2 mb-2">
                  <div class="col-3">
                    <select v-model="d.eye" class="form-select" :aria-label="`چشم تشخیص ${i + 1}`">
                      <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
                    </select>
                  </div>
                  <div class="col"><input v-model="d.text" class="form-control" maxlength="500" :aria-label="`متن تشخیص ${i + 1}`" /></div>
                  <div class="col-auto"><button type="button" class="btn btn-soft" aria-label="حذف تشخیص" @click="exam.diagnoses.splice(i, 1)"><i class="bi bi-x-lg" aria-hidden="true"></i></button></div>
                </div>
                <button type="button" class="btn btn-soft btn-sm" @click="exam.diagnoses.push({ eye: 'None', text: '' })">افزودن تشخیص</button>
              </div>
              <div class="mb-3">
                <label class="form-label" for="c-plan">پلان درمان</label>
                <textarea id="c-plan" v-model="exam.treatmentPlan" class="form-control" rows="2" maxlength="4000"></textarea>
              </div>
              <div v-if="canEdit" class="mb-3">
                <label class="form-label" for="c-note">یادداشت محرمانه داکتر</label>
                <textarea id="c-note" v-model="exam.confidentialNote" class="form-control" rows="2" maxlength="4000"></textarea>
              </div>
            </fieldset>
            <div v-if="canEdit && !finalized" class="d-flex flex-wrap gap-2">
              <button type="submit" class="btn btn-soft" :disabled="exam.saving">ذخیره</button>
              <template v-if="auth.can('consultation.approve') && exam.version !== null">
                <button type="button" class="btn btn-soft" @click="finalize(false)">نهایی‌سازی (مراجعه باز بماند)</button>
                <button type="button" class="btn btn-primary" @click="finalize(true)">نهایی‌سازی و تکمیل مراجعه</button>
              </template>
            </div>
          </form>

          <template v-if="finalized && canEdit">
            <hr />
            <h3 class="h6">ثبت اصلاحیه</h3>
            <form @submit.prevent="submitAmend">
              <div class="row g-3 mb-3">
                <div class="col-md-4">
                  <label class="form-label" for="a-field">فیلد</label>
                  <select id="a-field" v-model="amend.field" class="form-select">
                    <option v-for="(label, value) in amendFields" :key="value" :value="value">{{ label }}</option>
                  </select>
                </div>
                <div class="col-md-8">
                  <label class="form-label" for="a-reason">دلیل اصلاح</label>
                  <input id="a-reason" v-model="amend.reason" class="form-control" minlength="3" maxlength="500" required />
                </div>
              </div>
              <label class="form-label" for="a-value">متن جدید</label>
              <textarea id="a-value" v-model="amend.value" class="form-control mb-3" rows="2" maxlength="4000"></textarea>
              <button type="submit" class="btn btn-primary" :disabled="amend.saving">ثبت اصلاحیه</button>
            </form>
          </template>

          <template v-if="chart.amendments.length">
            <hr />
            <h3 class="h6">تاریخچه اصلاحیه‌ها</h3>
            <div class="xr-table-wrap">
              <table class="xr-table">
                <thead><tr><th>فیلد</th><th>قبلی</th><th>جدید</th><th>دلیل</th><th>کاربر</th><th>زمان</th></tr></thead>
                <tbody>
                  <tr v-for="(a, i) in chart.amendments" :key="i">
                    <td>{{ amendFields[a.field] ?? a.field }}</td>
                    <td>{{ a.oldValue ?? '—' }}</td>
                    <td>{{ a.newValue ?? '—' }}</td>
                    <td>{{ a.reason }}</td>
                    <td>{{ a.byUser ?? '—' }}</td>
                    <td>{{ formatDateTime(a.createdAt) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </template>

          <template v-if="canEdit">
            <hr />
            <h3 class="h6">مراجعه بعدی</h3>
            <ul v-if="chart.followUps.length" class="list-unstyled">
              <li v-for="f in chart.followUps" :key="f.id">{{ formatDate(f.dueDate) }} — {{ f.note ?? '' }}</li>
            </ul>
            <form class="row g-3 align-items-end" @submit.prevent="submitFollowUp">
              <div class="col-md-6"><ShamsiDateInput v-model="followUp.dueDate" label="تاریخ پیگیری" /></div>
              <div class="col-md-4">
                <label class="form-label" for="f-note">یادداشت</label>
                <input id="f-note" v-model="followUp.note" class="form-control" maxlength="300" />
              </div>
              <div class="col-md-2"><button type="submit" class="btn btn-soft w-100" :disabled="followUp.saving">ثبت پیگیری</button></div>
            </form>
          </template>
        </template>

        <template v-else-if="tab === 'rx'">
          <div v-for="p in chart.prescriptions" :key="p.id" class="mb-3">
            <strong class="ltr-num">{{ p.rxNumber }}</strong> — {{ rxStatusLabels[p.status] }}
            <RouterLink v-if="auth.can('prescriptions.print')" :to="`/print/rx/${p.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">چاپ</RouterLink>
            <button v-if="p.status === 'Issued' && auth.can('prescriptions.cancel')" type="button" class="btn btn-soft btn-sm ms-2" @click="cancelRx(p)">لغو نسخه</button>
            <ul class="mb-0">
              <li v-for="item in p.items" :key="item.id">{{ item.drugName }} — {{ item.dose ?? '' }} {{ item.route ?? '' }} {{ item.duration ?? '' }} {{ item.instructions ?? '' }} (تعداد: {{ item.quantity }})</li>
            </ul>
          </div>
          <p v-if="chart.prescriptions.length === 0" class="text-muted">نسخه‌ای ثبت نشده است.</p>
          <template v-if="auth.can('prescriptions.create')">
            <hr />
            <form @submit.prevent="submitRx">
              <div v-for="(item, i) in rx.items" :key="i" class="row g-2 mb-2">
                <div class="col-md-3"><input v-model="item.drugName" class="form-control" placeholder="نام دوا" :aria-label="`نام دوا ${i + 1}`" maxlength="200" required /></div>
                <div class="col-md-2"><input v-model="item.dose" class="form-control" placeholder="مقدار" :aria-label="`مقدار ${i + 1}`" maxlength="100" /></div>
                <div class="col-md-2"><input v-model="item.route" class="form-control" placeholder="روش مصرف" :aria-label="`روش مصرف ${i + 1}`" maxlength="100" /></div>
                <div class="col-md-2"><input v-model="item.duration" class="form-control" placeholder="مدت" :aria-label="`مدت ${i + 1}`" maxlength="100" /></div>
                <div class="col-md-2"><input v-model="item.instructions" class="form-control" placeholder="توضیح" :aria-label="`توضیح ${i + 1}`" maxlength="300" /></div>
                <div class="col-md-1"><input v-model.number="item.quantity" class="form-control" type="number" min="0.001" step="any" :aria-label="`تعداد ${i + 1}`" required /></div>
              </div>
              <button type="button" class="btn btn-soft btn-sm mb-3" @click="rx.items.push({ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 })">افزودن دوا</button>
              <label class="form-label d-block" for="rx-notes">توضیحات نسخه</label>
              <input id="rx-notes" v-model="rx.notes" class="form-control mb-3" maxlength="1000" />
              <button type="submit" class="btn btn-primary" :disabled="rx.saving">ثبت نسخه</button>
            </form>
          </template>
        </template>

        <template v-else-if="tab === 'optical'">
          <div v-if="chart.opticalPrescriptions.length" class="xr-table-wrap mb-3">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>چشم</th><th>Sphere</th><th>Cylinder</th><th>Axis</th><th>ADD</th><th>PD</th></tr></thead>
              <tbody>
                <template v-for="o in chart.opticalPrescriptions" :key="o.id">
                  <tr><td><span class="ltr-num">{{ o.rxNumber }}</span> <RouterLink v-if="auth.can('opticalrx.print')" :to="`/print/optical/${o.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ</RouterLink> <button v-if="!o.isCancelled && auth.can('opticalrx.cancel')" type="button" class="btn btn-soft btn-sm" @click="cancelOptical(o)">لغو</button></td><td>راست</td><td><span class="ltr-num">{{ o.sphereOd ?? '—' }}</span></td><td><span class="ltr-num">{{ o.cylinderOd ?? '—' }}</span></td><td><span class="ltr-num">{{ o.axisOd ?? '—' }}</span></td><td><span class="ltr-num">{{ o.addOd ?? '—' }}</span></td><td><span class="ltr-num">{{ o.pd ?? '—' }}</span></td></tr>
                  <tr><td></td><td>چپ</td><td><span class="ltr-num">{{ o.sphereOs ?? '—' }}</span></td><td><span class="ltr-num">{{ o.cylinderOs ?? '—' }}</span></td><td><span class="ltr-num">{{ o.axisOs ?? '—' }}</span></td><td><span class="ltr-num">{{ o.addOs ?? '—' }}</span></td><td></td></tr>
                </template>
              </tbody>
            </table>
          </div>
          <p v-else class="text-muted">نسخه عینک ثبت نشده است.</p>
          <form v-if="auth.can('opticalrx.create')" @submit.prevent="submitOptical">
            <div class="row g-2 mb-2">
              <div class="col-12 small fw-bold">چشم راست (OD)</div>
              <div class="col-3"><input v-model="optical.sphereOd" class="form-control" dir="ltr" placeholder="Sphere" aria-label="Sphere OD" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.cylinderOd" class="form-control" dir="ltr" placeholder="Cylinder" aria-label="Cylinder OD" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.axisOd" class="form-control" dir="ltr" placeholder="Axis" aria-label="Axis OD" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.addOd" class="form-control" dir="ltr" placeholder="ADD" aria-label="ADD OD" maxlength="10" /></div>
              <div class="col-12 small fw-bold mt-3">چشم چپ (OS)</div>
              <div class="col-3"><input v-model="optical.sphereOs" class="form-control" dir="ltr" placeholder="Sphere" aria-label="Sphere OS" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.cylinderOs" class="form-control" dir="ltr" placeholder="Cylinder" aria-label="Cylinder OS" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.axisOs" class="form-control" dir="ltr" placeholder="Axis" aria-label="Axis OS" maxlength="10" /></div>
              <div class="col-3"><input v-model="optical.addOs" class="form-control" dir="ltr" placeholder="ADD" aria-label="ADD OS" maxlength="10" /></div>
            </div>
            <div class="row g-2 mb-3">
              <div class="col-3"><input v-model="optical.pd" class="form-control" dir="ltr" placeholder="PD" aria-label="PD" maxlength="10" /></div>
              <div class="col-9"><input v-model="optical.notes" class="form-control" placeholder="توضیحات" aria-label="توضیحات نسخه عینک" maxlength="1000" /></div>
            </div>
            <button type="submit" class="btn btn-primary" :disabled="optical.saving">ثبت نسخه عینک</button>
          </form>
        </template>

        <template v-else-if="tab === 'lab'">
          <div v-if="chart.diagnosticOrders.length" class="xr-table-wrap mb-3">
            <table class="xr-table">
              <thead><tr><th>شماره</th><th>آزمایش</th><th>وضعیت</th><th>نتیجه</th><th>زمان نتیجه</th><th></th></tr></thead>
              <tbody>
                <tr v-for="o in chart.diagnosticOrders" :key="o.id">
                  <td><span class="ltr-num">{{ o.orderNumber }}</span></td>
                  <td>{{ o.testName }}</td>
                  <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[o.status]}`">{{ diagnosticStatusLabels[o.status] }}</span></td>
                  <td>{{ o.resultText ?? '—' }}</td>
                  <td>{{ formatDateTime(o.performedAt) }}</td>
                  <td class="text-nowrap">
                    <button type="button" class="btn btn-soft btn-sm" @click="attachmentsFor = attachmentsFor === o.id ? null : o.id">فایل‌ها</button>
                    <button v-if="['Requested', 'InProgress'].includes(o.status) && auth.can('diagnostics.cancel')" type="button" class="btn btn-soft btn-sm ms-2" @click="cancelOrder(o)">لغو</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p v-else class="text-muted">درخواستی ثبت نشده است.</p>
          <AttachmentPanel v-if="attachmentsFor" :key="attachmentsFor" class="mb-3" owner-type="DiagnosticOrder" :owner-id="attachmentsFor" :can-upload="false" />
          <form v-if="auth.can('diagnostics.create')" class="row g-3 align-items-end" @submit.prevent="submitOrder">
            <div class="col-md-5">
              <label class="form-label" for="o-test">آزمایش</label>
              <select id="o-test" v-model.number="order.testId" class="form-select" required>
                <option :value="null" disabled>انتخاب کنید</option>
                <option v-for="t in tests" :key="t.id" :value="t.id">{{ t.name }}</option>
              </select>
            </div>
            <div class="col-md-5">
              <label class="form-label" for="o-note">توضیح</label>
              <input id="o-note" v-model="order.note" class="form-control" maxlength="500" />
            </div>
            <div class="col-md-2"><button type="submit" class="btn btn-primary w-100" :disabled="order.saving">درخواست</button></div>
          </form>
        </template>

        <template v-else-if="tab === 'surgery'">
          <ul v-if="surgeries.length" class="list-unstyled">
            <li v-for="s in surgeries" :key="s.id" class="mb-2"><span class="ltr-num">{{ s.caseNumber }}</span> — {{ s.surgeryType }} — {{ eyeLabels[s.eye] }} — {{ surgeryStatusLabels[s.status] }}</li>
          </ul>
          <p v-else class="text-muted">پرونده عملیاتی ثبت نشده است.</p>
          <form v-if="auth.can('surgery.create')" class="row g-3 align-items-end" @submit.prevent="submitSurgery">
            <div class="col-md-4">
              <label class="form-label" for="s-type">نوع عملیات</label>
              <input id="s-type" v-model="surgery.surgeryType" class="form-control" maxlength="200" required />
            </div>
            <div class="col-md-3">
              <label class="form-label" for="s-service">خدمت قابل پرداخت</label>
              <select id="s-service" v-model.number="surgery.serviceId" class="form-select">
                <option :value="null">بدون هزینه خودکار</option>
                <option v-for="s in services" :key="s.id" :value="s.id">{{ s.name }}</option>
              </select>
            </div>
            <div class="col-md-2">
              <label class="form-label" for="s-eye">چشم</label>
              <select id="s-eye" v-model="surgery.eye" class="form-select">
                <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
              </select>
            </div>
            <div class="col-md-3"><button type="submit" class="btn btn-primary w-100" :disabled="surgery.saving">ارجاع به عملیات</button></div>
          </form>
        </template>

        <template v-else>
          <p v-if="history.length === 0" class="text-muted">سابقه‌ای پیدا نشد.</p>
          <div v-else class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>تاریخ</th><th>بخش</th><th>داکتر</th><th>تشخیص</th><th>پلان</th><th></th></tr></thead>
              <tbody>
                <tr v-for="h in history" :key="h.visitId">
                  <td>{{ formatDate(h.visitDate) }}</td>
                  <td>{{ h.departmentName }}</td>
                  <td>{{ h.doctorName ?? '—' }}</td>
                  <td>{{ h.diagnoses.map((d) => d.text).join('، ') || '—' }}</td>
                  <td>{{ h.treatmentPlan ?? '—' }}</td>
                  <td><RouterLink :to="`/chart/${h.visitId}`" class="btn btn-soft btn-sm">باز کردن</RouterLink></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>
    </section>
  </div>
</template>
