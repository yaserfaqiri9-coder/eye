<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { testOptions } from '@/api/lookups'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiAutocomplete from '@/components/UiAutocomplete.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import TransferDoctorModal from '@/features/visits/TransferDoctorModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'
import LensPicker from './LensPicker.vue'
import PatientBanner from './PatientBanner.vue'
import PreExamDocument from './PreExamDocument.vue'
import RxGrid, { type RxRow } from './RxGrid.vue'
import {
  anteriorRows,
  parseFindings,
  parseMeasurements,
  parseOpticalNotes,
  parsePlan,
  parseRefractionExtras,
  posteriorRows,
  serializeFindings,
  serializeOpticalNotes,
  serializePlan,
  systemicConditions,
  type EyeFindings,
  type ExamRow,
} from './examRecord'
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
  type Refraction,
  type SurgeryPackage,
  type SurgeryRow,
} from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ visitId: string }>()
const auth = useAuthStore()

const chart = ref<Chart | null>(null)
const refraction = ref<Refraction | null>(null)
const error = ref('')
const notice = ref('')
const needsEmergency = ref(false)
const tab = ref('exam')

const tabs = [
  { key: 'exam', label: 'معاینه' },
  { key: 'plan', label: 'تشخیص و پلان' },
  { key: 'rx', label: 'نسخه دوا' },
  { key: 'optical', label: 'نسخه عینک' },
  { key: 'lab', label: 'رفرکشن، عکس‌برداری و آزمایش' },
  { key: 'surgery', label: 'عملیات' },
  { key: 'followup', label: 'پیگیری' },
  { key: 'history', label: 'سوابق' },
]

const canEdit = computed(() => auth.can('consultation.edit'))
const finalized = computed(() => !!chart.value?.consultation?.finalizedAt)

const blankFindings = (): EyeFindings => ({ values: {}, note: '' })

const exam = reactive({
  od: blankFindings(),
  os: blankFindings(),
  history: '',
  treatmentPlan: '',
  advice: '',
  confidentialNote: '',
  diagnoses: [] as { eye: string; text: string }[],
})

const { dirty, markSaved, applying } = useUnsavedGuard(exam)
const version = ref<number | null>(null)
const saving = ref(false)
const posteriorOpen = ref(false)

const examSections = computed(() => [
  { key: 'anterior', title: 'بخش قدامی (Anterior Segment)', rows: anteriorRows, open: true },
  { key: 'posterior', title: 'بخش خلفی (Posterior Segment)', rows: posteriorRows, open: posteriorOpen.value },
])

function filledCount(rows: ExamRow[]) {
  return rows.filter((row) => exam.od.values[row.code]?.trim() || exam.os.values[row.code]?.trim()).length
}

function fillExam(c: Consultation | null) {
  const parsedPlan = parsePlan(c?.treatmentPlan)
  applying(() => {
    Object.assign(exam, {
      od: parseFindings(c?.findingsOd),
      os: parseFindings(c?.findingsOs),
      history: parsedPlan.history,
      treatmentPlan: parsedPlan.plan,
      advice: parsedPlan.advice,
      confidentialNote: c?.confidentialNote ?? '',
      diagnoses: c?.diagnoses.map((d) => ({ ...d })) ?? [],
    })
  })
  version.value = c?.version ?? null
  posteriorOpen.value = filledCount(posteriorRows) > 0
}

const preExtras = computed(() => parseMeasurements(chart.value?.preExam?.measurements))
const preExamOpen = ref(false)
const currentChartVisit = computed(() => chart.value?.visit.id === Number(props.visitId))
const preExamWarnings = computed(() => [
  ...systemicConditions.filter((condition) => preExtras.value.systemic[condition.code].answer === 'Y').map((condition) => condition.label),
  ...(preExtras.value.allergy === 'Y' ? [`حساسیت${preExtras.value.allergicTo ? `: ${preExtras.value.allergicTo}` : ''}`] : []),
])

const finalLines = computed(() => {
  const lines = refraction.value?.lines.filter((l) => l.stage === 'Final') ?? []
  return lines.length ? lines : null
})

const finalEyes = computed(() =>
  (['OD', 'OS'] as const).map((code) => ({
    code,
    tone: code === 'OD' ? 'xr-eye--od' : 'xr-eye--os',
    line: finalLines.value?.find((l) => l.eye === code) ?? null,
  })),
)

async function loadRefraction() {
  if (!auth.can('preexam.view')) return
  // The summary is optional: the chart stays usable if this one read fails.
  refraction.value = (await api<Refraction | undefined>(`/refractions/${props.visitId}`).catch(() => undefined)) ?? null
}

async function load() {
  error.value = ''
  needsEmergency.value = false
  try {
    const sameVisit = chart.value?.visit.id === Number(props.visitId)
    chart.value = await api<Chart>(`/consultations/${props.visitId}/chart`)
    // Reloading after an order or a prescription must never overwrite what the doctor is still typing.
    if (!sameVisit || !dirty.value) fillExam(chart.value.consultation)
    // The exam can start now; the refraction summary fills in when it arrives.
    loadRefraction()
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'clinical_access_required') needsEmergency.value = true
    error.value = errorMessage(cause)
  }
}

const transferring = ref(false)

