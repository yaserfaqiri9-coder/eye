<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api/http'
import { parseFindings, parseMeasurements, parsePlan, parseRefractionExtras, systemicConditions } from '@/features/clinical/examRecord'
import type { Chart, Refraction } from '@/features/clinical/types'
import type { Patient } from '@/features/patients/types'
import { patientName } from '@/features/patients/types'
import { printPaper, shamsiPaper, useClipper, useHospital, usePaperPage } from './paper'

const RX_LINES = 16

const props = defineProps<{ visitId: string }>()

const { hospital, load: loadHospital } = useHospital()
const { clipped, clip } = useClipper()
const overflow = ref<string[]>([])
const paperRoot = ref<HTMLElement | null>(null)
const backSheet = ref<HTMLElement | null>(null)
const contentCompact = ref(false)
const fitReady = ref(false)

const page = usePaperPage(async () => {
  const chart = await api<Chart>(`/consultations/${props.visitId}/chart`)
  const [patient, refraction] = await Promise.all([
    api<Patient>(`/patients/${chart.visit.patientId}`),
    api<Refraction | undefined>(`/refractions/${props.visitId}`).catch(() => undefined),
  ])
  return { chart, patient, refraction: refraction ?? null }
})

const chart = computed(() => page.data.value?.chart ?? null)
const patient = computed(() => page.data.value?.patient ?? null)
const refraction = computed(() => page.data.value?.refraction ?? null)

const pre = computed(() => chart.value?.preExam ?? null)
const extras = computed(() => parseMeasurements(pre.value?.measurements))
const od = computed(() => parseFindings(chart.value?.consultation?.findingsOd))
const os = computed(() => parseFindings(chart.value?.consultation?.findingsOs))
const plan = computed(() => parsePlan(chart.value?.consultation?.treatmentPlan))
const investigations = computed(() => parseRefractionExtras(refraction.value?.measurements))

const slitLamp = [
  { code: 'Lids', label: 'Lids' },
  { code: 'Conjunctiva', label: 'Conj' },
  { code: 'Cornea', label: 'Cornea' },
  { code: 'Sclera', label: 'Sclera' },
  { code: 'AC', label: 'AC' },
  { code: 'Iris', label: 'Iris' },
  { code: 'Pupil', label: 'Pupil' },
  { code: 'Lens', label: 'Lens' },
  { code: 'Gonioscopy', label: 'Gonioscopy' },
  { code: 'EOM', label: 'E.O.M' },
]

// The vitreous is read with the fundus, as on the screen dossier.
const fundusRows = [
  { code: 'Vitreous', label: 'Vit' },
  { code: 'Fundus', label: 'Fundus' },
  { code: 'Disc', label: 'Disc' },
  { code: 'CDR', label: 'CDR' },
  { code: 'Macula', label: 'Macula' },
  { code: 'Vessels', label: 'Vessels' },
  { code: 'Periphery', label: 'Periphery' },
  { code: 'Other', label: 'Other' },
]

const cell = (eye: 'od' | 'os', code: string, limit = 38) => clip(`${code} ${eye.toUpperCase()}`, (eye === 'od' ? od.value : os.value).values[code], limit)

const dilatedOd = computed(() => !!od.value.values.Dilatation)
const dilatedOs = computed(() => !!os.value.values.Dilatation)
const dropsUsed = computed(() => [od.value.values.Dilatation, os.value.values.Dilatation].filter(Boolean).join(' ').split(/\s+/))
const dilateTime = computed(() => od.value.values.DilateTime || os.value.values.DilateTime || '')
const method = computed(() => [od.value.values.Method, os.value.values.Method].filter(Boolean).join(' / '))

const stageLine = (stage: string, eye: 'OD' | 'OS') => refraction.value?.lines.find((l) => l.stage === stage && l.eye === eye) ?? null
const rxText = (line: { sphere: string | null; cylinder: string | null; axis: string | null; add?: string | null; va?: string | null } | null) => {
  if (!line) return ''
  const parts = [line.sphere, line.cylinder, line.axis ? `× ${line.axis}` : null, line.add ? `Add ${line.add}` : null, line.va ? `(${line.va})` : null]
  return parts.filter(Boolean).join(' ')
}

const diagnosesOf = (eye: 'OD' | 'OS') => (chart.value?.consultation?.diagnoses ?? []).filter((d) => d.eye === eye || d.eye === 'Both').map((d, i) => clip(`Diagnosis ${eye} ${i + 1}`, d.text, 90))
const diagnosesOd = computed(() => diagnosesOf('OD'))
const diagnosesOs = computed(() => diagnosesOf('OS'))
const diagnosesGeneral = computed(() => (chart.value?.consultation?.diagnoses ?? []).filter((d) => d.eye === 'None').map((d, i) => clip(`Diagnosis ${i + 1}`, d.text, 200)))

