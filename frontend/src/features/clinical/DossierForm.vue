<script lang="ts">
import type { EyeFindings } from './examRecord'

/** What the doctor writes on the dossier. The page that owns it saves it; this form only shows and edits it. */
export interface DossierExam {
  od: EyeFindings
  os: EyeFindings
  history: string
  treatmentPlan: string
  advice: string
  /** The "Note" box at the foot of the front. */
  note: string
  confidentialNote: string
  dxOd: string[]
  dxOs: string[]
  dxOther: { eye: string; text: string }[]
}

/** The prescription block of the dossier: its lines and the two things said of the whole prescription. */
export interface DossierRx {
  items: RxLine[]
  notes: string
  validUntil: string | null
}

export interface RxLine {
  drugName: string
  dose: string
  route: string
  duration: string
  instructions: string
  quantity: number
}

</script>

<script setup lang="ts">
import { computed } from 'vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { formatDate, todayIso } from '@/utils/date'
import DrugPicker from './DrugPicker.vue'
import { patientName, type Patient } from '@/features/patients/types'
import { visitStatusLabels, visitStatusTones } from '@/features/visits/types'
import { quantity } from '@/utils/money'
import { commonDiagnoses, doseOptions, durationOptions, prescriptionFormOf, type DrugOption } from './doctorShortcuts'
import {
  anteriorRows,
  dilationDrops,
  fundusMethods,
  fundusOptions,
  parseMeasurements,
  parseRefractionExtras,
  posteriorRows,
  splitChoices,
  systemicConditions,
} from './examRecord'
import type { Chart, Refraction, RefractionLine } from './types'

/**
 * The hospital's paper file on screen, block for block as printed: the patient's details at its head,
 * history and vision, slit lamp, funduscopy with its drawings, refraction, investigations and a note on
 * the front; diagnosis, plan and drugs on the back. Every examination box opens the paper's own
 * list of choices. What triage and the optometrist recorded is shown where the paper has it, and stays theirs.
 */
const props = defineProps<{
  chart: Chart
  refraction: Refraction | null
  exam: DossierExam
  rx: DossierRx
  examEditable: boolean
  rxEditable: boolean
  /** The patient's own record, for the head of the file. Null while loading or without permission to read it. */
  patient: Patient | null
  /** What the patient is allergic to; null when no allergy was recorded. */
  allergy: string | null
  /** Form and generic of each drug by name, from the pharmacy list. A drug missing here has none recorded. */
  drugInfo: Record<string, DrugOption>
}>()

const emit = defineEmits<{
  pickDrug: [drug: DrugOption]
}>()

const pre = computed(() => props.chart.preExam)
const extras = computed(() => parseMeasurements(pre.value?.measurements))
const investigations = computed(() => parseRefractionExtras(props.refraction?.measurements))

const visit = computed(() => props.chart.visit)
const gender = computed(() => props.patient?.gender ?? visit.value.patientGender)
/** The reception's reason, shown only when triage did not already write the same complaint. */
const visitReason = computed(() => {
  const reason = visit.value.reason?.trim()
  return reason && reason !== pre.value?.chiefComplaint?.trim() ? reason : ''
})

/** The ten rows of the paper's Ant. Seg table, with its short names. The vitreous belongs to the funduscopy. */
const short: Record<string, string> = { Conjunctiva: 'Conj', AC: 'Ac', EOM: 'E.O.M' }
const slitRows = anteriorRows
  .filter((row) => row.code !== 'Episclera' && row.code !== 'Vitreous')
  .map((row) => ({ ...row, short: short[row.code] ?? row.code }))

/** Rows of the funduscopy table beside the drawings. Rows without a list are typed. */
const vitreousRow = anteriorRows.find((row) => row.code === 'Vitreous')!
const fundusRows = [
  { ...vitreousRow, short: 'Vit' },
  ...posteriorRows.filter((row) => !['Fundus', 'Dilatation', 'DilateTime', 'Method'].includes(row.code)).map((row) => ({ ...row, short: row.code })),
]

/** A value written before the lists existed stays selectable, so opening a record never changes it. */
const choices = (options: string[], current: string | undefined) => (current && !options.includes(current) ? [current, ...options] : options)

const eyes = computed(() => [
  { code: 'OD' as const, findings: props.exam.od },
  { code: 'OS' as const, findings: props.exam.os },
])

/**
 * Fills every empty slit lamp box with its normal finding, which is the first choice of each list.
 * Boxes already chosen are left alone, so it never overwrites an abnormal finding. Gonioscopy is not a
 * routine test, so it is never filled for the doctor.
 */
function fillNormal() {
  for (const eye of eyes.value) {
    for (const row of slitRows) {
      if (row.code !== 'Gonioscopy' && !eye.findings.values[row.code] && row.options.length) eye.findings.values[row.code] = row.options[0]
    }
  }
}

/**
 * The paper's DILATATION line. Each dilated eye keeps the drops used as its value ("Yes" when none was
 * ticked yet), so the eyes, the drops and the time all live in the existing findings.
 */
const isDilated = (code: 'OD' | 'OS') => !!(code === 'OD' ? props.exam.od : props.exam.os).values.Dilatation
const dropsUsed = computed(() => dilationDrops.filter((d) => eyes.value.some((eye) => splitChoices(eye.findings.values.Dilatation).includes(d))))
const dropValue = (drops: string[]) => drops.join(' ') || 'Yes'

function setDilated(codes: ('OD' | 'OS')[], on: boolean) {
  for (const eye of eyes.value.filter((e) => codes.includes(e.code))) eye.findings.values.Dilatation = on ? dropValue(dropsUsed.value) : ''
}