async function onTransferred() {
  transferring.value = false
  notice.value = 'مسئولیت این مریض به داکتر جدید انتقال یافت.'
  await load()
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

async function persistExam() {
  const saved = await api<Consultation>(`/consultations/${props.visitId}`, {
    method: 'PUT',
    body: {
      findingsOd: serializeFindings(exam.od),
      findingsOs: serializeFindings(exam.os),
      treatmentPlan: serializePlan({ history: exam.history, plan: exam.treatmentPlan, advice: exam.advice }),
      confidentialNote: exam.confidentialNote,
      diagnoses: exam.diagnoses.filter((d) => d.text.trim()),
      version: version.value,
    },
  })
  version.value = saved.version
  markSaved()
}

async function saveExam() {
  saving.value = true
  error.value = ''
  try {
    await persistExam()
    notice.value = 'ذخیره شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    saving.value = false
  }
}

async function finalize(closeVisit: boolean) {
  saving.value = true
  error.value = ''
  try {
    // Unsaved typing goes in first, so finalising can never drop what is on screen.
    if (dirty.value) await persistExam()
    await api(`/consultations/${props.visitId}/finalize`, { method: 'POST', body: { version: version.value, closeVisit } })
    notice.value = closeVisit ? 'معاینه نهایی و مراجعه تکمیل شد.' : 'معاینه نهایی شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    saving.value = false
  }
}

const visitOpen = computed(() => !['Completed', 'Cancelled'].includes(chart.value?.visit.status ?? ''))
const reopen = reactive({ reason: '', saving: false })

async function submitReopen() {
  reopen.saving = true
  error.value = ''
  try {
    await api(`/consultations/${props.visitId}/reopen`, { method: 'POST', body: { reason: reopen.reason, version: chart.value!.consultation!.version } })
    reopen.reason = ''
    notice.value = 'معاینه باز شد. یافته‌ها و تشخیص‌های قبلی حفظ شده‌اند.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    reopen.saving = false
  }
}

const amend = reactive({ field: 'TreatmentPlan', value: '', reason: '', saving: false })
const amendFields: Record<string, string> = {
  FindingsOd: 'یافته‌های چشم راست',
  FindingsOs: 'یافته‌های چشم چپ',
  TreatmentPlan: 'پلان درمان',
  ConfidentialNote: 'یادداشت محرمانه',
}

/** Everything else the history can show: exam, refraction, prescriptions, lab results and surgery. */
const historyFields: Record<string, string> = {
  ...amendFields,
  ChiefComplaint: 'شکایت اصلی',
  History: 'سوابق',
  VisualAcuityOd: 'دید چشم راست',
  VisualAcuityOs: 'دید چشم چپ',
  IopOd: 'فشار چشم راست',
  IopOs: 'فشار چشم چپ',
  Measurements: 'اندازه‌گیری‌ها',
  Notes: 'یادداشت',
  UnaidedVaOd: 'دید بدون اصلاح راست',
  UnaidedVaOs: 'دید بدون اصلاح چپ',
  PinholeVaOd: 'Pinhole راست',
  PinholeVaOs: 'Pinhole چپ',
  NearVaOd: 'دید نزدیک راست',
  NearVaOs: 'دید نزدیک چپ',
  Pd: 'PD',
  Lines: 'ردیف‌های بینایی‌سنجی',
  Items: 'دواهای نسخه',
  SphereOd: 'SPH راست',
  CylinderOd: 'CYL راست',
  AxisOd: 'AXIS راست',
  AddOd: 'ADD راست',
  SphereOs: 'SPH چپ',
  CylinderOs: 'CYL چپ',
  AxisOs: 'AXIS چپ',
  AddOs: 'ADD چپ',
  ResultText: 'نتیجه آزمایش',
  SurgeryType: 'نوع عملیات',
  Eye: 'چشم عملیات',
  PreOpChecks: 'بررسی‌های پیش از عملیات',
  OperationReport: 'گزارش عملیات',
  PostOpStatus: 'وضعیت پس از عملیات',
}

const amendCurrent = computed(() => {
  const c = chart.value?.consultation
  if (!c) return ''
  return { FindingsOd: c.findingsOd, FindingsOs: c.findingsOs, TreatmentPlan: c.treatmentPlan, ConfidentialNote: c.confidentialNote }[amend.field] ?? ''
})

// An amendment replaces the whole field, so it starts from the current text instead of an empty box.
watch(amendCurrent, (value) => (amend.value = value), { immediate: true })

async function submitAmend() {
  amend.saving = true
  error.value = ''
  try {
    await api(`/consultations/${props.visitId}/amend`, {
      method: 'POST',
      body: { field: amend.field, value: amend.value, reason: amend.reason, version: chart.value!.consultation!.version },
    })
    amend.reason = ''
    notice.value = 'اصلاحیه ثبت شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    amend.saving = false
  }
}

const rx = reactive({ editing: null as Prescription | null, reason: '', notes: '', items: [{ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 }], key: newIdempotencyKey(), saving: false })

function editRx(p: Prescription) {
  Object.assign(rx, {
    editing: p,
    reason: '',
    notes: p.notes ?? '',
    items: p.items.map((i) => ({ drugName: i.drugName, dose: i.dose ?? '', route: i.route ?? '', duration: i.duration ?? '', instructions: i.instructions ?? '', quantity: i.quantity })),
  })
}

function clearRx() {
  Object.assign(rx, { editing: null, reason: '', notes: '', items: [{ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 }], key: newIdempotencyKey() })
}

/** Once the exam is final or the visit closed, the server asks for a reason on any change. */
const needsAmendReason = computed(() => finalized.value || chart.value?.visit.status === 'Completed')

const searchDrugs = (query: string, signal: AbortSignal) =>
  api<string[]>(`/prescriptions/drugs?${new URLSearchParams({ q: query })}`, { signal })

async function submitRx() {
  rx.saving = true
  error.value = ''
  try {
    if (rx.editing) {
      await api(`/prescriptions/${rx.editing.id}`, { method: 'PUT', body: { notes: rx.notes, items: rx.items, version: rx.editing.version, reason: rx.reason || undefined } })
      notice.value = 'نسخه ویرایش شد.'
    } else {
      await api('/prescriptions', {
        method: 'POST',
        idempotencyKey: rx.key,
        body: { visitId: Number(props.visitId), notes: rx.notes, items: rx.items },
      })
      notice.value = 'نسخه ثبت و برای دواخانه ارسال شد.'
    }
    clearRx()
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) rx.key = newIdempotencyKey()
  } finally {
    rx.saving = false
  }
}

const blankRow = (): RxRow => ({ sphere: '', cylinder: '', axis: '', add: '', va: '' })
const optical = reactive({ editing: null as OpticalRx | null, reason: '', od: blankRow(), os: blankRow(), pd: '', uvaOd: '', uvaOs: '', lens: [] as string[], notes: '', key: newIdempotencyKey(), saving: false })

function editOptical(o: OpticalRx) {
  const notes = parseOpticalNotes(o.notes)
  Object.assign(optical, {
    editing: o,
    reason: '',
    od: { sphere: o.sphereOd ?? '', cylinder: o.cylinderOd ?? '', axis: o.axisOd ?? '', add: o.addOd ?? '', va: notes.vaOd },
    os: { sphere: o.sphereOs ?? '', cylinder: o.cylinderOs ?? '', axis: o.axisOs ?? '', add: o.addOs ?? '', va: notes.vaOs },
    pd: o.pd ?? '',
    uvaOd: notes.uvaOd,
    uvaOs: notes.uvaOs,
    lens: notes.lens,
    notes: notes.notes,
  })
}

function clearOptical() {
  Object.assign(optical, { editing: null, reason: '', od: blankRow(), os: blankRow(), pd: '', uvaOd: '', uvaOs: '', lens: [], notes: '', key: newIdempotencyKey() })
}