/** Numbered lines on the paper file: at least three per eye, as on the original, more when recorded. */
const numbered = (rows: string[]) => Array.from({ length: Math.max(3, rows.length) }, (_, i) => rows[i] ?? '')

const rxItems = computed(() => (chart.value?.prescriptions ?? []).filter((p) => p.status !== 'Cancelled').flatMap((p) => p.items))
const rxShown = computed(() => rxItems.value.slice(0, RX_LINES))
const rxHidden = computed(() => rxItems.value.length - rxShown.value.length)
// Known-dense prescriptions start compact. Shorter prescriptions are measured in their
// generous layout and compact only when the real rendered content exceeds the fixed A4 back.
const densePrescription = computed(() => rxShown.value.length >= 13 || contentCompact.value)
const optical = computed(() => (chart.value?.opticalPrescriptions ?? []).filter((o) => !o.isCancelled))
const rxNotes = computed(() => (chart.value?.prescriptions ?? []).filter((p) => p.status !== 'Cancelled' && p.notes).map((p, i) => clip(`Rx note ${i + 1}`, p.notes, 160)))

const isMale = computed(() => patient.value?.gender === 'Male')
const isFemale = computed(() => patient.value?.gender === 'Female')

const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))
let fitRun = 0
let resizeFrame = 0

/**
 * Measure the actual rendered record after fonts and images settle. The back stays hidden during
 * this one-frame decision, so switching to compact density never flashes or jumps on screen.
 */
async function settleFit() {
  const run = ++fitRun
  fitReady.value = false
  contentCompact.value = false
  overflow.value = []
  await nextTick()
  await document.fonts?.ready
  await Promise.all([...document.images].map((image) => image.decode?.().catch(() => undefined)))
  await nextFrame()
  if (run !== fitRun) return

  const back = backSheet.value
  if (back && !densePrescription.value && back.scrollHeight > back.clientHeight + 1) {
    contentCompact.value = true
    await nextTick()
    await nextFrame()
    if (run !== fitRun) return
  }

  overflow.value = [...(paperRoot.value?.querySelectorAll<HTMLElement>('.sheet') ?? [])]
    .filter((sheet) => sheet.scrollHeight > sheet.clientHeight + 1)
    .map((sheet) => sheet.dataset.side ?? '')
  fitReady.value = true
}

function scheduleFit() {
  fitReady.value = false
  cancelAnimationFrame(resizeFrame)
  resizeFrame = requestAnimationFrame(() => void settleFit())
}

onMounted(async () => {
  window.addEventListener('resize', scheduleFit, { passive: true })
  document.fonts?.addEventListener?.('loadingdone', scheduleFit)
  await Promise.all([page.load(), loadHospital()])
  await settleFit()
})

onBeforeUnmount(() => {
  ++fitRun
  cancelAnimationFrame(resizeFrame)
  window.removeEventListener('resize', scheduleFit)
  document.fonts?.removeEventListener?.('loadingdone', scheduleFit)
})
</script>