function toggleDrop(drop: string) {
  const drops = dropsUsed.value.includes(drop) ? dropsUsed.value.filter((d) => d !== drop) : dilationDrops.filter((d) => d === drop || dropsUsed.value.includes(d))
  // A drop ticked before any eye means both eyes, the usual case.
  const targets = eyes.value.filter((eye) => eye.findings.values.Dilatation)
  for (const eye of targets.length ? targets : eyes.value) eye.findings.values.Dilatation = dropValue(drops)
}

const dilateTime = computed({
  get: () => props.exam.od.values.DilateTime || props.exam.os.values.DilateTime || '',
  set: (value: string) => {
    props.exam.od.values.DilateTime = value
    props.exam.os.values.DilateTime = value
  },
})

/** "Indirect: 90 D / 78 D / 20 D - 360° - Direct": the methods used, written the same on both eyes. */
const methodsUsed = computed(() => [...new Set(eyes.value.flatMap((eye) => splitChoices(eye.findings.values.Method)))])
const methodChoices = computed(() => [...fundusMethods.filter((m) => m !== 'Indirect' || methodsUsed.value.includes(m)), ...methodsUsed.value.filter((m) => !fundusMethods.includes(m))])

function toggleMethod(method: string) {
  const used = methodsUsed.value.includes(method) ? methodsUsed.value.filter((m) => m !== method) : [...methodsUsed.value, method]
  const value = methodChoices.value.filter((m) => used.includes(m)).join(' / ')
  for (const eye of eyes.value) eye.findings.values.Method = value
}

/** Writes the right eye's findings into the left. Asks first when that would replace something already chosen. */
function copyOdToOs() {
  const od = props.exam.od.values
  const os = props.exam.os.values
  const codes = [...slitRows, ...fundusRows, ...posteriorRows].map((row) => row.code).filter((code) => od[code]?.trim())
  const replaced = codes.filter((code) => os[code]?.trim() && os[code] !== od[code])
  if (replaced.length && !window.confirm(`${replaced.length} یافته چشم چپ (OS) تغییر می‌کند. ادامه داده شود؟`)) return
  for (const code of codes) os[code] = od[code]
}

/** Free text kept with one eye, mostly from records written before the boxes existed. Shown in the Note box only when there is some. */
const notedEyes = computed(() => eyes.value.filter((eye) => eye.findings.note.trim()))
/** The prescription's note and validity are rarely written; they stay folded until one is. */
const rxExtrasSet = computed(() => !!(props.rx.notes.trim() || props.rx.validUntil))

// The paper's Rx has drug, dose and quantity only. Duration and instructions show only for an older
// prescription that has them, so nothing written there is hidden.
const rxHasDuration = computed(() => props.rx.items.some((i) => i.duration.trim()))
const rxHasInstructions = computed(() => props.rx.items.some((i) => i.instructions.trim()))

const triageNotes = computed(() => [extras.value.measurements, pre.value?.notes].map((t) => t?.trim()).filter(Boolean) as string[])
const refractionNotes = computed(() => [investigations.value.measurements, props.refraction?.notes].map((t) => t?.trim()).filter(Boolean) as string[])


function addOuDiagnosis() {
  props.exam.dxOther.push({ eye: 'Both', text: '' })
}

const stageLine = (stage: string, eye: 'OD' | 'OS') => props.refraction?.lines.find((l) => l.stage === stage && l.eye === eye) ?? null
const rxText = (line: RefractionLine | null) =>
  line ? [line.sphere, line.cylinder, line.axis ? `× ${line.axis}` : null, line.add ? `Add ${line.add}` : null, line.va ? `(${line.va})` : null].filter(Boolean).join(' ') : ''

const sides = computed(() => [
  { code: 'OD' as const, lines: props.exam.dxOd },
  { code: 'OS' as const, lines: props.exam.dxOs },
])
</script>

