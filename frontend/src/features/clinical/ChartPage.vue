<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { testOptions } from '@/api/lookups'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import TransferDoctorModal from '@/features/visits/TransferDoctorModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime, todayIso } from '@/utils/date'
import { money } from '@/utils/money'
import DossierForm, { type RxLine } from './DossierForm.vue'
import { paperDose, recentDrugs, rememberDrugs } from './doctorShortcuts'
import LensPicker from './LensPicker.vue'
import PatientBanner from './PatientBanner.vue'
import RxGrid, { type RxRow } from './RxGrid.vue'
import {
  parseFindings,
  parseOpticalNotes,
  parsePlan,
  parseRefractionExtras,
  serializeFindings,
  serializeOpticalNotes,
  serializePlan,
  type EyeFindings,
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

/**
 * The page is the patient's paper file: the doctor fills it in place and saves once.
 * The few things that are not on the paper open in a window from the buttons above it.
 */
type PanelKey = 'optical' | 'lab' | 'surgery' | 'history'
const panel = ref<PanelKey | null>(null)
const panels = computed(() => [
  { key: 'optical' as const, label: 'نسخه عینک', icon: 'ri-glasses-2-line', count: chart.value?.opticalPrescriptions.length ?? 0 },
  { key: 'lab' as const, label: 'درخواست تشخیصی', icon: 'ri-microscope-line', count: chart.value?.diagnosticOrders.length ?? 0 },
  { key: 'surgery' as const, label: 'عملیات', icon: 'ri-hospital-line', count: 0 },
  { key: 'history' as const, label: 'سابقه مراجعات', icon: 'ri-history-line', count: 0 },
])

const canEdit = computed(() => auth.can('consultation.edit'))
const finalized = computed(() => !!chart.value?.consultation?.finalizedAt)
const examEditable = computed(() => canEdit.value && !finalized.value)

const blankFindings = (): EyeFindings => ({ values: {}, note: '' })

/** The paper form has three diagnosis lines per eye; they are always there to type into. */
const DX_LINES = 3
const padLines = (lines: string[]) => [...lines, ...Array<string>(Math.max(0, DX_LINES - lines.length)).fill('')]

const exam = reactive({
  od: blankFindings(),
  os: blankFindings(),
  history: '',
  treatmentPlan: '',
  advice: '',
  confidentialNote: '',
  dxOd: padLines([]),
  dxOs: padLines([]),
  /** Diagnoses for both eyes or for neither, which the paper had no line for. */
  dxOther: [] as { eye: string; text: string }[],
})

/** The prescription being written on this page: a new one, or the visit's own that the pharmacy has not started. */
const rxForm = reactive({ items: [] as RxLine[], notes: '', validUntil: null as string | null, reason: '' })

const { dirty, markSaved, applying } = useUnsavedGuard(reactive({ exam, rxForm }))
const version = ref<number | null>(null)
const saving = ref(false)


function fillChart(c: Chart) {
  const consultation = c.consultation
  const parsedPlan = parsePlan(consultation?.treatmentPlan)
  const dx = consultation?.diagnoses ?? []
  const current = editableRx(c)
  applying(() => {
    Object.assign(exam, {
      od: parseFindings(consultation?.findingsOd),
      os: parseFindings(consultation?.findingsOs),
      history: parsedPlan.history,
      treatmentPlan: parsedPlan.plan,
      advice: parsedPlan.advice,
      confidentialNote: consultation?.confidentialNote ?? '',
      dxOd: padLines(dx.filter((d) => d.eye === 'OD').map((d) => d.text)),
      dxOs: padLines(dx.filter((d) => d.eye === 'OS').map((d) => d.text)),
      dxOther: dx.filter((d) => d.eye !== 'OD' && d.eye !== 'OS').map((d) => ({ ...d })),
    })
    setRx(current)
  })
  examSaved.value = examSnapshot()
  version.value = consultation?.version ?? null
}

const diagnosesToSave = () =>
  [
    ...exam.dxOd.map((text) => ({ eye: 'OD', text })),
    ...exam.dxOs.map((text) => ({ eye: 'OS', text })),
    ...exam.dxOther,
  ].filter((d) => d.text.trim())


/** The exam is sent only when it changed: a prescription alone must not wait on the exam's own rules. */
const examSnapshot = () => JSON.stringify(exam)
const examSaved = ref('')
const examChanged = computed(() => examSnapshot() !== examSaved.value)


const finalLines = computed(() => {
  const lines = refraction.value?.lines.filter((l) => l.stage === 'Final') ?? []
  return lines.length ? lines : null
})


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
    if (!sameVisit || !dirty.value) fillChart(chart.value)
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
      diagnoses: diagnosesToSave(),
      version: version.value,
    },
  })
  version.value = saved.version
  examSaved.value = examSnapshot()
}