<template>
  <div ref="paperRoot" class="paper-screen">
    <div v-if="page.error.value" class="alert alert-danger" role="alert">{{ page.error.value }}</div>
    <p v-else-if="page.loading.value" class="xr-state" role="status">در حال آماده‌سازی دوسیه...</p>

    <div v-if="chart" class="paper-tools">
      <button type="button" class="btn btn-primary" :disabled="!fitReady || overflow.length > 0" @click="printPaper">چاپ پشت و رو</button>
      <span class="paper-tools__hint">در پنجره چاپ، گزینه چاپ دو طرفه را روشن کنید. یادداشت محرمانه و معلومات مالی چاپ نمی‌شوند.</span>
    </div>
    <div v-if="clipped.length" class="alert alert-warning paper-tools" role="alert">
      <strong>{{ clipped.length }} مورد در چاپ خلاصه شد (با علامت *):</strong>
      {{ clipped.map((c) => c.field).join('، ') }}.
      متن کامل در پرونده دیجیتالی باقی است.
    </div>
    <div v-if="fitReady && overflow.length" class="alert alert-danger paper-tools" role="alert">محتوای این دوسیه از یک ورق بیشتر شد و چاپ متوقف است. لطفاً به مسئول سیستم اطلاع دهید.</div>

    <template v-if="chart && patient">
      <!-- Front: identity and examinations. -->
      <article class="sheet" data-side="روی ورق" dir="ltr" aria-label="روی دوسیه">
        <header class="id-block">
          <div class="id-block__left box">
            <div class="kv"><b>S/No:</b> {{ patient.fileNumber }}</div>
            <div class="kv"><b>Date:</b> {{ shamsiPaper(chart.visit.visitDate) }}</div>
            <div class="kv"><b>Mob:</b> {{ patient.phone }}</div>
          </div>
          <div class="id-block__mid box">
            <div class="kv line"><b>Name:</b> <span dir="auto">{{ patientName(patient) }}</span></div>
            <div class="kv line"><b>F/Name:</b> <span dir="auto">{{ patient.fatherName }}</span></div>
            <div class="kv line id-block__row">
              <span><b>Sex:</b> M <i class="tick" :class="{ 'tick--on': isMale }"></i> F <i class="tick" :class="{ 'tick--on': isFemale }"></i></span>
              <span><b>Age:</b> {{ patient.ageYears }}</span>
              <span class="id-block__addr"><b>Add:</b> <span dir="auto">{{ clip('Address', patient.address, 60) }}</span></span>
            </div>
            <div class="kv"><b>Occupation:</b> <span dir="auto">{{ clip('Occupation', patient.occupation, 40) }}</span></div>
          </div>
          <div class="id-block__brand box">
            <img src="/print/alshefa-official-logo.png" alt="لوگوی شفاخانه تخصصی چشم الشفا" class="brand-logo" />
            <div class="brand-name" dir="auto">{{ hospital?.hospitalName }}</div>
          </div>
        </header>

        <section class="hx-block">
          <div class="box hx-block__text">
            <b>Hx:</b>
            <p v-if="pre?.chiefComplaint" dir="auto">{{ clip('Chief complaint', pre.chiefComplaint, 160) }}<template v-if="extras.duration"> — {{ extras.duration }}</template><template v-if="extras.eye"> ({{ extras.eye }})</template></p>
            <p v-if="pre?.history" dir="auto">{{ clip('History', pre.history, 200) }}</p>
            <p v-if="plan.history" dir="auto">{{ clip('Doctor history', plan.history, 240) }}</p>
          </div>
          <table class="grid vision">
            <thead><tr><th>OD</th><th class="dark">Vision</th><th>OS</th></tr></thead>
            <tbody>
              <tr><td>{{ pre?.visualAcuityOd }}</td><th class="dark">VA</th><td>{{ pre?.visualAcuityOs }}</td></tr>
              <tr><td>{{ extras.pinholeOd }}</td><th class="dark">PH</th><td>{{ extras.pinholeOs }}</td></tr>
              <tr><td>{{ extras.glassesOd }}</td><th class="dark">GL</th><td>{{ extras.glassesOs }}</td></tr>
              <tr><td>{{ extras.nearOd }}</td><th class="dark">NV</th><td>{{ extras.nearOs }}</td></tr>
            </tbody>
          </table>
        </section>

        <section class="sys-block">
          <div class="box sys-block__list">
            <div v-for="c in systemicConditions" :key="c.code" class="sys-item">
              <b>{{ c.label }}:</b>
              Y <i class="tick" :class="{ 'tick--on': extras.systemic[c.code].answer === 'Y' }"></i>
              N <i class="tick" :class="{ 'tick--on': extras.systemic[c.code].answer === 'N' }"></i>
              <span class="sys-item__years">{{ extras.systemic[c.code].years || '....' }} Years using Rx</span>
            </div>
            <div class="sys-item sys-item--wide">
              <b>Allergies:</b>
              Y <i class="tick" :class="{ 'tick--on': extras.allergy === 'Y' }"></i>
              N <i class="tick" :class="{ 'tick--on': extras.allergy === 'N' }"></i>
              <span class="sys-item__to"><b>Allergic to:</b> <span dir="auto">{{ clip('Allergic to', extras.allergicTo, 70) }}</span></span>
            </div>
          </div>
          <table class="grid pressure">
            <tbody>
              <tr><th>BP=</th><td colspan="2">{{ extras.bp }} <span class="unit">mmHg</span></td></tr>
              <tr><td>{{ extras.syringingOd }}</td><th>Syringing</th><td>{{ extras.syringingOs }}</td></tr>
              <tr><td>{{ pre?.iopOd }}</td><th>IOP</th><td>{{ pre?.iopOs }}</td></tr>
            </tbody>
          </table>
        </section>

        <section class="slit box">
          <div class="slit__art">
            <h2 class="display">Slit Lamp Examination</h2>
            <svg viewBox="0 0 300 96" class="slit__eyes" aria-hidden="true">
              <g fill="none" stroke="#000" stroke-width="1">
                <path d="M18 34 Q72 8 132 36 Q72 24 18 34Z" />
                <path d="M168 36 Q228 8 282 34 Q228 24 168 36Z" />
                <path d="M16 62 Q74 26 134 62 Q74 94 16 62Z" />
                <circle cx="75" cy="60" r="15" /><circle cx="75" cy="60" r="6" />
                <path d="M166 62 Q226 26 284 62 Q226 94 166 62Z" />
                <circle cx="225" cy="60" r="15" /><circle cx="225" cy="60" r="6" />
              </g>
            </svg>
          </div>
          <table class="grid slit__table">
            <thead><tr><th class="dark">Ant. Seg</th><th class="dark">OD</th><th class="dark">OS</th></tr></thead>
            <tbody>
              <tr v-for="row in slitLamp" :key="row.code">
                <th class="dark italic">{{ row.label }}</th>
                <td>{{ cell('od', row.code) }}</td>
                <td>{{ cell('os', row.code) }}</td>
              </tr>
            </tbody>
          </table>
        </section>

        <section class="fundus box">
          <div class="side-title"><span>Funduscopy</span></div>
          <div class="fundus__body">
            <div class="fundus__line">
              <b>DILATATION</b>
              <span>OD <i class="tick" :class="{ 'tick--on': dilatedOd }"></i></span>
              <span>OS <i class="tick" :class="{ 'tick--on': dilatedOs }"></i></span>
              <span>OU <i class="tick" :class="{ 'tick--on': dilatedOd && dilatedOs }"></i></span>
              <span class="small">Drop(s):
                T <i class="tick" :class="{ 'tick--on': dropsUsed.includes('T') }"></i>
                T+Ph <i class="tick" :class="{ 'tick--on': dropsUsed.includes('T+Ph') }"></i>
                Ph <i class="tick" :class="{ 'tick--on': dropsUsed.includes('Ph') }"></i>
                C <i class="tick" :class="{ 'tick--on': dropsUsed.includes('C') }"></i>
                H <i class="tick" :class="{ 'tick--on': dropsUsed.includes('H') }"></i>
              </span>
              <span class="small">Dilate Time: {{ dilateTime }}</span>
            </div>
            <div class="fundus__method"><b>Indirect: 90 D / 78 D / 20 D - 360° - Direct:</b> {{ method }}</div>
            <div class="fundus__grid">
              <svg viewBox="0 0 110 110" class="fundus__eye" aria-hidden="true">
                <g fill="none" stroke="#000"><circle cx="55" cy="55" r="52" /><ellipse cx="72" cy="55" rx="9" ry="11" /><path d="M72 44 Q52 34 36 40 M72 66 Q52 76 36 70" /></g>
              </svg>
              <table class="grid fundus__table">
                <thead><tr><th></th><th>OD</th><th>OS</th></tr></thead>
                <tbody>
                  <tr v-for="row in fundusRows" :key="row.code"><th>{{ row.label }}</th><td>{{ cell('od', row.code, 34) }}</td><td>{{ cell('os', row.code, 34) }}</td></tr>
                </tbody>
              </table>
              <svg viewBox="0 0 110 110" class="fundus__eye" aria-hidden="true">
                <g fill="none" stroke="#000"><circle cx="55" cy="55" r="52" /><ellipse cx="38" cy="55" rx="9" ry="11" /><path d="M38 44 Q58 34 74 40 M38 66 Q58 76 74 70" /></g>
              </svg>
            </div>
          </div>
        </section>

        <section class="refr box">
          <div class="side-title"><span>Refraction</span></div>
          <div class="refr__left">
            <div class="kv"><b>Date:</b> {{ refraction ? shamsiPaper(refraction.recordedAt) : '' }}</div>
            <div class="refr__row"><b>present GL:</b> <span>OD: {{ rxText(stageLine('Current', 'OD')) }}</span> <span>OS: {{ rxText(stageLine('Current', 'OS')) }}</span></div>
            <div class="refr__row"><b>Retinoscopy:</b> <span>OD: {{ investigations.od.retinoscopy }}</span> <span>OS: {{ investigations.os.retinoscopy }}</span></div>
            <div class="refr__row"><b>Final Rx:</b> <span>OD: {{ rxText(stageLine('Final', 'OD')) }}</span> <span>OS: {{ rxText(stageLine('Final', 'OS')) }}</span></div>
            <div class="refr__cross">
              <svg v-for="eye in ['OD', 'OS']" :key="eye" viewBox="0 0 120 56" aria-hidden="true">
                <g stroke="#000" fill="none"><path d="M60 8V48 M26 28H94" /></g>
                <g font-size="8" text-anchor="middle" fill="#000"><text x="60" y="7">90</text><text x="60" y="55">270</text><text x="16" y="31">180</text><text x="104" y="31">0</text></g>
              </svg>
            </div>
          </div>
          <div class="refr__right">
            <div class="tagbox"><b class="tag">AR</b><div><div>OD: {{ rxText(stageLine('Auto', 'OD')) }}</div><div>OS: {{ rxText(stageLine('Auto', 'OS')) }}</div></div></div>
            <div class="tagbox"><b class="tag">ST</b><div><div>OD: {{ rxText(stageLine('Subjective', 'OD')) }}</div><div>OS: {{ rxText(stageLine('Subjective', 'OS')) }}</div></div></div>
            <div class="tagbox"><b class="tag">NV</b><div><div>OD: {{ refraction?.nearVaOd }}</div><div>OS: {{ refraction?.nearVaOs }}</div><div>IPD= {{ refraction?.pd }} <span class="unit">mm</span></div></div></div>
          </div>
        </section>

        <section class="meas">
          <div class="meas__top box">
            <span><b>Color vision:</b> OD: {{ investigations.od.colorVision }}</span>
            <span>OS: {{ investigations.os.colorVision }}</span>
            <span><b>RBS:</b> {{ investigations.rbs }} <span class="unit">mg/dl</span></span>
            <span><b>JCC:</b> {{ investigations.jcc }}</span>
          </div>
          <div class="meas__bottom">
            <table class="grid">
              <tbody>
                <tr><th class="dark" rowspan="2">K</th><th>OD</th><td><b>K1 (H):</b> {{ investigations.od.k1 }}</td><td><b>K2 (V):</b> {{ investigations.od.k2 }}</td></tr>
                <tr><th>OS</th><td><b>K1 (H):</b> {{ investigations.os.k1 }}</td><td><b>K2 (V):</b> {{ investigations.os.k2 }}</td></tr>
              </tbody>
            </table>
            <table class="grid">
              <tbody>
                <tr><th class="dark" rowspan="2">AL</th><th>OD</th><td>{{ investigations.od.al }}</td></tr>
                <tr><th>OS</th><td>{{ investigations.os.al }}</td></tr>
              </tbody>
            </table>
            <table class="grid">
              <tbody>
                <tr><th class="dark" rowspan="2">IOL</th><th>OD</th><td>{{ investigations.od.iol }}</td></tr>
                <tr><th>OS</th><td>{{ investigations.os.iol }}</td></tr>
              </tbody>
            </table>
          </div>
        </section>
        <section v-if="plan.note" class="note-block box">
          <b>Note:</b> <span dir="auto">{{ clip('Note', plan.note, 220) }}</span>
        </section>
        <p v-if="clipped.length" class="clip-note">* خلاصه‌شده؛ متن کامل در پرونده دیجیتالی.</p>
      </article>

      <!-- Back: advice, diagnosis, prescription and signature. -->
      <article ref="backSheet" class="sheet sheet--back" :class="{ 'sheet--back-dense': densePrescription, 'sheet--back-measuring': !fitReady }" :aria-busy="!fitReady" data-side="پشت ورق" dir="ltr" aria-label="پشت دوسیه">
        <section class="back-advice box">
          <b>Advice:</b>
          <p dir="auto">{{ clip('Advice', plan.advice, 420) }}</p>
        </section>
        <section class="back-dx box">
          <div v-for="eye in (['OD', 'OS'] as const)" :key="eye" class="back-dx__col">
            <div class="back-dx__title"><b v-if="eye === 'OD'">Diagnosis:</b> {{ eye }}</div>
            <ol>
              <li v-for="(text, i) in numbered(eye === 'OD' ? diagnosesOd : diagnosesOs)" :key="i"><span class="dots" dir="auto">{{ text }}</span></li>
            </ol>
          </div>
          <p v-if="diagnosesGeneral.length" class="back-dx__general" dir="auto"><b>General:</b> {{ diagnosesGeneral.join(' ؛ ') }}</p>
        </section>
        <section class="back-rx box">
          <div class="rx-mark">Rx:</div>
          <table v-if="rxShown.length" class="rx-table" dir="rtl">
            <colgroup>
              <col class="rx-table__drug-col" />
              <col class="rx-table__dose-col" />
              <col class="rx-table__qty-col" />
            </colgroup>
            <thead>
              <tr>
                <th scope="col">دوا</th>
                <th scope="col" class="rx-table__dose">طریقه مصرف</th>
                <th scope="col" class="rx-table__qty">تعداد</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(item, i) in rxShown" :key="item.id">
                <td class="rx-table__drug"><span class="rx-table__index">{{ i + 1 }}.</span> <span dir="auto">{{ clip(`Rx ${i + 1}`, item.drugName, 80) }}</span></td>
                <td class="rx-table__dose">
                  <strong dir="auto">{{ item.dose || '—' }}</strong>
                  <small v-if="item.route || item.duration" dir="auto">{{ [item.route, item.duration].filter(Boolean).join(' · ') }}</small>
                </td>
                <td class="rx-table__qty" dir="auto">{{ item.quantity }}</td>
              </tr>
            </tbody>
          </table>
          <p v-if="rxHidden > 0" class="rx-more" dir="rtl">* {{ rxHidden }} قلم دیگر در این ورق جا نشد؛ فهرست کامل در پرونده دیجیتالی است.</p>
          <p v-for="note in rxNotes" :key="note" class="rx-note" dir="auto">{{ note }}</p>
          <div v-for="o in optical" :key="o.id" class="rx-glasses">
            <b>Glasses {{ o.rxNumber }}:</b>
            OD {{ rxText({ sphere: o.sphereOd, cylinder: o.cylinderOd, axis: o.axisOd, add: o.addOd }) }} &nbsp;|&nbsp;
            OS {{ rxText({ sphere: o.sphereOs, cylinder: o.cylinderOs, axis: o.axisOs, add: o.addOs }) }}
            <template v-if="o.pd"> &nbsp;|&nbsp; PD {{ o.pd }}</template>
          </div>
          <div class="back-sign">
            <div><b>Ophthalmologist's Signature</b> <span class="sign-line"></span></div>
          </div>
        </section>
        <footer class="back-foot" dir="auto">
          <span v-if="hospital?.address">{{ hospital.address }}</span>
          <span v-if="hospital?.phone" dir="ltr">{{ hospital.phone }}</span>
        </footer>
      </article>
    </template>
  </div>