<template>
  <div class="dossier">
    <!-- Front of the file. -->
    <article class="sheet" dir="ltr" aria-label="روی دوسیه">
      <!-- The head of the paper: who the patient is and which visit this is. -->
      <header class="id" aria-label="مشخصات مریض">
        <div class="box id__left">
          <div>
            <b>S/No:</b>
            <RouterLink v-if="patient" :to="`/patients/${visit.patientId}`" class="ltr-num">{{ visit.patientFileNumber }}</RouterLink>
            <span v-else class="ltr-num">{{ visit.patientFileNumber }}</span>
          </div>
          <div><b>Date:</b> <span dir="rtl">{{ formatDate(visit.visitDate) }}</span></div>
          <div><b>Mob:</b> <span class="ltr-num">{{ patient?.phone }}</span></div>
        </div>
        <div class="box id__mid">
          <div class="id__line"><b>Name:</b> <strong class="id__name" dir="auto">{{ patient ? patientName(patient) : visit.patientName }}</strong></div>
          <div class="id__line"><b>F/Name:</b> <span dir="auto">{{ patient?.fatherName }}</span></div>
          <div class="id__line id__row">
            <span><b>Sex:</b> M <i class="tick" :class="{ 'tick--on': gender === 'Male' }"></i> F <i class="tick" :class="{ 'tick--on': gender === 'Female' }"></i></span>
            <span><b>Age:</b> <template v-if="patient?.ageYears != null">{{ quantity(patient.ageYears) }} <span dir="rtl">ساله</span></template></span>
            <span class="id__addr"><b>Add:</b> <span dir="auto">{{ patient?.address }}</span></span>
          </div>
          <div class="id__line"><b>Occupation:</b> <span dir="auto">{{ patient?.occupation }}</span></div>
        </div>
        <div class="box id__visit">
          <div><b>Visit:</b> <span class="ltr-num">{{ visit.visitNumber }}</span></div>
          <span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span>
        </div>
      </header>

      <section class="pair">
        <div class="box hx">
          <b>Hx:</b>
          <p v-if="visitReason" class="given" dir="auto"><b dir="rtl">علت مراجعه:</b> {{ visitReason }}</p>
          <p v-if="pre?.chiefComplaint" class="given" dir="auto">{{ pre.chiefComplaint }}<template v-if="extras.duration"> — {{ extras.duration }}</template><template v-if="extras.eye"> ({{ extras.eye }})</template></p>
          <p v-if="pre?.history" class="given" dir="auto">{{ pre.history }}</p>
          <p v-for="(text, i) in triageNotes" :key="`triage-${i}`" class="given given--note" dir="auto"><b dir="rtl">یادداشت تریاژ:</b> {{ text }}</p>
          <textarea v-model="exam.history" class="cell cell--area" dir="auto" rows="2" maxlength="1200" :disabled="!examEditable" aria-label="شرح حال داکتر"></textarea>
        </div>
        <table class="grid">
          <thead><tr><th>OD</th><th class="dark">Vision</th><th>OS</th></tr></thead>
          <tbody>
            <tr><td>{{ pre?.visualAcuityOd }}</td><th class="dark">VA</th><td>{{ pre?.visualAcuityOs }}</td></tr>
            <tr><td>{{ extras.pinholeOd }}</td><th class="dark">PH</th><td>{{ extras.pinholeOs }}</td></tr>
            <tr><td>{{ extras.glassesOd }}</td><th class="dark">GL</th><td>{{ extras.glassesOs }}</td></tr>
            <tr><td>{{ extras.nearOd }}</td><th class="dark">NV</th><td>{{ extras.nearOs }}</td></tr>
          </tbody>
        </table>
      </section>

      <section class="pair">
        <div class="box sys">
          <div v-for="c in systemicConditions" :key="c.code">
            <b>{{ c.label }}:</b>
            Y <i class="tick" :class="{ 'tick--on': extras.systemic[c.code].answer === 'Y' }"></i>
            N <i class="tick" :class="{ 'tick--on': extras.systemic[c.code].answer === 'N' }"></i>
            <small>{{ extras.systemic[c.code].years || '....' }} Years using Rx</small>
          </div>
          <div class="sys__wide" :class="{ 'sys__allergy': extras.allergy === 'Y' }" :role="extras.allergy === 'Y' ? 'alert' : undefined">
            <b>Allergies:</b>
            Y <i class="tick" :class="{ 'tick--on': extras.allergy === 'Y' }"></i>
            N <i class="tick" :class="{ 'tick--on': extras.allergy === 'N' }"></i>
            <b class="sys__to">Allergic to:</b> <span dir="auto" class="alert-text">{{ extras.allergicTo }}</span>
          </div>
        </div>
        <table class="grid">
          <tbody>
            <tr><th>BP=</th><td colspan="2">{{ extras.bp }} <small>mmHg</small></td></tr>
            <tr><td>{{ extras.syringingOd }}</td><th>Syringing</th><td>{{ extras.syringingOs }}</td></tr>
            <tr><td>{{ pre?.iopOd }}</td><th>IOP</th><td>{{ pre?.iopOs }}</td></tr>
          </tbody>
        </table>
      </section>

      <section class="box slit">
        <div class="slit__side">
          <h2 class="display">Slit Lamp Examination</h2>
          <!-- The front view of both eyes, as drawn on the paper. -->
          <svg viewBox="0 0 300 96" class="slit__eyes" aria-hidden="true">
            <g fill="none" stroke="currentColor" stroke-width="1.2">
              <path d="M18 34 Q72 8 132 36 Q72 24 18 34Z" />
              <path d="M168 36 Q228 8 282 34 Q228 24 168 36Z" />
              <path d="M16 62 Q74 26 134 62 Q74 94 16 62Z" />
              <circle cx="75" cy="60" r="15" /><circle cx="75" cy="60" r="6" />
              <path d="M166 62 Q226 26 284 62 Q226 94 166 62Z" />
              <circle cx="225" cy="60" r="15" /><circle cx="225" cy="60" r="6" />
            </g>
          </svg>
          <div v-if="examEditable" class="quick" dir="rtl">
            <button type="button" class="quick__btn" title="فقط جعبه‌های خالی Slit Lamp هر دو چشم با یافته نارمل پر می‌شوند" @click="fillNormal">
              <i class="ri-checkbox-multiple-line" aria-hidden="true"></i> بقیه نارمل
            </button>
            <button type="button" class="quick__btn" title="یافته‌های چشم راست به چشم چپ کاپی می‌شوند" @click="copyOdToOs">
              <i class="ri-file-copy-line" aria-hidden="true"></i> کاپی OD به OS
            </button>
          </div>
        </div>
        <table class="grid grid--entry">
          <thead><tr><th class="dark">Ant. Seg</th><th class="dark">OD</th><th class="dark">OS</th></tr></thead>
          <tbody>
            <tr v-for="row in slitRows" :key="row.code">
              <th class="dark italic" :title="row.label">{{ row.short }}</th>
              <td v-for="eye in eyes" :key="eye.code">
                <select v-model="eye.findings.values[row.code]" class="cell cell--pick" :disabled="!examEditable" :aria-label="`${row.label} ${eye.code}`">
                  <option value=""></option>
                  <option v-for="option in choices(row.options, eye.findings.values[row.code])" :key="option" :value="option">{{ option }}</option>
                </select>
              </td>
            </tr>
          </tbody>
        </table>
      </section>

      <!-- The paper's DILATATION line: which eye, which drops, and when. -->
      <section class="dil" aria-label="گشادسازی مردمک">
        <b>DILATATION:</b>
        <span class="dil__group">
          <label v-for="code in (['OD', 'OS'] as const)" :key="code" class="dil__tick">
            <input type="checkbox" :checked="isDilated(code)" :disabled="!examEditable" :aria-label="`گشادسازی ${code}`" @change="setDilated([code], ($event.target as HTMLInputElement).checked)" /> {{ code }}
          </label>
          <label class="dil__tick">
            <input type="checkbox" :checked="isDilated('OD') && isDilated('OS')" :disabled="!examEditable" aria-label="گشادسازی OU" @change="setDilated(['OD', 'OS'], ($event.target as HTMLInputElement).checked)" /> OU
          </label>
        </span>
        <span class="dil__group">
          Drop(s):
          <label v-for="drop in dilationDrops" :key="drop" class="dil__tick">
            <input type="checkbox" :checked="dropsUsed.includes(drop)" :disabled="!examEditable" :aria-label="`قطره ${drop}`" @change="toggleDrop(drop)" /> {{ drop }}
          </label>
        </span>
        <label class="dil__time">
          Dilate Time:
          <input v-model="dilateTime" class="cell cell--line" dir="ltr" maxlength="20" autocomplete="off" :disabled="!examEditable" aria-label="زمان گشادسازی" />
        </label>
      </section>

      <section class="box side fundus" aria-label="فوندوسکوپی">
        <div class="side__title"><span>Funduscopy</span></div>
        <div class="side__body">
          <div class="fundus__method">
            <b>Indirect:</b>
            <button
              v-for="method in methodChoices"
              :key="method"
              type="button"
              class="chip"
              :class="{ 'chip--on': methodsUsed.includes(method) }"
              :aria-pressed="methodsUsed.includes(method)"
              :disabled="!examEditable"
              @click="toggleMethod(method)"
            >{{ method }}</button>
          </div>
          <div class="fundus__grid">
            <!-- Each fundus drawing is a button: a click opens the fundus choices, and the choice is written under it. -->
            <label v-for="eye in eyes" :key="eye.code" class="eye" :class="[`eye--${eye.code.toLowerCase()}`, { 'eye--set': eye.findings.values.Fundus, 'eye--locked': !examEditable }]">
              <svg viewBox="0 0 230 110" aria-hidden="true">
                <g fill="none" stroke="currentColor" stroke-width="1.2" :transform="eye.code === 'OS' ? 'translate(230 0) scale(-1 1)' : undefined">
                  <circle cx="55" cy="55" r="50" />
                  <ellipse cx="74" cy="55" rx="6" ry="8" />
                  <path d="M74 47 Q62 38 48 45 M74 63 Q62 72 48 65" />
                  <path d="M107 55H119 M113 49V61" />
                  <path d="M134 18 Q170 30 182 40 M134 92 Q170 80 182 70 M228 18 Q200 28 194 40 M228 92 Q200 82 194 70" />
                  <ellipse cx="188" cy="55" rx="13" ry="17" />
                </g>
              </svg>
              <span class="eye__code">{{ eye.code }}</span>
              <span class="eye__value">{{ eye.findings.values.Fundus || (examEditable ? 'برای انتخاب کلیک کنید' : '') }}</span>
              <select v-model="eye.findings.values.Fundus" class="eye__select" :disabled="!examEditable" :aria-label="`فوندوس ${eye.code}`">
                <option value=""></option>
                <option v-for="option in choices(fundusOptions, eye.findings.values.Fundus)" :key="option" :value="option">{{ option }}</option>
              </select>
            </label>
            <table class="grid grid--entry fundus__table">
              <thead><tr><th class="dark"></th><th class="dark">OD</th><th class="dark">OS</th></tr></thead>
              <tbody>
                <tr v-for="row in fundusRows" :key="row.code">
                  <th class="dark italic" :title="row.label">{{ row.short }}</th>
                  <td v-for="eye in eyes" :key="eye.code">
                    <select v-if="row.options.length" v-model="eye.findings.values[row.code]" class="cell cell--pick" :disabled="!examEditable" :aria-label="`${row.label} ${eye.code}`">
                      <option value=""></option>
                      <option v-for="option in choices(row.options, eye.findings.values[row.code])" :key="option" :value="option">{{ option }}</option>
                    </select>
                    <input v-else v-model="eye.findings.values[row.code]" class="cell" dir="auto" maxlength="100" autocomplete="off" :disabled="!examEditable" :aria-label="`${row.label} ${eye.code}`" />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </section>

      <section class="box side">
        <div class="side__title"><span>Refraction</span></div>
        <div v-if="refraction" class="side__body refr">
          <div>
            <div><b>present GL:</b> OD: {{ rxText(stageLine('Current', 'OD')) }} &nbsp; OS: {{ rxText(stageLine('Current', 'OS')) }}</div>
            <div><b>Retinoscopy:</b> OD: {{ investigations.od.retinoscopy }} &nbsp; OS: {{ investigations.os.retinoscopy }}</div>
            <div class="refr__final"><b>Final:</b> OD: {{ rxText(stageLine('Final', 'OD')) }} &nbsp; OS: {{ rxText(stageLine('Final', 'OS')) }}</div>
          </div>
          <div class="refr__tags">
            <div class="tagbox"><b class="tag">AR</b><div>OD: {{ rxText(stageLine('Auto', 'OD')) }}<br />OS: {{ rxText(stageLine('Auto', 'OS')) }}</div></div>
            <div class="tagbox"><b class="tag">ST</b><div>OD: {{ rxText(stageLine('Subjective', 'OD')) }}<br />OS: {{ rxText(stageLine('Subjective', 'OS')) }}</div></div>
            <div class="tagbox"><b class="tag">NV</b><div>OD: {{ refraction.nearVaOd }} &nbsp; OS: {{ refraction.nearVaOs }}<br />IPD= {{ refraction.pd }} <small>mm</small></div></div>
          </div>
        </div>
        <p v-else class="side__body given" dir="rtl">بینایی‌سنجی برای این مراجعه ثبت نشده است.</p>
      </section>
      <p v-for="(text, i) in refractionNotes" :key="`refr-${i}`" class="given given--note" dir="auto"><b dir="rtl">یادداشت بینایی‌سنج:</b> {{ text }}</p>

      <!-- Investigations as the paper lays them out; the optometrist records them, the doctor reads them here. -->
      <section class="meas" aria-label="آزمایش‌های بینایی‌سنجی">
        <div class="box meas__top">
          <span><b>Color vision:</b> OD: {{ investigations.od.colorVision }}</span>
          <span>OS: {{ investigations.os.colorVision }}</span>
          <span><b>RBS:</b> {{ investigations.rbs }} <small>mg/dl</small></span>
          <span><b>JCC:</b> {{ investigations.jcc }}</span>
        </div>
        <div class="meas__bottom">
          <div class="box kbox">
            <b class="tag">K</b>
            <div>
              <div><b>OD:</b> K1 (H): {{ investigations.od.k1 }} &nbsp; K2 (V): {{ investigations.od.k2 }}</div>
              <div><b>OS:</b> K1 (H): {{ investigations.os.k1 }} &nbsp; K2 (V): {{ investigations.os.k2 }}</div>
            </div>
          </div>
          <div class="box kbox">
            <b class="tag">AL</b>
            <div><div><b>OD:</b> {{ investigations.od.al }}</div><div><b>OS:</b> {{ investigations.os.al }}</div></div>
          </div>
          <div class="box kbox">
            <b class="tag">IOL</b>
            <div><div><b>OD:</b> {{ investigations.od.iol }}</div><div><b>OS:</b> {{ investigations.os.iol }}</div></div>
          </div>
        </div>
      </section>

      <section class="box note">
        <label class="back-label" for="dossier-note">Note:</label>
        <textarea id="dossier-note" v-model="exam.note" class="cell cell--area" dir="auto" rows="2" maxlength="700" :disabled="!examEditable" aria-label="یادداشت"></textarea>
        <!-- Free text kept with one eye, mostly from older records; editable where it was written. -->
        <label v-for="eye in notedEyes" :key="eye.code" class="note__eye">
          <b>{{ eye.code }}:</b>
          <textarea v-model="eye.findings.note" class="cell cell--area" dir="auto" rows="1" maxlength="1500" :disabled="!examEditable" :aria-label="`یادداشت معاینه ${eye.code}`"></textarea>
        </label>
      </section>
    </article>

    <!-- Back of the file. -->
    <article class="sheet" dir="ltr" aria-label="پشت دوسیه">

      <section class="box dx">
        <div v-for="side in sides" :key="side.code">
          <div><b v-if="side.code === 'OD'" class="back-label">Diagnosis:</b> <b>{{ side.code }}</b></div>
          <ol>
            <li v-for="(_, i) in side.lines" :key="i">
              <input v-model="side.lines[i]" class="cell cell--line" dir="auto" list="dossier-dx" maxlength="500" autocomplete="off" :disabled="!examEditable" :aria-label="`تشخیص ${side.code} ${i + 1}`" />
            </li>
          </ol>
        </div>
        <datalist id="dossier-dx"><option v-for="d in commonDiagnoses" :key="d" :value="d"></option></datalist>
        <!-- A diagnosis of both eyes is written once. Older records may also hold one for neither eye. -->
        <div v-for="(d, i) in exam.dxOther" :key="`other-${i}`" class="dx__other">
          <b>{{ d.eye === 'Both' ? 'OU' : 'General' }}:</b>
          <input v-model="d.text" class="cell cell--line" dir="auto" list="dossier-dx" maxlength="500" autocomplete="off" :disabled="!examEditable" :aria-label="`تشخیص ${d.eye === 'Both' ? 'هر دو چشم' : 'عمومی'} ${i + 1}`" />
          <button v-if="examEditable" type="button" class="remove" title="حذف این تشخیص" :aria-label="`حذف تشخیص ${i + 1}`" @click="exam.dxOther.splice(i, 1)">×</button>
        </div>
        <div v-if="examEditable" class="dx__other" dir="rtl">
          <button type="button" class="link" @click="addOuDiagnosis">+ تشخیص هر دو چشم (OU)</button>
        </div>
      </section>

      <!-- Plan and advice side by side: two short boxes instead of two full-width ones. -->
      <section class="plan">
        <div class="box">
          <label class="back-label" for="dossier-plan">Plan:</label>
          <textarea id="dossier-plan" v-model="exam.treatmentPlan" class="cell cell--area" dir="auto" rows="2" maxlength="1200" :disabled="!examEditable" aria-label="پلان درمان"></textarea>
        </div>
        <div class="box">
          <label class="back-label" for="dossier-advice">Advice:</label>
          <textarea id="dossier-advice" v-model="exam.advice" class="cell cell--area" dir="auto" rows="2" maxlength="800" :disabled="!examEditable" aria-label="Advice"></textarea>
        </div>
      </section>

      <section class="box rx">
        <div class="rx__head">
          <div class="rx-mark">Rx:</div>
          <slot name="rx-meta" />
        </div>
        <p v-if="allergy != null" class="rx-allergy" dir="rtl" role="alert">
          <i class="ri-alert-fill" aria-hidden="true"></i> مریض حساسیت دارد: <bdi>{{ allergy || 'نوع آن ثبت نشده' }}</bdi>
        </p>
        <div v-if="rxEditable" dir="rtl"><DrugPicker @pick="(drug) => emit('pickDrug', drug)" /></div>
        <datalist id="dossier-doses"><option v-for="d in doseOptions" :key="d" :value="d"></option></datalist>
        <datalist id="dossier-durations"><option v-for="d in durationOptions" :key="d" :value="d"></option></datalist>
        <div v-if="rx.items.length" class="rx-scroll" dir="rtl">
        <table class="rx-table">
          <thead>
            <tr>
              <th class="rx-table__no">#</th><th>نام دوا</th><th class="rx-table__form">نوعیت</th><th class="rx-table__dose">طریقه مصرف</th>
              <th v-if="rxHasDuration" class="rx-table__dur">مدت</th><th v-if="rxHasInstructions" class="rx-table__how">دستور</th>
              <th class="rx-table__qty">تعداد</th><th class="rx-table__del"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(item, i) in rx.items" :key="i">
              <td class="rx-table__no">{{ i + 1 }}</td>
              <td>
                <input v-model="item.drugName" class="cell cell--drug" dir="auto" maxlength="200" autocomplete="off" :disabled="!rxEditable" :aria-label="`نام دوا ${i + 1}`" />
                <small v-if="drugInfo[item.drugName.trim()]?.generic" class="rx-generic" dir="ltr">({{ drugInfo[item.drugName.trim()]?.generic }})</small>
              </td>
              <td class="rx-table__form">
                <span v-if="drugInfo[item.drugName.trim()]?.form" class="rx-form" dir="ltr" :title="drugInfo[item.drugName.trim()]?.form ?? undefined">
                  {{ prescriptionFormOf(drugInfo[item.drugName.trim()]?.form) }}
                </span>
                <span v-else class="text-muted">—</span>
              </td>
              <td><input v-model="item.dose" class="cell cell--mid" dir="auto" list="dossier-doses" maxlength="100" autocomplete="off" :disabled="!rxEditable" :aria-label="`طریقه مصرف ${i + 1}`" /></td>
              <td v-if="rxHasDuration"><input v-model="item.duration" class="cell cell--mid" dir="auto" list="dossier-durations" maxlength="100" autocomplete="off" :disabled="!rxEditable" :aria-label="`مدت مصرف ${i + 1}`" /></td>
              <td v-if="rxHasInstructions"><input v-model="item.instructions" class="cell" dir="auto" maxlength="300" autocomplete="off" :disabled="!rxEditable" :aria-label="`دستور مصرف ${i + 1}`" placeholder="OD / OS / OU …" /></td>
              <td><input v-model.number="item.quantity" class="cell cell--mid" type="number" min="0.001" step="any" required :disabled="!rxEditable" :aria-label="`تعداد ${i + 1}`" /></td>
              <td class="rx-table__del"><button v-if="rxEditable" type="button" class="remove" :aria-label="`حذف دوا ${i + 1}`" title="حذف دوا" @click="rx.items.splice(i, 1)">×</button></td>
            </tr>
          </tbody>
        </table>
        </div>
        <details v-if="rx.items.length && (rxEditable || rxExtrasSet)" class="rx-whole" dir="rtl" :open="rxExtrasSet">
          <summary>توضیح برای دواخانه و اعتبار نسخه</summary>
          <div class="rx-whole__body">
            <label class="rx-whole__notes">
              <span class="form-label">توضیح نسخه برای دواخانه</span>
              <input v-model="rx.notes" class="form-control" maxlength="1000" :disabled="!rxEditable" />
            </label>
            <ShamsiDateInput v-model="rx.validUntil" label="اعتبار نسخه تا (اختیاری)" :min="todayIso()" :disabled="!rxEditable" />
          </div>
        </details>
        <div dir="rtl"><slot name="rx-extra" /></div>
      </section>
    </article>
  </div>