/** Copies the optometrist's final values into the form for the doctor to review. Nothing is issued until they submit. */
function copyFinalToOptical() {
  for (const eye of ['OD', 'OS'] as const) {
    const line = finalLines.value?.find((l) => l.eye === eye)
    optical[eye === 'OD' ? 'od' : 'os'] = { sphere: line?.sphere ?? '', cylinder: line?.cylinder ?? '', axis: line?.axis ?? '', add: line?.add ?? '', va: line?.va ?? '' }
  }
  optical.pd = refraction.value?.pd ?? ''
  optical.uvaOd = refraction.value?.unaidedVaOd ?? ''
  optical.uvaOs = refraction.value?.unaidedVaOs ?? ''
  optical.lens = parseRefractionExtras(refraction.value?.measurements).lens
}

async function submitOptical() {
  optical.saving = true
  error.value = ''
  try {
    const body = {
      sphereOd: optical.od.sphere,
      cylinderOd: optical.od.cylinder,
      axisOd: optical.od.axis,
      addOd: optical.od.add,
      sphereOs: optical.os.sphere,
      cylinderOs: optical.os.cylinder,
      axisOs: optical.os.axis,
      addOs: optical.os.add,
      pd: optical.pd,
      notes: serializeOpticalNotes({ lens: optical.lens, vaOd: optical.od.va, vaOs: optical.os.va, uvaOd: optical.uvaOd, uvaOs: optical.uvaOs, notes: optical.notes }),
    }
    if (optical.editing) {
      await api(`/optical-prescriptions/${optical.editing.id}`, { method: 'PUT', body: { ...body, version: optical.editing.version, reason: optical.reason || undefined } })
      notice.value = 'نسخه عینک ویرایش شد.'
    } else {
      await api('/optical-prescriptions', { method: 'POST', idempotencyKey: optical.key, body: { ...body, visitId: Number(props.visitId) } })
      notice.value = 'نسخه عینک ثبت و برای بخش عینک ارسال شد.'
    }
    clearOptical()
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) optical.key = newIdempotencyKey()
  } finally {
    optical.saving = false
  }
}

const tests = ref<(ComboOption & { kind: string })[]>([])
const testsLoading = ref(false)
const order = reactive({ testId: null as number | null, note: '', returnToDoctor: true, key: newIdempotencyKey(), saving: false })
const orderKind = computed(() => tests.value.find((t) => t.value === order.testId)?.kind ?? 'Lab')

async function submitOrder() {
  order.saving = true
  error.value = ''
  try {
    // The patient may be away for a while, so what is on screen goes onto the record first.
    if (dirty.value) await persistExam()
    const kind = orderKind.value
    await api('/diagnostic-orders', {
      method: 'POST',
      idempotencyKey: order.key,
      body: { patientId: chart.value!.visit.patientId, visitId: Number(props.visitId), testId: order.testId, note: order.note, returnToDoctor: order.returnToDoctor },
    })
    Object.assign(order, { testId: null, note: '', returnToDoctor: true, key: newIdempotencyKey() })
    notice.value = kind === 'Lab' ? 'درخواست برای آزمایشگاه ارسال شد.' : 'درخواست ثبت شد. پس از پرداخت کامل فیس شروع می‌شود.'
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) order.key = newIdempotencyKey()
  } finally {
    order.saving = false
  }
}

const attachmentsFor = ref<number | null>(null)

const noteEdit = reactive({ order: null as DiagnosticOrder | null, note: '', saving: false, error: '' })

async function saveOrderNote() {
  noteEdit.saving = true
  noteEdit.error = ''
  try {
    await api(`/diagnostic-orders/${noteEdit.order!.id}/note`, { method: 'PUT', body: { note: noteEdit.note, version: noteEdit.order!.version } })
    noteEdit.order = null
    notice.value = 'توضیح درخواست ویرایش شد.'
    await load()
  } catch (cause) {
    noteEdit.error = errorMessage(cause)
  } finally {
    noteEdit.saving = false
  }
}

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
const services = ref<ComboOption[]>([])
const surgery = reactive({ surgeryType: '', eye: 'OD', serviceId: null as number | null, packageId: null as number | null, key: newIdempotencyKey(), saving: false })
const packages = ref<SurgeryPackage[]>([])
const packageOptions = computed<ComboOption[]>(() => packages.value.map((p) => ({ value: p.id, label: p.name, hint: p.price === null ? 'بدون قیمت' : money(p.price) })))

/** A package brings its name and its fee, so the doctor picks one thing. */
watch(
  () => surgery.packageId,
  (id) => {
    const chosen = packages.value.find((p) => p.id === id)
    if (chosen) Object.assign(surgery, { surgeryType: chosen.name, serviceId: chosen.serviceId })
  },
)

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
      body: { visitId: Number(props.visitId), surgeryType: surgery.surgeryType, eye: surgery.eye, serviceId: surgery.packageId ? null : surgery.serviceId, packageId: surgery.packageId },
    })
    Object.assign(surgery, { surgeryType: '', serviceId: null, packageId: null, key: newIdempotencyKey() })
    notice.value = 'پرونده عملیات ایجاد شد.'
    await loadSurgeries()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) surgery.key = newIdempotencyKey()
  } finally {
    surgery.saving = false
  }
}

const followUp = reactive({ editing: null as number | null, dueDate: null as string | null, note: '', saving: false })

function editFollowUp(f: { id: number; dueDate: string; note: string | null }) {
  Object.assign(followUp, { editing: f.id, dueDate: f.dueDate, note: f.note ?? '' })
}

function clearFollowUp() {
  Object.assign(followUp, { editing: null, dueDate: null, note: '' })
}

async function removeFollowUp(f: { id: number; dueDate: string }) {
  if (!window.confirm(`پیگیری ${formatDate(f.dueDate)} حذف شود؟`)) return
  error.value = ''
  try {
    await api(`/follow-ups/${f.id}`, { method: 'DELETE' })
    notice.value = 'پیگیری حذف شد.'
    if (followUp.editing === f.id) clearFollowUp()
    await load()
  } catch (cause) {
    explain(cause)
  }
}