</template>

<style scoped>
.paper-screen {
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
}

.paper-tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  max-width: 210mm;
  margin: 0 auto 12px;
}

.paper-tools__hint {
  color: var(--xr-text);
  font-size: 12.25px;
}

/* One sheet side, at its real paper size. */
.sheet {
  --line: 0.5pt solid #000;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 1.6mm;
  width: 210mm;
  height: 297mm;
  margin: 0 auto 12px;
  padding: 7mm 8mm;
  overflow: hidden;
  background: #fff;
  color: #000;
  font-family: 'Poppins', 'Vazirmatn', sans-serif;
  font-size: 8.5pt;
  line-height: 1.3;
  overflow-wrap: anywhere;
}

.sheet * {
  box-sizing: border-box;
}

.sheet--back-measuring {
  visibility: hidden;
}

.sheet p {
  margin: 0;
}

.box {
  border: var(--line);
  padding: 1.2mm 1.6mm;
}

.kv b,
.sheet b {
  font-weight: 600;
}

.line {
  border-bottom: var(--line);
  padding-bottom: 0.8mm;
  margin-bottom: 0.8mm;
}

.unit {
  font-size: 7pt;
}

.small {
  font-size: 7.5pt;
}

.tick {
  display: inline-block;
  width: 2.6mm;
  height: 2.6mm;
  margin-inline: 0.6mm 1.4mm;
  border: var(--line);
  vertical-align: -0.3mm;
}