</template>

<style scoped>
.dossier {
  display: grid;
  gap: 10px;
  overflow-x: auto;
  padding: 4px;
}

/* Each side is drawn like the printed form: black rules on white, the same blocks in the same places. */
.sheet {
  --line: 1px solid #1f2328;
  display: grid;
  gap: 6px;
  width: 100%;
  min-width: 0;
  max-width: 980px;
  margin: 0 auto;
  padding: 10px 12px;
  border: 1px solid var(--xr-border);
  border-radius: 4px;
  background: #fff;
  box-shadow: 0 2px 10px rgb(0 0 0 / 0.06);
  color: #111;
  font-family: 'Poppins', 'Vazirmatn', sans-serif;
  font-size: 13px;
  line-height: 1.4;
}

.sheet p {
  margin: 0;
}

.box {
  padding: 4px 8px;
  border: var(--line);
}

.pair {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 260px;
  gap: 8px;
}

.grid {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.grid th,
.grid td {
  height: 25px;
  padding: 0 6px;
  border: var(--line);
  font-weight: 400;
  text-align: center;
}

.grid th {
  font-weight: 600;
}

.grid--entry td {
  padding: 0;
}

.dark {
  background: #1f2328;
  color: #fff;
}

.italic {
  font-family: Georgia, 'Times New Roman', serif;
  font-style: italic;
  font-weight: 400 !important;
}

.display {
  margin: 0 0 4px;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 17px;
  font-style: italic;
  font-weight: 700;
}

.given {
  color: #3b4650;
  font-size: 12px;
}

.alert-text {
  color: var(--xr-red, #c62828);
  font-weight: 600;
}

/* A box on the paper: empty until it is clicked, written in the same ink as the rest of the file. */
.cell {
  width: 100%;
  height: 100%;
  min-height: 25px;
  padding: 1px 6px;
  border: 0;
  background: transparent;
  color: #111;
  font: inherit;
  font-weight: 500;
}

.cell:hover:not(:disabled) {
  background: #f5f6f7;
}

.cell:focus {
  outline: 2px solid color-mix(in srgb, var(--xr-primary) 45%, transparent);
  outline-offset: -2px;
  background: #f5f6f7;
}

.cell:disabled {
  color: #111;
  opacity: 1;
}

/* A choice box keeps a faint arrow, so an empty one still reads as something to pick, not a printed blank. */
.cell--pick {
  appearance: none;
  padding-inline-end: 16px;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='8' height='5'%3E%3Cpath d='M0 0l4 5 4-5z' fill='%239aa4ad'/%3E%3C/svg%3E");
  background-position: right 5px center;
  background-repeat: no-repeat;
  cursor: pointer;
  text-align: center;
}

.cell--pick:disabled {
  background-image: none;
}

.cell--pick:hover:not(:disabled),
.cell--pick:focus {
  appearance: auto;
}

.cell--pick:disabled {
  cursor: default;
}

.cell--area {
  display: block;
  resize: vertical;
}

.cell--line {
  min-height: 28px;
  border-bottom: 1px dotted #555;
}

.cell--drug {
  font-size: 15px;
  font-weight: 700;
}

/* The generic in brackets under the brand, as the old prescriptions wrote it. */
.rx-generic {
  display: block;
  padding-inline: 4px;
  color: var(--xr-text-muted);
  font-size: 12px;
  text-align: start;
}

.hx {
  min-height: 0;
}

.tick {
  display: inline-block;
  width: 11px;
  height: 11px;
  margin-inline: 2px 6px;
  border: var(--line);
  vertical-align: -1px;
}

.tick--on {
  background: #111;
}

.sys {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 2px 14px;
  align-content: center;
  font-size: 12.5px;
}

.sys small {
  font-size: 11px;
}

.sys__wide {
  grid-column: 1 / -1;
}

.sys__to {
  margin-inline-start: 14px;
}

.slit {
  display: grid;
  grid-template-columns: 260px minmax(0, 1fr);
  gap: 10px;
}

.slit .grid th:first-child {
  width: 80px;
}

.eye {
  position: relative;
  display: grid;
  justify-items: center;
  gap: 2px;
  margin: 0;
  padding: 2px 2px 4px;
  border: 1px dashed transparent;
  border-radius: 8px;
  color: #1f2328;
  cursor: pointer;
}

.eye:hover:not(.eye--locked) {
  border-color: color-mix(in srgb, var(--xr-primary) 55%, transparent);
  background: #f5faff;
}

.eye:focus-within {
  border-color: var(--xr-primary);
  background: #eef5ff;
}

.eye--locked {
  cursor: default;
}

.eye svg {
  width: 100%;
  max-height: 110px;
}

.eye__code {
  font-size: 12px;
  font-weight: 700;
}

.eye__value {
  min-height: 20px;
  color: var(--xr-text-muted);
  font-size: 12px;
  text-align: center;
}

.eye--set .eye__value {
  color: #111;
  font-size: 14px;
  font-weight: 600;
}

/* The real list sits over the whole drawing, invisible, so a click anywhere on the eye opens it. */
.eye__select {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: inherit;
}

.side {
  display: flex;
  gap: 8px;
  padding: 0;
}

.side__title {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  border-inline-end: var(--line);
}

.side__title span {
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 17px;
  font-style: italic;
  font-weight: 700;
  transform: rotate(180deg);
  writing-mode: vertical-rl;
}

.side__body {
  flex: 1;
  min-width: 0;
  padding: 4px 8px 4px 0;
}

.refr {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: 10px;
  font-size: 12.5px;
}

.refr__final {
  margin-top: 4px;
  font-size: 14px;
}

.refr__tags {
  display: grid;
  gap: 4px;
}

.tagbox {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 30px;
  border: var(--line);
  line-height: 1.25;
}

.tag {
  display: flex;
  align-items: center;
  justify-content: center;
  align-self: stretch;
  width: 34px;
  background: #1f2328;
  color: #fff;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 14px;
}

/* Head of the paper. */
.id {
  display: grid;
  grid-template-columns: 200px minmax(0, 1fr) 150px;
  gap: 6px;
}

.id .box {
  display: grid;
  align-content: center;
  gap: 2px;
}

.id__line {
  padding-bottom: 1px;
  border-bottom: 1px solid #d0d4d8;
}

.id__name {
  font-size: 15px;
}

.id__row {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 18px;
}

.id__addr {
  flex: 1;
  min-width: 120px;
}

.id__visit {
  justify-items: start;
}

.sys__allergy {
  padding: 1px 4px;
  border-radius: 4px;
  background: #fdecea;
}

.slit__eyes {
  width: 100%;
  max-height: 80px;
}

/* DILATATION line, under the slit lamp as on the paper. */
.dil {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 18px;
  padding: 2px 4px;
  font-size: 12.5px;
}

.dil__group {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 10px;
}

.dil__tick {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin: 0;
  cursor: pointer;
}

.dil__time {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 0 auto;
}

.dil__time .cell {
  width: 90px;
}

.fundus .side__body {
  display: grid;
  gap: 4px;
}

.fundus__method {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 4px;
  font-size: 13px;
}

.chip {
  padding: 1px 9px;
  border: 1px solid #9aa4ad;
  border-radius: 12px;
  background: #fff;
  color: #1f2328;
  font-size: 12px;
  font-weight: 600;
}

.chip--on {
  border-color: #1f2328;
  background: #1f2328;
  color: #fff;
}

.chip:disabled {
  cursor: default;
}

.chip:disabled:not(.chip--on) {
  opacity: 0.55;
}

/* OD drawing, the table, OS drawing: the order of the paper. */
.fundus__grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.25fr) minmax(0, 1fr);
  gap: 8px;
  align-items: start;
}