async function submitFollowUp() {
  if (!followUp.dueDate) {
    error.value = 'تاریخ پیگیری را وارد کنید.'
    return
  }
  followUp.saving = true
  error.value = ''
  try {
    if (followUp.editing) {
      await api(`/follow-ups/${followUp.editing}`, { method: 'PUT', body: { dueDate: followUp.dueDate, note: followUp.note } })
      notice.value = 'پیگیری ویرایش شد.'
      clearFollowUp()
    } else {
      await api('/follow-ups', { method: 'POST', body: { visitId: Number(props.visitId), dueDate: followUp.dueDate, note: followUp.note } })
      followUp.note = ''
      notice.value = 'مراجعه بعدی ثبت شد.'
    }
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
  notice.value = ''
  if (value === 'history' && chart.value) loadHistory()
  if (value === 'surgery') {
    loadSurgeries().catch(explain)
    if (auth.can('surgery.create')) {
      // Prices are read fresh each time, never from a cache.
      api<{ id: number; name: string; isActive: boolean; currentPrice: number | null }[]>('/services')
        .then((rows) => (services.value = rows.filter((s) => s.isActive && s.currentPrice !== null).map((s) => ({ value: s.id, label: s.name, hint: money(s.currentPrice) }))))
        .catch(explain)
      api<SurgeryPackage[]>('/surgery-packages')
        .then((rows) => (packages.value = rows))
        .catch(explain)
    }
  }
  if (value === 'lab' && auth.can('diagnostics.create')) {
    testsLoading.value = true
    testOptions()
      .then((rows) => (tests.value = rows))
      .catch(explain)
      .finally(() => (testsLoading.value = false))
  }
})