/* Prints even with the browser's "background graphics" off, which is its default. */
.tick--on {
  box-shadow: inset 0 0 0 3mm #000;
  print-color-adjust: exact;
  -webkit-print-color-adjust: exact;
}

/* Identity block */
.id-block {
  display: grid;
  grid-template-columns: 38mm 1fr 44mm;
  gap: 1.2mm;
}

.id-block__left {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 2mm;
}

.id-block__row {
  display: flex;
  gap: 4mm;
}

.id-block__addr {
  flex: 1;
  min-width: 0;
}

.id-block__brand {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1mm;
  text-align: center;
}

.brand-logo {
  max-width: 100%;
  max-height: 14mm;
}

.brand-name {
  font-size: 10pt;
  font-weight: 700;
}

/* Grids */
.grid {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.grid th,
.grid td {
  height: 5.1mm;
  padding: 0 1mm;
  border: var(--line);
  font-weight: 400;
  text-align: center;
  overflow: hidden;
}

.grid td {
  font-size: 8pt;
}

.grid th {
  font-weight: 600;
}

/* Solid black prints reliably even with the browser's "background graphics" turned off. */
.dark {
  box-shadow: inset 0 0 0 20mm #000;
  color: #fff;
  print-color-adjust: exact;
  -webkit-print-color-adjust: exact;
}

.italic {
  font-family: Georgia, 'Times New Roman', serif;
  font-style: italic;
  font-weight: 400 !important;
}

.display {
  margin: 0 0 1mm;
  color: #000;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 13pt;
  font-style: italic;
  font-weight: 700;
}

.hx-block {
  display: grid;
  grid-template-columns: 1fr 54mm;
  gap: 1.2mm;
}

.hx-block__text {
  height: 30mm;
  overflow: hidden;
}

.sys-block {
  display: grid;
  grid-template-columns: 1fr 54mm;
  gap: 1.2mm;
}

.sys-block__list {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 0.6mm 3mm;
  align-content: center;
  font-size: 7.8pt;
}

.sys-item--wide {
  grid-column: 1 / -1;
  display: flex;
  align-items: center;
  gap: 1mm;
}

.sys-item__to {
  flex: 1;
  margin-inline-start: 3mm;
}

.sys-item__years {
  font-size: 7pt;
}

/* Slit lamp */
.slit {
  display: grid;
  grid-template-columns: 70mm 1fr;
  gap: 2mm;
}

.slit__art {
  display: flex;
  flex-direction: column;
}

.slit__eyes {
  width: 100%;
  margin-top: 4mm;
}

.slit__table td {
  height: 5.8mm;
  text-align: start;
  font-size: 7.6pt;
}

.slit__table th:first-child {
  width: 15mm;
}

.side-title {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 7mm;
  border-inline-end: var(--line);
}

.side-title span {
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 11pt;
  font-style: italic;
  font-weight: 700;
  writing-mode: vertical-rl;
  transform: rotate(180deg);
}

/* Fundus */
.fundus,
.refr {
  display: flex;
  gap: 1.6mm;
  padding: 0;
}

.fundus__body {
  flex: 1;
  min-width: 0;
  padding: 1mm 1.6mm;
}

.fundus__line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2.4mm;
  padding-bottom: 0.8mm;
  border-bottom: var(--line);
}