.fundus__grid .eye--os {
  order: 3;
}

.fundus__table .grid th:first-child,
.fundus__table th:first-child {
  width: 70px;
}

.meas {
  display: grid;
  gap: 6px;
  font-size: 12.5px;
}

.meas__top {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 4px 14px;
}

.meas__bottom {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr);
  gap: 6px;
}

.kbox {
  display: flex;
  align-items: stretch;
  gap: 8px;
  padding: 0;
  line-height: 1.5;
}

.kbox > div {
  padding: 2px 4px;
}

.note {
  display: grid;
  gap: 2px;
}

.note__eye {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
}

/* Back */
.back-label {
  font-size: 13.5px;
  font-weight: 700;
}

.dx {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 28px;
  padding: 6px 10px;
}

.dx ol {
  margin: 2px 0 0;
  padding-inline-start: 22px;
}

.dx li {
  padding-block: 0;
}

.plan {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 6px;
}

.dx__other {
  display: flex;
  grid-column: 1 / -1;
  align-items: center;
  gap: 8px;
}

.rx {
  display: grid;
  gap: 6px;
  padding: 6px 10px;
}

.rx__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.rx-mark {
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 22px;
  font-style: italic;
  font-weight: 700;
  line-height: 1;
}

.rx-table {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.rx-table th {
  padding: 6px 8px;
  border-bottom: var(--line);
  background: #f6f8fa;
  color: #444;
  font-size: 12px;
  font-weight: 600;
  text-align: start;
}

/* Each drug on its own clear line: room around the fields, a light rule between rows. */
.rx-table td {
  padding: 5px 4px;
  border-bottom: 1px solid #e1e4e8;
  vertical-align: middle;
}

.rx-table tbody tr:nth-child(even) {
  background: #fafbfc;
}

.rx-table tbody tr:hover {
  background: #f1f7ff;
}

.rx-table .cell {
  min-height: 32px;
  padding: 3px 8px;
  border: 1px solid #d8dee4;
  border-radius: 4px;
  background: #fff;
}

.rx-table .cell:disabled {
  border-color: transparent;
  background: transparent;
}

/* Dose, duration and count are short and often left to right; centred, they sit under their heading. */
.rx-table .cell--mid {
  text-align: center;
}

.rx-table th.rx-table__dose,
.rx-table th.rx-table__dur,
.rx-table th.rx-table__qty,
.rx-table th.rx-table__form {
  text-align: center;
}

.rx-table__no {
  width: 34px;
  text-align: center !important;
  font-weight: 600;
}

.rx-table__dose {
  width: 170px;
}

.rx-table__form {
  width: 100px;
  text-align: center;
}

.rx-table__qty {
  width: 80px;
}

.rx-table__del {
  width: 34px;
  text-align: center;
}

.remove {
  border: 0;
  background: none;
  color: var(--xr-text-muted);
  font-size: 17px;
  line-height: 1;
}

.remove:hover {
  color: var(--xr-red, #c62828);
}

.rx-form {
  color: #111;
  font-size: 12px;
}

.link {
  padding: 0 2px;
  border: 0;
  background: none;
  color: var(--xr-primary);
  font-size: 12px;
  font-weight: 400;
}

.link--danger {
  color: var(--xr-red, #c62828);
}

.given--note {
  margin-block: 2px;
}

/* The eye drawing's column: heading on top, the quick actions resting at its foot, under the drawing. */
.slit__side {
  display: flex;
  flex-direction: column;
}

.quick {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 6px;
  margin-top: auto;
  padding-top: 6px;
  border-top: 1px dashed #d0d7de;
}

.quick__btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 10px;
  border: 1px solid #d0d7de;
  border-radius: 999px;
  background: transparent;
  color: #57606a;
  font: inherit;
  font-size: 11.5px;
  line-height: 1.6;
  cursor: pointer;
  transition: color 0.15s, border-color 0.15s, background-color 0.15s;
}

.quick__btn i {
  font-size: 13px;
  opacity: 0.8;
}

.quick__btn:hover {
  border-color: #1f2328;
  background: #f6f8fa;
  color: #1f2328;
}

.quick__btn:focus-visible {
  outline: 2px solid #0969da;
  outline-offset: 1px;
}

@media print {
  .quick {
    display: none;
  }
}

.rx-whole summary {
  cursor: pointer;
  font-size: 13px;
}

.rx-allergy {
  margin: 0;
  padding: 6px 10px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--xr-red, #c62828) 12%, transparent);
  color: var(--xr-red, #c62828);
  font-weight: 700;
}

.rx-table__dur {
  width: 120px;
}

.rx-table__how {
  width: 140px;
}

.rx-whole summary {
  color: var(--xr-text-muted);
}

.rx-whole__body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 240px;
  gap: 10px;
  align-items: end;
  margin-top: 6px;
}

.rx-whole__notes {
  margin: 0;
}

/* A tablet or a narrow window: the side-by-side blocks of the paper stack, so nothing scrolls sideways. */
@media (max-width: 900px) {
  .sheet {
    padding: 8px;
  }
  .id,
  .pair,
  .slit,
  .refr,
  .dx,
  .plan,
  .meas__bottom,
  .rx-whole__body {
    grid-template-columns: minmax(0, 1fr);
  }
  .meas__top,
  .fundus__grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .fundus__table {
    grid-column: 1 / -1;
    order: 4;
  }
  .dil__time {
    margin-inline-start: 0;
  }
  .rx-scroll {
    overflow-x: auto;
  }
  .rx-table {
    min-width: 560px;
  }
}
</style>