onMounted(load)
watch(() => props.visitId, () => {
  preExamOpen.value = false
  load()
})
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>

  <UiCard v-if="needsEmergency" title="دسترسی اضطراری">
    <p>این مریض به شما مربوط نیست. در حالت اضطرار می‌توانید با ثبت دلیل، برای ۴ ساعت دسترسی بگیرید. این دسترسی ثبت و قابل بررسی است.</p>
    <form @submit.prevent="requestEmergency">
      <label class="form-label" for="em-reason">دلیل</label>
      <input id="em-reason" v-model="emergency.reason" class="form-control mb-3" minlength="5" maxlength="500" required />
      <button type="submit" class="btn btn-primary" :disabled="emergency.saving">ثبت دلیل و باز کردن پرونده</button>
    </form>
  </UiCard>

  <!-- Opens at once in the shape of the page; nothing can be typed until this visit's record has arrived. -->
  <div v-if="!chart && !error && !needsEmergency" class="d-grid gap-4" role="status" aria-label="در حال بارگذاری پرونده">
    <section class="xr-card chart-placeholder">
      <span class="xr-skeleton xr-skeleton--title"></span>
      <span class="xr-skeleton xr-skeleton--wide"></span>
    </section>
    <section class="xr-card chart-placeholder chart-placeholder--summary">
      <span v-for="n in 6" :key="n" class="xr-skeleton"></span>
    </section>
    <section class="xr-card">
      <div class="xr-card__header"><nav class="xr-tabs"><span v-for="t in tabs" :key="t.key" class="xr-tab">{{ t.label }}</span></nav></div>
      <div class="xr-card__body chart-placeholder">
        <span v-for="n in 9" :key="n" class="xr-skeleton"></span>
      </div>
    </section>
  </div>

  <div v-if="chart" class="d-grid gap-4">
    <PatientBanner :visit="chart.visit" />
    <div v-if="canEdit && !['Completed', 'Cancelled'].includes(chart.visit.status)" class="d-flex justify-content-end">
      <button type="button" class="btn btn-soft" @click="transferring = true">انتقال مسئولیت به داکتر دیگر</button>
    </div>
    <TransferDoctorModal v-if="transferring" :visit="chart.visit" @close="transferring = false" @saved="onTransferred" />

    <section class="xr-card summary" aria-label="خلاصه پیش از معاینه">
      <div class="summary__block">
        <h3 class="summary__title">معاینه ابتدایی</h3>
        <p class="summary__document-hint">دید، فشار چشم، سوابق و یادداشت‌ها در یک سند منظم</p>
        <button type="button" class="btn btn-soft summary__document-button" :disabled="!chart.preExam || !currentChartVisit" aria-haspopup="dialog" @click="preExamOpen = true">
          <i class="ri-file-list-3-line" aria-hidden="true"></i>
          مشاهده معاینه ابتدایی
          <i class="ri-arrow-left-line" aria-hidden="true"></i>
        </button>
        <p class="summary__document-status">{{ !chart.preExam ? 'معاینه ابتدایی هنوز ثبت نشده است.' : chart.preExam.completedAt ? 'معاینه ابتدایی تکمیل شده است.' : 'ثبت شده؛ هنوز تکمیل نشده است.' }}</p>
      </div>

      <div v-if="auth.can('preexam.view')" class="summary__block">
        <h3 class="summary__title">
          بینایی‌سنجی: نسخه نهایی
          <RouterLink v-if="refraction" :to="`/refraction/${visitId}`" class="summary__link">جزئیات</RouterLink>
        </h3>
        <p v-if="!finalLines" class="summary__empty">ثبت نشده است.</p>
        <table v-else class="xr-clinical-table summary__table">
          <thead><tr><th scope="col">چشم</th><th scope="col">SPH</th><th scope="col">CYL</th><th scope="col">AXIS</th><th scope="col">ADD</th><th scope="col">VA</th></tr></thead>
          <tbody>
            <tr v-for="eye in finalEyes" :key="eye.code">
              <th scope="row"><span class="xr-eye" :class="eye.tone">{{ eye.code }}</span></th>
              <td class="ltr-num">{{ eye.line?.sphere ?? '—' }}</td>
              <td class="ltr-num">{{ eye.line?.cylinder ?? '—' }}</td>
              <td class="ltr-num">{{ eye.line?.axis ?? '—' }}</td>
              <td class="ltr-num">{{ eye.line?.add ?? '—' }}</td>
              <td class="ltr-num">{{ eye.line?.va ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="summary__block">
        <h3 class="summary__title">شکایت و سوابق</h3>
        <dl class="summary__facts">
          <dt>شکایت اصلی</dt>
          <dd>
            {{ chart.preExam?.chiefComplaint ?? '—' }}
            <span v-if="preExtras.eye" class="xr-eye" :class="preExtras.eye === 'OS' ? 'xr-eye--os' : 'xr-eye--od'">{{ preExtras.eye }}</span>
          </dd>
          <dt>سوابق و هشدارها</dt>
          <dd :class="{ summary__alert: chart.preExam?.history }">{{ chart.preExam?.history ?? '—' }}</dd>
          <template v-if="preExamWarnings.length">
            <dt>هشدارهای ثبت‌شده</dt>
            <dd class="summary__alert">{{ preExamWarnings.join(' · ') }}</dd>
          </template>
        </dl>
      </div>
    </section>

    <section class="xr-card">
      <div class="xr-card__header flex-wrap">
        <nav class="xr-tabs" role="tablist" aria-label="بخش‌های پرونده">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
        </nav>
        <RouterLink v-if="auth.can('consultation.print')" :to="`/print/dossier/${visitId}`" target="_blank" class="btn btn-soft">چاپ دوسیه</RouterLink>
      </div>

      <form v-if="tab === 'exam' || tab === 'plan'" @submit.prevent="saveExam">
        <fieldset :disabled="!canEdit || finalized" class="xr-card__body">
          <template v-if="tab === 'exam'">
            <div class="mb-3">
              <label class="form-label" for="c-history">شرح حال</label>
              <textarea id="c-history" v-model="exam.history" class="form-control" rows="2" maxlength="1500"></textarea>
            </div>

            <details v-for="section in examSections" :key="section.key" class="xr-section" :open="section.open" @toggle="section.key === 'posterior' && (posteriorOpen = ($event.target as HTMLDetailsElement).open)">
              <summary>
                {{ section.title }}
                <span class="xr-section__hint">{{ filledCount(section.rows) ? `${filledCount(section.rows)} مورد ثبت‌شده` : 'ثبت نشده' }}</span>
              </summary>
              <!-- A closed section is not built until opened, so the page is ready for typing sooner. -->
              <div v-if="section.open" class="xr-section__body xr-table-wrap">
                <table class="xr-clinical-table exam-grid">
                  <thead>
                    <tr>
                      <th scope="col">قسمت چشم</th>
                      <th scope="col"><span class="xr-eye xr-eye--od">OD</span> چشم راست</th>
                      <th scope="col"><span class="xr-eye xr-eye--os">OS</span> چشم چپ</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="row in section.rows" :key="row.code">
                      <th scope="row" class="fw-normal">{{ row.label }} <span class="exam-grid__code">{{ row.code }}</span></th>
                      <td><input v-model="exam.od.values[row.code]" class="form-control" dir="ltr" maxlength="200" autocomplete="off" :list="`exam-${row.code}`" :aria-label="`${row.label} OD`" /></td>
                      <td><input v-model="exam.os.values[row.code]" class="form-control" dir="ltr" maxlength="200" autocomplete="off" :list="`exam-${row.code}`" :aria-label="`${row.label} OS`" /></td>
                    </tr>
                  </tbody>
                </table>
                <datalist v-for="row in section.rows" :id="`exam-${row.code}`" :key="row.code">
                  <option v-for="option in row.options" :key="option" :value="option"></option>
                </datalist>
              </div>
            </details>

            <div class="row g-3 mt-1">
              <div class="col-md-6">
                <label class="form-label" for="c-od"><span class="xr-eye xr-eye--od">OD</span> یادداشت یافته‌های چشم راست</label>
                <textarea id="c-od" v-model="exam.od.note" class="form-control" rows="2" maxlength="1000"></textarea>
              </div>
              <div class="col-md-6">
                <label class="form-label" for="c-os"><span class="xr-eye xr-eye--os">OS</span> یادداشت یافته‌های چشم چپ</label>
                <textarea id="c-os" v-model="exam.os.note" class="form-control" rows="2" maxlength="1000"></textarea>
              </div>
            </div>
          </template>

          <template v-else>
            <div class="mb-3">
              <div class="form-label">تشخیص</div>
              <p v-if="exam.diagnoses.length === 0" class="form-hint">تشخیصی ثبت نشده است.</p>
              <div v-for="(d, i) in exam.diagnoses" :key="i" class="row g-2 mb-2">
                <div class="col-sm-3">
                  <select v-model="d.eye" class="form-select" :aria-label="`چشم تشخیص ${i + 1}`">
                    <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
                  </select>
                </div>
                <div class="col"><input v-model="d.text" class="form-control" maxlength="500" :aria-label="`متن تشخیص ${i + 1}`" /></div>
                <div class="col-auto"><button type="button" class="btn btn-soft-danger btn-icon xr-control-icon" aria-label="حذف تشخیص" title="حذف تشخیص" @click="exam.diagnoses.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></div>
              </div>
              <div v-if="canEdit && !finalized" class="d-flex flex-wrap gap-2">
                <button type="button" class="btn btn-soft btn-sm" @click="exam.diagnoses.push({ eye: 'OD', text: '' })">تشخیص چشم راست (OD)</button>
                <button type="button" class="btn btn-soft btn-sm" @click="exam.diagnoses.push({ eye: 'OS', text: '' })">تشخیص چشم چپ (OS)</button>
                <button type="button" class="btn btn-soft btn-sm" @click="exam.diagnoses.push({ eye: 'None', text: '' })">تشخیص عمومی</button>
              </div>
            </div>
            <div class="mb-3">
              <label class="form-label" for="c-advice">توصیه (Advice)</label>
              <textarea id="c-advice" v-model="exam.advice" class="form-control" rows="2" maxlength="800"></textarea>
            </div>
            <div class="mb-3">
              <label class="form-label" for="c-plan">پلان درمان</label>
              <textarea id="c-plan" v-model="exam.treatmentPlan" class="form-control" rows="4" maxlength="2000"></textarea>
            </div>
            <div v-if="canEdit">
              <label class="form-label" for="c-note">یادداشت محرمانه داکتر</label>
              <textarea id="c-note" v-model="exam.confidentialNote" class="form-control" rows="2" maxlength="4000"></textarea>
            </div>
          </template>
        </fieldset>

        <div v-if="canEdit && !finalized" class="xr-action-bar">
          <template v-if="auth.can('consultation.approve') && (version !== null || dirty)">
            <button type="button" class="btn btn-primary" :disabled="saving" @click="finalize(true)">نهایی‌سازی و تکمیل مراجعه</button>
            <button type="button" class="btn btn-soft" :disabled="saving" @click="finalize(false)">نهایی‌سازی (مراجعه باز بماند)</button>
          </template>
          <button type="submit" class="btn btn-soft" :disabled="saving">ذخیره</button>
          <span v-if="dirty" class="xr-action-bar__status">تغییرات ذخیره نشده</span>
        </div>
      </form>

      <div v-if="(tab === 'exam' || tab === 'plan') && (finalized && canEdit || chart.amendments.length)" class="xr-card__body border-top">
        <form v-if="finalized && canEdit && visitOpen" class="row g-3 align-items-end mb-4" @submit.prevent="submitReopen">
          <div class="col-md-9">
            <label class="form-label" for="r-reason">دلیل ادامه معاینه</label>
            <input id="r-reason" v-model="reopen.reason" class="form-control" minlength="3" maxlength="500" required />
          </div>
          <div class="col-md-3"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="reopen.saving">ادامه معاینه</button></div>
        </form>
        <template v-if="finalized && canEdit">
          <h3 class="h6 mb-3">ثبت اصلاحیه</h3>
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
            <textarea id="a-value" v-model="amend.value" class="form-control mb-3" rows="4" maxlength="4000"></textarea>
            <button type="submit" class="btn btn-primary" :disabled="amend.saving">ثبت اصلاحیه</button>
          </form>
        </template>

        <template v-if="chart.amendments.length">
          <h3 class="h6 mt-4 mb-3">تاریخچه اصلاحیه‌ها</h3>
          <div class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>فیلد</th><th>قبلی</th><th>جدید</th><th>دلیل</th><th>کاربر</th><th>زمان</th></tr></thead>
              <tbody>
                <tr v-for="(a, i) in chart.amendments" :key="i">
                  <td>{{ a.field === 'FinalizedAt' ? 'ادامه معاینه' : historyFields[a.field] ?? a.field }}</td>
                  <td class="pre-line">{{ a.oldValue ?? '—' }}</td>
                  <td class="pre-line">{{ a.newValue ?? '—' }}</td>
                  <td>{{ a.reason }}</td>
                  <td>{{ a.byUser ?? '—' }}</td>
                  <td>{{ formatDateTime(a.createdAt) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </div>

      <div v-if="tab === 'rx'" class="xr-card__body">
        <div v-for="p in chart.prescriptions" :key="p.id" class="issued">
          <div class="issued__head">
            <strong class="ltr-num">{{ p.rxNumber }}</strong>
            <span class="xr-badge xr-badge--primary">{{ rxStatusLabels[p.status] }}</span>
            <RouterLink v-if="auth.can('prescriptions.print')" :to="`/print/rx/${p.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ</RouterLink>
            <button v-if="p.status === 'Issued' && auth.can('prescriptions.edit')" type="button" class="btn btn-soft btn-sm" @click="editRx(p)">ویرایش</button>
            <button v-if="p.status === 'Issued' && auth.can('prescriptions.cancel')" type="button" class="btn btn-soft-danger btn-sm" @click="cancelRx(p)">لغو نسخه</button>
          </div>
          <ul class="mb-0">
            <li v-for="item in p.items" :key="item.id">{{ item.drugName }} — {{ item.dose ?? '' }} {{ item.route ?? '' }} {{ item.duration ?? '' }} {{ item.instructions ?? '' }} (تعداد: {{ item.quantity }})</li>
          </ul>
        </div>
        <p v-if="chart.prescriptions.length === 0" class="xr-state">نسخه‌ای ثبت نشده است.</p>
        <template v-if="auth.can('prescriptions.create') || rx.editing">
          <hr />
          <h3 class="h6 mb-3">{{ rx.editing ? `ویرایش نسخه ${rx.editing.rxNumber}` : 'نسخه جدید' }}</h3>
          <form @submit.prevent="submitRx">
            <div class="xr-table-wrap mb-2">
              <table class="xr-clinical-table">
                <thead><tr><th class="rx-drug">نام دوا <span class="text-danger">*</span></th><th>مقدار</th><th>روش مصرف</th><th>مدت</th><th>توضیح</th><th>تعداد <span class="text-danger">*</span></th><th></th></tr></thead>
                <tbody>
                  <tr v-for="(item, i) in rx.items" :key="i">
                    <td><UiAutocomplete v-model="item.drugName" :label="`نام دوا ${i + 1}`" :search="searchDrugs" :maxlength="200" required placeholder="حرف اول نام دوا را بنویسید" /></td>
                    <td><input v-model="item.dose" class="form-control" :aria-label="`مقدار ${i + 1}`" maxlength="100" /></td>
                    <td><input v-model="item.route" class="form-control" :aria-label="`روش مصرف ${i + 1}`" maxlength="100" /></td>
                    <td><input v-model="item.duration" class="form-control" :aria-label="`مدت ${i + 1}`" maxlength="100" /></td>
                    <td><input v-model="item.instructions" class="form-control" :aria-label="`توضیح ${i + 1}`" maxlength="300" /></td>
                    <td><input v-model.number="item.quantity" class="form-control" type="number" min="0.001" step="any" :aria-label="`تعداد ${i + 1}`" required /></td>
                    <td><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف دوا ${i + 1}`" title="حذف دوا" :disabled="rx.items.length === 1" @click="rx.items.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></td>
                  </tr>
                </tbody>
              </table>
            </div>
            <button type="button" class="btn btn-soft btn-sm mb-3" @click="rx.items.push({ drugName: '', dose: '', route: '', duration: '', instructions: '', quantity: 1 })">افزودن دوا</button>
            <label class="form-label d-block" for="rx-notes">توضیحات نسخه</label>
            <input id="rx-notes" v-model="rx.notes" class="form-control mb-3" maxlength="1000" />
            <template v-if="rx.editing && needsAmendReason">
              <label class="form-label d-block" for="rx-reason">دلیل اصلاح</label>
              <input id="rx-reason" v-model="rx.reason" class="form-control mb-3" minlength="3" maxlength="500" required />
            </template>
            <button type="submit" class="btn btn-primary" :disabled="rx.saving">{{ rx.editing ? 'ذخیره ویرایش' : 'ثبت نسخه' }}</button>
            <button v-if="rx.editing" type="button" class="btn btn-soft-danger ms-2" @click="clearRx">انصراف</button>
          </form>
        </template>
      </div>

      <div v-if="tab === 'optical'" class="xr-card__body">
        <div v-if="chart.opticalPrescriptions.length" class="xr-table-wrap mb-3">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>چشم</th><th>SPH</th><th>CYL</th><th>AXIS</th><th>ADD</th><th>PD</th><th></th></tr></thead>
            <tbody>
              <template v-for="o in chart.opticalPrescriptions" :key="o.id">
                <tr>
                  <td><span class="ltr-num">{{ o.rxNumber }}</span></td>
                  <td><span class="xr-eye xr-eye--od">OD</span></td>
                  <td><span class="ltr-num">{{ o.sphereOd ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.cylinderOd ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.axisOd ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.addOd ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.pd ?? '—' }}</span></td>
                  <td class="text-nowrap">
                    <RouterLink v-if="auth.can('opticalrx.print')" :to="`/print/optical/${o.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ</RouterLink>
                    <button v-if="!o.isCancelled && auth.can('opticalrx.edit')" type="button" class="btn btn-soft btn-sm ms-2" @click="editOptical(o)">ویرایش</button>
                    <button v-if="!o.isCancelled && auth.can('opticalrx.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelOptical(o)">لغو</button>
                  </td>
                </tr>
                <tr>
                  <td></td>
                  <td><span class="xr-eye xr-eye--os">OS</span></td>
                  <td><span class="ltr-num">{{ o.sphereOs ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.cylinderOs ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.axisOs ?? '—' }}</span></td>
                  <td><span class="ltr-num">{{ o.addOs ?? '—' }}</span></td>
                  <td></td>
                  <td></td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>
        <p v-else class="xr-state">نسخه عینک ثبت نشده است.</p>
        <form v-if="auth.can('opticalrx.create') || optical.editing" @submit.prevent="submitOptical">
          <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-2">
            <h3 class="h6 mb-0">{{ optical.editing ? `ویرایش نسخه ${optical.editing.rxNumber}` : 'نسخه عینک جدید' }}</h3>
            <button v-if="finalLines" type="button" class="btn btn-soft btn-sm" @click="copyFinalToOptical">انتقال از نسخه نهایی بینایی‌سنجی</button>
          </div>
          <RxGrid :od="optical.od" :os="optical.os" label="نسخه عینک" show-add show-va />
          <div class="row g-3 mt-1 mb-3">
            <div class="col-md-2 col-6">
              <label class="form-label" for="op-pd">PD</label>
              <input id="op-pd" v-model="optical.pd" class="form-control" dir="ltr" maxlength="10" autocomplete="off" />
            </div>
            <div class="col-md-2 col-6">
              <label class="form-label" for="op-uva-od"><span class="xr-eye xr-eye--od">OD</span> دید بدون عینک</label>
              <input id="op-uva-od" v-model="optical.uvaOd" class="form-control" dir="ltr" maxlength="30" autocomplete="off" />
            </div>
            <div class="col-md-2 col-6">
              <label class="form-label" for="op-uva-os"><span class="xr-eye xr-eye--os">OS</span> دید بدون عینک</label>
              <input id="op-uva-os" v-model="optical.uvaOs" class="form-control" dir="ltr" maxlength="30" autocomplete="off" />
            </div>
            <div class="col-md-6">
              <label class="form-label" for="op-notes">توضیحات</label>
              <input id="op-notes" v-model="optical.notes" class="form-control" maxlength="700" />
            </div>
            <div class="col-12"><LensPicker v-model="optical.lens" /></div>
            <div v-if="optical.editing && needsAmendReason" class="col-12">
              <label class="form-label" for="op-reason">دلیل اصلاح</label>
              <input id="op-reason" v-model="optical.reason" class="form-control" minlength="3" maxlength="500" required />
            </div>
          </div>
          <button type="submit" class="btn btn-primary" :disabled="optical.saving">{{ optical.editing ? 'ذخیره ویرایش' : 'ثبت نسخه عینک' }}</button>
          <button v-if="optical.editing" type="button" class="btn btn-soft-danger ms-2" @click="clearOptical">انصراف</button>
        </form>
      </div>

      <div v-if="tab === 'lab'" class="xr-card__body">
        <div v-if="chart.diagnosticOrders.length" class="xr-table-wrap mb-3">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>درخواست</th><th>وضعیت</th><th>پرداخت</th><th>نتیجه</th><th>زمان نتیجه</th><th></th></tr></thead>
            <tbody>
              <tr v-for="o in chart.diagnosticOrders" :key="o.id">
                <td><span class="ltr-num">{{ o.orderNumber }}</span></td>
                <td>{{ o.testName }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[o.status]}`">{{ diagnosticStatusLabels[o.status] }}</span></td>
                <td>{{ o.paid === null ? '—' : o.paid ? 'پرداخت‌شده' : 'در انتظار پرداخت' }}</td>
                <td>{{ o.resultText ?? '—' }}</td>
                <td>{{ formatDateTime(o.performedAt) }}</td>
                <td class="text-nowrap">
                  <button type="button" class="btn btn-soft btn-sm" @click="attachmentsFor = attachmentsFor === o.id ? null : o.id">فایل‌ها</button>
                  <button v-if="['Requested', 'InProgress'].includes(o.status) && auth.can('diagnostics.create')" type="button" class="btn btn-soft btn-sm ms-2" @click="Object.assign(noteEdit, { order: o, note: o.note ?? '', saving: false, error: '' })">ویرایش توضیح</button>
                  <button v-if="['Requested', 'InProgress'].includes(o.status) && auth.can('diagnostics.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelOrder(o)">لغو</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="xr-state">درخواستی ثبت نشده است.</p>
        <AttachmentPanel v-if="attachmentsFor" :key="attachmentsFor" class="mb-3" owner-type="DiagnosticOrder" :owner-id="attachmentsFor" :can-upload="false" />
        <form v-if="auth.can('diagnostics.create')" class="row g-3 align-items-end" @submit.prevent="submitOrder">
          <div class="col-md-5">
            <UiComboBox v-model="order.testId" label="درخواست" :options="tests" :loading="testsLoading" required chip />
          </div>
          <div class="col-md-5">
            <label class="form-label" for="o-note">توضیح</label>
            <input id="o-note" v-model="order.note" class="form-control" maxlength="500" />
          </div>
          <div class="col-md-2"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="order.saving">درخواست</button></div>
          <div v-if="orderKind === 'Imaging'" class="col-12">
            <div class="form-check">
              <input id="o-return" v-model="order.returnToDoctor" class="form-check-input" type="checkbox" />
              <label class="form-check-label" for="o-return">بعد از عکس‌برداری مریض به صف داکتر برگردد</label>
            </div>
          </div>
        </form>
      </div>

      <div v-if="tab === 'surgery'" class="xr-card__body">
        <div v-if="surgeries.length" class="xr-table-wrap mb-3">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>نوع عملیات</th><th>چشم</th><th>وضعیت</th></tr></thead>
            <tbody>
              <tr v-for="s in surgeries" :key="s.id">
                <td>
                  <RouterLink v-if="auth.can('surgery.edit')" :to="`/surgery/${s.id}`"><span class="ltr-num">{{ s.caseNumber }}</span></RouterLink>
                  <span v-else class="ltr-num">{{ s.caseNumber }}</span>
                </td>
                <td>{{ s.surgeryType }}</td>
                <td>{{ eyeLabels[s.eye] }}</td>
                <td><span class="xr-badge xr-badge--primary">{{ surgeryStatusLabels[s.status] }}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="xr-state">پرونده عملیاتی ثبت نشده است.</p>
        <form v-if="auth.can('surgery.create')" class="row g-3 align-items-end" @submit.prevent="submitSurgery">
          <div v-if="packages.length" class="col-12">
            <UiComboBox v-model="surgery.packageId" label="پکیج عملیات" :options="packageOptions" chip placeholder="بدون پکیج — نوع و خدمت را خودتان وارد کنید" />
            <p class="form-hint mb-0">با انتخاب پکیج، نوع عملیات و فیس آن خودکار پر می‌شود و مواد مصرفی آن پس از عملیات از گدام کسر می‌شود.</p>
          </div>
          <div class="col-md-4">
            <label class="form-label" for="s-type">نوع عملیات</label>
            <input id="s-type" v-model="surgery.surgeryType" class="form-control" maxlength="200" required />
          </div>
          <div class="col-md-3">
            <UiComboBox v-model="surgery.serviceId" label="خدمت قابل پرداخت" :options="services" required :disabled="surgery.packageId !== null" />
          </div>
          <div class="col-md-2">
            <label class="form-label" for="s-eye">چشم</label>
            <select id="s-eye" v-model="surgery.eye" class="form-select">
              <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
            </select>
          </div>
          <div class="col-md-3"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="surgery.saving">ارجاع به عملیات</button></div>
        </form>
      </div>

      <div v-if="tab === 'followup'" class="xr-card__body">
        <div v-if="chart.followUps.length" class="xr-table-wrap mb-3">
          <table class="xr-table">
            <thead><tr><th>تاریخ پیگیری</th><th>یادداشت</th><th v-if="canEdit">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="f in chart.followUps" :key="f.id">
                <td>{{ formatDate(f.dueDate) }}</td>
                <td>{{ f.note ?? '—' }}</td>
                <td v-if="canEdit" class="text-nowrap">
                  <button type="button" class="btn btn-soft btn-sm" @click="editFollowUp(f)">ویرایش</button>
                  <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="removeFollowUp(f)">حذف</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="xr-state">پیگیری ثبت نشده است.</p>
        <form v-if="canEdit" class="row g-3 align-items-end" @submit.prevent="submitFollowUp">
          <div class="col-md-6"><ShamsiDateInput v-model="followUp.dueDate" label="تاریخ پیگیری" required /></div>
          <div class="col-md-4">
            <label class="form-label" for="f-note">یادداشت</label>
            <input id="f-note" v-model="followUp.note" class="form-control" maxlength="300" />
          </div>
          <div class="col-md-2"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="followUp.saving">{{ followUp.editing ? 'ذخیره ویرایش' : 'ثبت پیگیری' }}</button></div>
          <div v-if="followUp.editing" class="col-12"><button type="button" class="btn btn-soft-danger btn-sm" @click="clearFollowUp">انصراف از ویرایش</button></div>
        </form>
      </div>

      <div v-if="tab === 'history'" class="xr-card__body">
        <p v-if="history.length === 0" class="xr-state">سابقه‌ای پیدا نشد.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>تاریخ</th><th>بخش</th><th>داکتر</th><th>تشخیص</th><th>پلان</th><th></th></tr></thead>
            <tbody>
              <tr v-for="h in history" :key="h.visitId">
                <td>{{ formatDate(h.visitDate) }}</td>
                <td>{{ h.departmentName }}</td>
                <td>{{ h.doctorName ?? '—' }}</td>
                <td>{{ h.diagnoses.map((d) => d.text).join('، ') || '—' }}</td>
                <td>{{ parsePlan(h.treatmentPlan).plan || '—' }}</td>
                <td><RouterLink :to="`/chart/${h.visitId}`" class="btn btn-soft btn-sm text-nowrap">باز کردن</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>
  </div>

  <UiModal v-if="preExamOpen && currentChartVisit && chart?.preExam" title="معاینه ابتدایی مریض" size="lg" @close="preExamOpen = false">
    <div class="summary__document-close"><button type="button" class="btn btn-soft btn-sm" data-modal-cancel>بستن سند</button></div>
    <PreExamDocument :pre-exam="chart.preExam" :visit="chart.visit" />
  </UiModal>

  <UiModal v-if="noteEdit.order" :title="`توضیح ${noteEdit.order.testName}`" @close="noteEdit.order = null">
    <form @submit.prevent="saveOrderNote">
      <p class="form-hint">خود آزمایش تغییر نمی‌کند، چون به همان نام هزینه و ارجاع شده است. اگر اشتباه است، درخواست را لغو و دوباره ثبت کنید.</p>
      <label class="form-label" for="note-edit">توضیح</label>
      <input id="note-edit" v-model="noteEdit.note" class="form-control mb-3" maxlength="500" />
      <div v-if="noteEdit.error" class="alert alert-danger" role="alert">{{ noteEdit.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="noteEdit.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
/* Drug names carry a strength and form, and must stay readable in full once chosen. */
.rx-drug {
  min-width: 280px;
}

.chart-placeholder {
  display: grid;
  gap: 14px;
  padding: 22px 20px;
}

.chart-placeholder--summary {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.summary {
  display: grid;
  grid-template-columns: minmax(0, 0.8fr) minmax(0, 1.2fr) minmax(0, 1fr);
  gap: 16px 32px;
  padding: 16px 20px;
}

.summary__title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 6px;
  color: var(--xr-primary);
  font-size: var(--xr-fs-body);
  font-weight: 500;
}

.summary__link {
  font-size: 12.25px;
  font-weight: 400;
}

.summary__empty {
  margin: 0;
  color: var(--xr-text-muted);
}

.summary__document-hint,
.summary__document-status { color: #59666c; font-size: 12px; line-height: 1.8; }
.summary__document-hint { margin: 0 0 12px; }
.summary__document-status { margin: 8px 0 0; }
.summary__document-button { display: inline-flex; align-items: center; gap: 9px; max-width: 100%; white-space: normal; text-align: start; }
.summary__document-close { display: flex; justify-content: flex-end; margin-bottom: 12px; }

.summary__table th,
.summary__table td {
  padding: 3px 6px;
}

.summary__facts {
  margin: 0;
}

.summary__facts dt {
  color: var(--xr-text-muted);
  font-size: 12.25px;
  font-weight: 400;
}

.summary__facts dd {
  margin-bottom: 4px;
  color: var(--xr-text);
  overflow-wrap: anywhere;
}

.summary__alert {
  color: var(--xr-red) !important;
}

.exam-grid th:first-child {
  width: 190px;
}

.exam-grid__code {
  color: var(--xr-text-muted);
  font-size: 12.25px;
}

.issued {
  margin-bottom: 16px;
  color: var(--xr-text);
}

.issued__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.pre-line {
  white-space: pre-line;
}

/* Keeps an inline action level with the 45px fields beside it. */
.xr-control-button,
.xr-control-icon {
  height: var(--xr-control-height);
}

.xr-control-icon {
  width: var(--xr-control-height);
}

@media (max-width: 1199.98px) {
  .summary {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