.fundus__method {
  padding-block: 0.6mm;
  font-size: 7.8pt;
  text-align: center;
}

.fundus__grid {
  display: grid;
  grid-template-columns: 26mm 1fr 26mm;
  align-items: center;
  gap: 1.6mm;
}

.fundus__eye {
  width: 100%;
}

.fundus__table th:first-child {
  width: 19mm;
}

.fundus__table td {
  text-align: start;
  font-size: 7.4pt;
}

.fundus__table th,
.fundus__table td {
  height: 4.6mm;
}

/* Refraction */
.refr__left {
  flex: 1;
  min-width: 0;
  padding: 1mm 0;
  font-size: 7.8pt;
}

.refr__row {
  display: flex;
  flex-wrap: wrap;
  gap: 2mm;
  padding-block: 0.2mm;
}

.refr__cross {
  display: flex;
  justify-content: space-around;
}

.refr__cross svg {
  width: 36mm;
}

.refr__right {
  display: flex;
  flex-direction: column;
  gap: 0.8mm;
  width: 76mm;
  padding: 1mm 1.6mm 1mm 0;
}

.tagbox {
  display: flex;
  align-items: center;
  gap: 2mm;
  min-height: 9mm;
  padding: 0.4mm 1.4mm 0.4mm 0;
  border: var(--line);
  font-size: 7.8pt;
}