/** Problems the doctor can fix before anything is sent, so nothing is half saved. */
function draftProblem() {
  if (rxEditable.value && rxChanged.value && rx.editing && rxLines().length === 0) return 'نسخه بدون دوا ذخیره نمی‌شود. برای حذف کامل نسخه، دکمه «لغو نسخه» را بزنید.'
  return ''
}

/**
 * Saves everything typed on the page: the exam with its diagnoses, the prescription and the next visit.
 * Returns what was sent to the pharmacy, if anything, for the message.
 */
async function saveDraft() {
  if (examEditable.value && examChanged.value) await persistExam()
  const rxSent = rxEditable.value && rxChanged.value ? await persistRx() : false
  if (canEdit.value && followUp.dueDate) await persistFollowUp()
  markSaved()
  return rxSent
}

async function saveAll() {
  const problem = draftProblem()
  if (problem) {
    error.value = problem
    notice.value = ''
    return
  }
  saving.value = true
  error.value = ''
  try {
    const rxSent = await saveDraft()
    notice.value = rxSent ? 'ذخیره شد و نسخه دوا برای دواخانه ارسال شد.' : 'ذخیره شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    saving.value = false
  }
}

async function finalize(closeVisit: boolean) {
  const problem = draftProblem()
  if (problem) {
    error.value = problem
    notice.value = ''
    return
  }
  saving.value = true
  error.value = ''
  try {
    // Unsaved typing goes in first, so finalising can never drop what is on screen.
    await saveDraft()
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

/** The prescription on the page and the server state it started from. */
const rx = reactive({ editing: null as Prescription | null, saved: '', key: newIdempotencyKey() })

/** The visit's newest prescription that may still change: issued, and nothing of it handed out yet. */
function editableRx(c: Chart) {
  if (!auth.can('prescriptions.edit')) return null
  return [...c.prescriptions].reverse().find((p) => p.status === 'Issued' && p.items.every((i) => !i.dispensedQuantity)) ?? null
}

const rxLines = () => rxForm.items.filter((i) => i.drugName.trim())
const rxSnapshot = () => JSON.stringify({ items: rxLines(), notes: rxForm.notes.trim(), validUntil: rxForm.validUntil })
const rxChanged = computed(() => rxSnapshot() !== rx.saved)

/** Loads a prescription into the page, or an empty one when `p` is null. */
function setRx(p: Prescription | null) {
  rx.editing = p
  Object.assign(rxForm, {
    reason: '',
    notes: p?.notes ?? '',
    validUntil: p?.validUntil ?? null,
    items: p?.items.map((i) => ({ drugName: i.drugName, dose: i.dose ?? '', route: i.route ?? '', duration: i.duration ?? '', instructions: i.instructions ?? '', quantity: i.quantity })) ?? [],
  })
  rx.saved = rxSnapshot()
}

/** A new prescription can always be started; an issued one can change until the pharmacy starts on it. */
const rxEditable = computed(() => (rx.editing ? auth.can('prescriptions.edit') : auth.can('prescriptions.create')))

/** Once the exam is final or the visit closed, the server asks for a reason on any change. */
const needsAmendReason = computed(() => finalized.value || chart.value?.visit.status === 'Completed')

const recent = ref(recentDrugs(auth.user?.id))

function addDrug(name: string) {
  const index = rxForm.items.findIndex((i) => i.drugName.trim().toLowerCase() === name.toLowerCase())
  if (index < 0) {
    const known = recent.value.find((d) => d.name.toLowerCase() === name.toLowerCase())
    rxForm.items.push({ drugName: name, dose: known?.dose || paperDose(name), route: '', duration: '', instructions: '', quantity: known?.quantity || 1 })
  }
  // The cursor goes to the dose, the one thing left to write for the drug.
  const at = index < 0 ? rxForm.items.length : index + 1
  nextTick(() => document.querySelector<HTMLInputElement>(`[aria-label="دوز ${at}"]`)?.focus())
}

/** Sends the prescription; true when the pharmacy received something new. */
async function persistRx() {
  const items = rxLines().map((i) => ({ ...i, drugName: i.drugName.trim(), quantity: i.quantity || 1 }))
  try {
    if (rx.editing) {
      await api(`/prescriptions/${rx.editing.id}`, { method: 'PUT', body: { notes: rxForm.notes, items, version: rx.editing.version, reason: rxForm.reason || undefined, validUntil: rxForm.validUntil } })
    } else {
      if (items.length === 0) return false
      await api('/prescriptions', { method: 'POST', idempotencyKey: rx.key, body: { visitId: Number(props.visitId), notes: rxForm.notes, items, validUntil: rxForm.validUntil } })
      rx.key = newIdempotencyKey()
    }
  } catch (cause) {
    if (cause instanceof ApiError && cause.status !== 0 && !rx.editing) rx.key = newIdempotencyKey()
    throw cause
  }
  rx.saved = rxSnapshot()
  rememberDrugs(auth.user?.id, items)
  recent.value = recentDrugs(auth.user?.id)
  return true
}

/** Moves the page to another prescription of this visit, or to a blank new one. */
function openRx(p: Prescription | null) {
  if (rxChanged.value && !window.confirm('تغییرات نسخه فعلی ذخیره نشده‌اند. از آن صرف نظر شود؟')) return
  setRx(p)
  rx.key = newIdempotencyKey()
}

const otherPrescriptions = computed(() => chart.value?.prescriptions.filter((p) => p.id !== rx.editing?.id) ?? [])

/** The doctor answers a pharmacy's request for review, or renews a prescription whose validity ran out. */
const reapprove = reactive({ rx: null as Prescription | null, validUntil: null as string | null, note: '', saving: false, error: '' })

async function submitReapprove() {
  reapprove.saving = true
  reapprove.error = ''
  try {
    await api(`/prescriptions/${reapprove.rx!.id}/reapprove`, { method: 'POST', body: { validUntil: reapprove.validUntil, note: reapprove.note, version: reapprove.rx!.version } })
    reapprove.rx = null
    notice.value = 'نسخه دوباره تأیید شد و دواخانه می‌تواند آن را تحویل دهد.'
    await load()
  } catch (cause) {
    reapprove.error = errorMessage(cause)
  } finally {
    reapprove.saving = false
  }
}

const rxNeedsDoctor = (p: Prescription) =>
  ['Issued', 'PartiallyDispensed'].includes(p.status) && (p.pharmacyStatus === 'NeedsDoctorReview' || (!!p.validUntil && p.validUntil < todayIso()))

const blankRow = (): RxRow => ({ sphere: '', cylinder: '', axis: '', add: '', va: '' })
const optical = reactive({ editing: null as OpticalRx | null, reason: '', od: blankRow(), os: blankRow(), pd: '', uvaOd: '', uvaOs: '', lens: [] as string[], notes: '', key: newIdempotencyKey(), saving: false })
const opticalFormOpen = ref(false)

function editOptical(o: OpticalRx) {
  opticalFormOpen.value = true
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
  opticalFormOpen.value = false
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
const orderFormOpen = ref(false)
const order = reactive({ testId: null as number | null, note: '', returnToDoctor: true, key: newIdempotencyKey(), saving: false })
const orderKind = computed(() => tests.value.find((t) => t.value === order.testId)?.kind ?? 'Lab')

async function submitOrder() {
  order.saving = true
  error.value = ''
  try {
    // The patient may be away for a while, so what is on screen goes onto the record first.
    // The prescription waits for the doctor's own save: it would reach the pharmacy at once.
    if (examChanged.value && examEditable.value) {
      await persistExam()
      if (!rxChanged.value) markSaved()
    }
    const kind = orderKind.value
    await api('/diagnostic-orders', {
      method: 'POST',
      idempotencyKey: order.key,
      body: { patientId: chart.value!.visit.patientId, visitId: Number(props.visitId), testId: order.testId, note: order.note, returnToDoctor: order.returnToDoctor },
    })
    Object.assign(order, { testId: null, note: '', returnToDoctor: true, key: newIdempotencyKey() })
    orderFormOpen.value = false
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
const surgeryFormOpen = ref(false)
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
    surgeryFormOpen.value = false
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

/** Saved with the rest of the page whenever a date is filled in. */
async function persistFollowUp() {
  if (followUp.editing) {
    await api(`/follow-ups/${followUp.editing}`, { method: 'PUT', body: { dueDate: followUp.dueDate, note: followUp.note } })
  } else {
    await api('/follow-ups', { method: 'POST', body: { visitId: Number(props.visitId), dueDate: followUp.dueDate, note: followUp.note } })
  }
  clearFollowUp()
}

const history = ref<HistoryItem[]>([])

async function loadHistory() {
  try {
    history.value = await api<HistoryItem[]>(`/clinical/patients/${chart.value!.visit.patientId}/history`)
  } catch (cause) {
    explain(cause)
  }
}

watch(panel, (value) => {
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
    <section class="xr-card chart-placeholder chart-placeholder--sheet">
      <span v-for="n in 12" :key="n" class="xr-skeleton"></span>
    </section>
  </div>

  <div v-if="chart" class="chart-shell">
    <section class="chart-header" aria-label="مشخصات مریض و اقدامات پرونده">
      <PatientBanner :visit="chart.visit" />
      <nav class="chart-header__actions" aria-label="بخش‌های دیگر پرونده">
        <button v-for="item in panels" :key="item.key" type="button" class="btn btn-soft btn-sm" aria-haspopup="dialog" @click="panel = item.key">
          <i :class="item.icon" aria-hidden="true"></i> {{ item.label }}<span v-if="item.count" class="chart-header__count">{{ item.count }}</span>
        </button>
        <RouterLink v-if="auth.can('consultation.print')" :to="`/print/dossier/${visitId}`" target="_blank" class="btn btn-soft btn-sm">
          <i class="ri-printer-line" aria-hidden="true"></i> چاپ دوسیه
        </RouterLink>
        <button v-if="canEdit && visitOpen" type="button" class="btn btn-soft btn-sm" @click="transferring = true">
          <i class="ri-user-shared-line" aria-hidden="true"></i> انتقال به داکتر دیگر
        </button>
      </nav>
    </section>
    <TransferDoctorModal v-if="transferring" :visit="chart.visit" @close="transferring = false" @saved="onTransferred" />

    <!-- The patient's file itself: the doctor writes straight onto the paper form, both sides, and saves once. -->
    <form class="chart-dossier" aria-label="دوسیه مریض" @submit.prevent="saveAll">
      <DossierForm
        :chart="chart"
        :refraction="refraction"
        :exam="exam"
        :rx-lines="rxForm.items"
        :follow-up="followUp"
        :exam-editable="examEditable"
        :rx-editable="rxEditable"
        :follow-up-editable="canEdit"
        @pick-drug="addDrug"
        @edit-follow-up="editFollowUp"
        @remove-follow-up="removeFollowUp"
        @clear-follow-up="clearFollowUp"
      >
        <template #rx-meta>
          <span v-if="rx.editing" class="rx-meta" dir="rtl">
            <strong class="ltr-num">{{ rx.editing.rxNumber }}</strong>
            <span class="xr-badge xr-badge--primary">{{ rxStatusLabels[rx.editing.status] }}</span>
            <span v-if="rxNeedsDoctor(rx.editing)" class="xr-badge xr-badge--orange">نیازمند تأیید دوباره داکتر</span>
            <button v-if="rxNeedsDoctor(rx.editing) && auth.can('prescriptions.edit')" type="button" class="btn btn-soft btn-sm" @click="Object.assign(reapprove, { rx: rx.editing, validUntil: null, note: '', error: '' })">تأیید دوباره</button>
            <button v-if="auth.can('prescriptions.cancel')" type="button" class="btn btn-soft-danger btn-sm" @click="cancelRx(rx.editing)">لغو نسخه</button>
          </span>
        </template>
        <template #rx-extra>
          <div v-if="rx.editing && needsAmendReason && rxChanged" class="mt-2">
            <label class="form-label" for="rx-reason">دلیل اصلاح نسخه</label>
            <input id="rx-reason" v-model="rxForm.reason" class="form-control" minlength="3" maxlength="500" required />
          </div>
          <div v-if="otherPrescriptions.length" class="rx-others">
            <div v-for="p in otherPrescriptions" :key="p.id" class="issued">
              <div class="issued__head">
                <strong class="ltr-num">{{ p.rxNumber }}</strong>
                <span class="xr-badge xr-badge--primary">{{ rxStatusLabels[p.status] }}</span>
                <span v-if="p.validUntil" class="small">معتبر تا {{ formatDate(p.validUntil) }}</span>
                <span v-if="rxNeedsDoctor(p)" class="xr-badge xr-badge--orange">نیازمند تأیید دوباره داکتر</span>
                <button v-if="rxNeedsDoctor(p) && auth.can('prescriptions.edit')" type="button" class="btn btn-soft btn-sm" @click="Object.assign(reapprove, { rx: p, validUntil: null, note: '', error: '' })">تأیید دوباره</button>
                <RouterLink v-if="auth.can('prescriptions.print')" :to="`/print/rx/${p.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ</RouterLink>
                <button v-if="p.status === 'Issued' && p.items.every((i) => !i.dispensedQuantity) && auth.can('prescriptions.edit')" type="button" class="btn btn-soft btn-sm" @click="openRx(p)">ویرایش</button>
                <button v-if="p.status === 'Issued' && auth.can('prescriptions.cancel')" type="button" class="btn btn-soft-danger btn-sm" @click="cancelRx(p)">لغو نسخه</button>
              </div>
              <ul class="mb-0">
                <li v-for="item in p.items" :key="item.id">{{ item.drugName }} — {{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join(' ') }} (تعداد: {{ item.quantity }})</li>
              </ul>
            </div>
          </div>
        </template>
      </DossierForm>


      <div v-if="canEdit || rxEditable" class="xr-action-bar chart-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving"><i class="ri-save-line" aria-hidden="true"></i> ذخیره</button>
        <template v-if="examEditable && auth.can('consultation.approve') && (version !== null || dirty)">
          <button type="button" class="btn btn-soft" :disabled="saving" @click="finalize(true)">نهایی‌سازی و تکمیل مراجعه</button>
          <button type="button" class="btn btn-soft" :disabled="saving" @click="finalize(false)">نهایی‌سازی (مراجعه باز بماند)</button>
        </template>
        <span v-if="finalized" class="xr-badge xr-badge--primary">معاینه نهایی شده است</span>
        <span v-if="dirty || rxChanged" class="xr-action-bar__status">تغییرات ذخیره نشده</span>
      </div>
    </form>

    <section v-if="finalized && canEdit || chart.amendments.length" class="xr-card xr-card__body" aria-label="ادامه معاینه و اصلاحیه‌ها">
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
    </section>

      <UiModal v-if="panel === 'optical'" title="نسخه عینک" size="lg" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <header class="module-panel__heading"><div><h3>نسخه عینک</h3><p>نتیجه نهایی OD/OS و انتخاب لنز</p></div><span class="module-panel__count">{{ chart.opticalPrescriptions.length }} نسخه</span></header>
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
        <button v-if="auth.can('opticalrx.create') && !opticalFormOpen && !optical.editing" type="button" class="btn btn-primary module-panel__create" @click="opticalFormOpen = true"><i class="ri-add-line" aria-hidden="true"></i> نسخه عینک جدید</button>
        <form v-if="opticalFormOpen || optical.editing" @submit.prevent="submitOptical">
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
          <button v-else type="button" class="btn btn-soft ms-2" @click="opticalFormOpen = false">بستن فرم</button>
        </form>
      </div></UiModal>

      <UiModal v-if="panel === 'lab'" title="درخواست تشخیصی" size="lg" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <header class="module-panel__heading"><div><h3>درخواست‌های تشخیصی</h3><p>رفرکشن، عکس‌برداری و آزمایش</p></div><span class="module-panel__count">{{ chart.diagnosticOrders.length }} درخواست</span></header>
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
        <button v-if="auth.can('diagnostics.create') && !orderFormOpen" type="button" class="btn btn-primary module-panel__create" @click="orderFormOpen = true"><i class="ri-add-line" aria-hidden="true"></i> درخواست تشخیصی جدید</button>
        <form v-if="orderFormOpen" class="row g-3 align-items-end" @submit.prevent="submitOrder">
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
          <div class="col-12"><button type="button" class="btn btn-soft btn-sm" @click="orderFormOpen = false">بستن فرم</button></div>
        </form>
      </div></UiModal>

      <UiModal v-if="panel === 'surgery'" title="ارجاع به عملیات" size="lg" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <header class="module-panel__heading"><div><h3>ارجاع به عملیات</h3><p>پرونده‌های فعلی، خدمت و پکیج عملیات</p></div><span class="module-panel__count">{{ surgeries.length }} پرونده</span></header>
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
        <button v-if="auth.can('surgery.create') && !surgeryFormOpen" type="button" class="btn btn-primary module-panel__create" @click="surgeryFormOpen = true"><i class="ri-add-line" aria-hidden="true"></i> ارجاع جدید به عملیات</button>
        <form v-if="surgeryFormOpen" class="row g-3 align-items-end" @submit.prevent="submitSurgery">
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
          <div class="col-12"><button type="button" class="btn btn-soft btn-sm" @click="surgeryFormOpen = false">بستن فرم</button></div>
        </form>
      </div></UiModal>

      <UiModal v-if="panel === 'history'" title="سابقه مراجعات" size="lg" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <header class="module-panel__heading"><div><h3>سابقه بالینی</h3><p>مراجعات، تشخیص‌ها و پلان‌های گذشته</p></div></header>
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
      </div></UiModal>
  </div>


  <UiModal v-if="reapprove.rx" :title="`تأیید دوباره نسخه ${reapprove.rx.rxNumber}`" @close="reapprove.rx = null">
    <form @submit.prevent="submitReapprove">
      <div class="mb-3"><ShamsiDateInput v-model="reapprove.validUntil" label="اعتبار تازه تا (اختیاری)" :min="todayIso()" /></div>
      <label class="form-label" for="reapprove-note">توضیح داکتر</label>
      <input id="reapprove-note" v-model="reapprove.note" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="reapprove.error" class="alert alert-danger" role="alert">{{ reapprove.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="reapprove.saving">تأیید</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
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
.chart-shell {
  display: grid;
  gap: 12px;
  min-width: 0;
}

.chart-header {
  display: grid;
  gap: 8px;
  min-width: 0;
}

.chart-header :deep(.banner),
.chart-header :deep(.banner__facts) {
  min-width: 0;
}

/* Everything that is not written on the paper sits here, one click away, out of the doctor's way. */
.chart-header__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.chart-header__actions .btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.chart-header__count {
  min-width: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: var(--xr-primary);
  color: #fff;
  font-size: 10.5px;
  line-height: 18px;
  text-align: center;
}

.chart-dossier {
  display: grid;
  gap: 12px;
  min-width: 0;
}

.chart-actions {
  position: sticky;
  bottom: 0;
  max-width: 980px;
  width: 100%;
  margin: 0 auto;
  border: 1px solid var(--xr-border);
  border-radius: var(--xr-radius-card);
  box-shadow: 0 -4px 14px rgb(0 0 0 / 0.06);
}

.rx-meta {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}

.rx-others {
  margin-top: 10px;
  padding: 8px 10px;
  border: 1px dashed var(--xr-border);
  border-radius: 6px;
  font-size: 13px;
}

.module-panel__heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin: 0 0 16px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--xr-border);
}

.module-panel__heading h3 {
  margin: 0;
  color: var(--xr-text);
  font-size: 16px;
  font-weight: 650;
}

.module-panel__heading p {
  margin: 3px 0 0;
  color: var(--xr-text-muted);
  font-size: 12px;
}

.module-panel__count {
  flex: 0 0 auto;
  padding: 4px 8px;
  border-radius: 6px;
  background: var(--xr-surface-muted, #f1f5f5);
  color: var(--xr-text-muted);
  font-size: 11px;
  font-weight: 600;
}

.module-panel__create {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
}

.chart-placeholder {
  display: grid;
  gap: 14px;
  padding: 22px 20px;
}

.chart-placeholder--sheet {
  max-width: 980px;
  width: 100%;
  min-height: 60vh;
  margin: 0 auto;
}

.summary__document-close {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 12px;
}

.issued {
  margin-bottom: 10px;
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

@media (max-width: 767.98px) {
  .chart-shell { gap: 8px; }
  .chart-header :deep(.banner) {
    gap: 8px;
    padding: 10px 12px;
  }
  .chart-header :deep(.banner__name) { font-size: 16px; }
  .chart-header :deep(.banner__facts) {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 6px 12px;
  }
  .chart-header__actions {
    flex-wrap: nowrap;
    overflow-x: auto;
  }
  .chart-header__actions .btn { flex: 0 0 auto; }
  .module-panel__heading { align-items: stretch; flex-direction: column; }
}
</style>