.tag {
  display: flex;
  align-items: center;
  justify-content: center;
  align-self: stretch;
  width: 10mm;
  box-shadow: inset 0 0 0 20mm #000;
  color: #fff;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 12pt;
  print-color-adjust: exact;
  -webkit-print-color-adjust: exact;
}

/* Measurements */
.meas {
  display: grid;
  gap: 0.8mm;
}

.meas__top {
  display: grid;
  grid-template-columns: 2fr 1fr 1.4fr 1.4fr;
  gap: 2mm;
}

.meas__bottom {
  display: grid;
  grid-template-columns: 2.4fr 1fr 1fr;
  gap: 1.2mm;
}

.meas__bottom td {
  text-align: start;
}

.meas__bottom .dark {
  width: 12mm;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 11pt;
}

.meas__bottom th:not(.dark) {
  width: 9mm;
}

.note-block {
  font-size: 8pt;
  line-height: 1.3;
}

.clip-note {
  margin-top: auto;
  font-size: 7pt;
  text-align: end;
}

/* Back of the sheet */
.sheet--back {
  justify-content: flex-start;
  font-size: 9.5pt;
  line-height: 1.4;
}

.back-advice {
  min-height: 24mm;
}

.back-dx {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 2mm 8mm;
  padding: 2mm 3mm;
}

.back-dx__title {
  margin-bottom: 1mm;
}

.back-dx ol {
  margin: 0;
  padding-inline-start: 6mm;
}

.back-dx li {
  padding-block: 1.1mm;
}

.dots {
  display: block;
  min-height: 5mm;
  border-bottom: 0.5pt dotted #000;
}

.back-dx__general {
  grid-column: 1 / -1;
}

.back-rx {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  padding: 2mm 3mm;
}

.rx-mark {
  margin-bottom: 2mm;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 20pt;
  font-style: italic;
  font-weight: 700;
  line-height: 1;
}

.rx-table {
  table-layout: fixed;
  width: 100%;
  border-collapse: collapse;
  border: 0.75pt solid #000;
}

.rx-table__drug-col {
  width: 52%;
}

.rx-table__dose-col {
  width: 33%;
}

.rx-table__qty-col {
  width: 15%;
}

.rx-table th,
.rx-table td {
  border-inline-start: 0.5pt solid #000;
  border-bottom: 0.5pt solid #000;
  text-align: start;
  vertical-align: middle;
}

.rx-table th:first-child,
.rx-table td:first-child {
  border-inline-start: 0;
}

.rx-table th {
  padding: 1.2mm 1.8mm;
  border-bottom-width: 0.75pt;
  font-size: 8.5pt;
  font-weight: 700;
}

.rx-table td {
  height: 8.5mm;
  padding: 1.2mm 1.8mm;
}

.rx-table__drug {
  font-size: 11pt;
  font-weight: 700;
  line-height: 1.18;
  overflow-wrap: anywhere;
}

.rx-table__index {
  display: inline-block;
  min-width: 4mm;
  font-size: 8pt;
  font-weight: 600;
}

.rx-table__dose strong {
  display: block;
  font-size: 10pt;
  font-weight: 650;
  line-height: 1.15;
  overflow-wrap: anywhere;
}

.rx-table__dose small {
  display: block;
  margin-top: 0.7mm;
  font-size: 7.5pt;
  font-weight: 500;
  line-height: 1.15;
  overflow-wrap: anywhere;
}

/* The dose ("1x3") reads left to right inside a right-to-left table; centred, it sits under its heading either way. */
.rx-table th.rx-table__dose,
.rx-table td.rx-table__dose,
.rx-table th.rx-table__qty,
.rx-table td.rx-table__qty {
  text-align: center;
}

.rx-table td.rx-table__qty {
  font-size: 11.5pt;
  font-weight: 700;
  white-space: nowrap;
}

.rx-more,
.rx-note,
.rx-glasses {
  margin-top: 1.4mm !important;
}

/* A fixed A4 back supports up to 16 medicines. Only dense prescriptions tighten the
   surrounding vertical rhythm; ordinary prescriptions retain the generous ledger. */
.sheet--back-dense {
  font-size: 8.5pt;
  line-height: 1.25;
}

.sheet--back-dense .back-advice {
  min-height: 20mm;
}

.sheet--back-dense .back-dx {
  gap-block: 1mm;
  padding-block: 1.2mm;
}

.sheet--back-dense .back-dx__title {
  margin-bottom: 0.4mm;
}

.sheet--back-dense .back-dx li {
  padding-block: 0.45mm;
}

.sheet--back-dense .dots {
  min-height: 4mm;
}

.sheet--back-dense .back-rx {
  padding-block: 1.2mm;
}

.sheet--back-dense .rx-mark {
  margin-bottom: 1mm;
  font-size: 17pt;
}

.sheet--back-dense .rx-table th {
  padding: 0.65mm 1.2mm;
  font-size: 7.5pt;
}

.sheet--back-dense .rx-table td {
  height: 5.8mm;
  padding: 0.35mm 1.2mm;
}

.sheet--back-dense .rx-table__drug {
  font-size: 9.5pt;
  line-height: 1.05;
}

.sheet--back-dense .rx-table__index {
  min-width: 3.5mm;
  font-size: 7pt;
}

.sheet--back-dense .rx-table__dose strong {
  font-size: 8.75pt;
  line-height: 1.05;
}

.sheet--back-dense .rx-table__dose small {
  margin-top: 0.2mm;
  font-size: 6.75pt;
  line-height: 1.05;
}

.sheet--back-dense .rx-table td.rx-table__qty {
  font-size: 10.25pt;
}

.sheet--back-dense .rx-more,
.sheet--back-dense .rx-note,
.sheet--back-dense .rx-glasses {
  margin-top: 0.8mm !important;
  font-size: 7.5pt;
  line-height: 1.15;
}

.sheet--back-dense .back-sign {
  gap: 1mm;
  font-size: 8pt;
}

.back-sign {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 2mm;
  margin-top: auto;
}

.sign-line {
  display: inline-block;
  width: 45mm;
  border-bottom: 0.5pt solid #000;
}

.back-foot {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 1mm 6mm;
  padding-top: 1mm;
  border-top: var(--line);
  font-size: 8pt;
}

@media print {
  .paper-screen {
    padding: 0;
    background: #fff;
  }

  .paper-tools,
  .alert {
    display: none !important;
  }

  .sheet {
    margin: 0;
    break-after: page;
  }

  .sheet:last-of-type {
    break-after: auto;
  }
}

@media screen {
  .sheet {
    box-shadow: 0 2px 12px rgba(0, 0, 0, 0.15);
  }
}
</style>

<style>
@page {
  size: A4 portrait;
  margin: 0;
}
</style>
