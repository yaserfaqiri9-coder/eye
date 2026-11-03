<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api/http'
import { parseFindings, parseMeasurements, parseRefractionExtras } from '@/features/clinical/examRecord'
import type { Chart, Refraction, SurgeryDetail } from '@/features/clinical/types'
import { eyeLabels } from '@/features/clinical/types'
import type { Patient } from '@/features/patients/types'
import { printPaper, shamsiPaper, sheetOverflows, useClipper, useHospital, usePaperPage } from './paper'

const props = defineProps<{ id: string }>()
const { hospital, load: loadHospital } = useHospital()
const { clipped, clip } = useClipper()

const page = usePaperPage(async () => {
  const surgery = await api<SurgeryDetail>(`/surgeries/${props.id}`)
  const [chart, patient, refraction] = await Promise.all([
    api<Chart>(`/consultations/${surgery.case.visitId}/chart`),
    api<Patient>(`/patients/${surgery.case.patientId}`),
    api<Refraction | undefined>(`/refractions/${surgery.case.visitId}`).catch(() => undefined),
  ])
  return { surgery, chart, patient, refraction: refraction ?? null }
})

const data = computed(() => page.data.value)
const pre = computed(() => data.value?.chart.preExam ?? null)
const extras = computed(() => parseMeasurements(pre.value?.measurements))
const od = computed(() => parseFindings(data.value?.chart.consultation?.findingsOd))
const os = computed(() => parseFindings(data.value?.chart.consultation?.findingsOs))
const final = (eye: 'OD' | 'OS') => data.value?.refraction?.lines.find((l) => l.stage === 'Final' && l.eye === eye) ?? null

const rxText = (eye: 'OD' | 'OS') => {
  const line = final(eye)
  return line ? [line.sphere, line.cylinder, line.axis ? `× ${line.axis}` : null].filter(Boolean).join(' ') : ''
}

const adds = computed(() => [final('OD')?.add, final('OS')?.add].filter(Boolean).join(' / '))

const diagnoses = computed(() =>
  Array.from({ length: 4 }, (_, i) => {
    const d = data.value?.chart.consultation?.diagnoses[i]
    return d ? clip(`Diagnosis ${i + 1}`, `${d.eye === 'None' ? '' : `${d.eye}: `}${d.text}`, 60) : ''
  }),
)
const moreDiagnoses = computed(() => Math.max(0, (data.value?.chart.consultation?.diagnoses.length ?? 0) - 4))

// The card's own names for each structure, in its own order.
const structures = [
  { code: 'Lids', label: 'LIDS' },
  { code: 'Conjunctiva', label: 'CONJ' },
  { code: 'Cornea', label: 'CORNEA' },
  { code: 'Episclera', label: 'EPISCL' },
  { code: 'Sclera', label: 'SCLERA' },
  { code: 'AC', label: 'AC' },
  { code: 'Iris', label: 'IRIS' },
  { code: 'Pupil', label: 'PUPILS' },
  { code: 'Lens', label: 'LENS' },
  { code: 'Disc', label: 'DISC' },
  { code: 'Fundus', label: 'FUNDUS' },
  { code: 'Other', label: 'OTHER' },
]

const rxItems = computed(() => (data.value?.chart.prescriptions ?? []).filter((p) => p.status !== 'Cancelled').flatMap((p) => p.items))
const rxShown = computed(() => rxItems.value.slice(0, 12))

const overflow = ref(false)

onMounted(async () => {
  await Promise.all([page.load(), loadHospital()])
  overflow.value = await sheetOverflows('.card-sheet')
})
</script>

<template>
  <div class="card-screen">
    <div v-if="page.error.value" class="alert alert-danger" role="alert">{{ page.error.value }}</div>
    <p v-else-if="page.loading.value" class="xr-state" role="status">در حال آماده‌سازی کارت...</p>

    <template v-if="data">
      <div class="card-tools">
        <button type="button" class="btn btn-primary" :disabled="overflow" @click="printPaper">چاپ</button>
        <span class="card-tools__hint">فقط از معلومات ثبت‌شده همین مراجعه ساخته می‌شود.</span>
      </div>
      <div v-if="overflow" class="alert alert-danger card-tools" role="alert">محتوای این کارت از یک ورق بیشتر شد و بخش پایین آن چاپ نمی‌شد. چاپ متوقف است؛ لطفاً به مسئول سیستم اطلاع دهید.</div>
      <div v-if="clipped.length" class="alert alert-warning card-tools" role="alert">{{ clipped.length }} مورد خلاصه شد (با علامت *): {{ clipped.map((c) => c.field).join('، ') }}. متن کامل در پرونده دیجیتالی باقی است.</div>

      <article class="card-sheet" dir="ltr" aria-label="کارت عملیات‌خانه">
        <header class="card-head">
          <div class="card-head__clinic" dir="auto">{{ hospital?.hospitalName }}</div>
          <img v-if="hospital?.logoAttachmentId" :src="`/api/attachments/${hospital.logoAttachmentId}`" alt="" class="card-head__logo" />
          <div class="card-head__no"><b>CARD NO:</b> {{ data.surgery.case.caseNumber }}</div>
        </header>

        <div class="row-line row-line--name">
          <span class="grow"><b>NAME:</b> <span dir="auto">{{ data.surgery.case.patientName }}</span></span>
          <span class="grow"><b>FATHER:</b> <span dir="auto">{{ data.patient.fatherName }}</span></span>
          <span>M <i class="tick" :class="{ 'tick--on': data.patient.gender === 'Male' }"></i></span>
          <span>F <i class="tick" :class="{ 'tick--on': data.patient.gender === 'Female' }"></i></span>
          <span><b>AGE:</b> {{ data.patient.ageYears }}</span>
        </div>
        <div class="row-line">
          <span class="grow"><b>ADDRESS:</b> <span dir="auto">{{ clip('Address', data.patient.address, 80) }}</span></span>
          <span><b>DATE:</b> {{ shamsiPaper(data.chart.visit.visitDate) }}</span>
        </div>
        <div class="row-line">
          <span class="grow"><b>OPERATION:</b> <span dir="auto">{{ data.surgery.case.surgeryType }}</span></span>
          <span><b>EYE:</b> {{ eyeLabels[data.surgery.case.eye] }}</span>
          <span><b>SCHEDULED:</b> {{ shamsiPaper(data.surgery.case.scheduledAt) }}</span>
        </div>

        <section class="triple">
          <div class="cell"><b>MAIN COMPLAINT:</b><p dir="auto">{{ clip('Main complaint', pre?.chiefComplaint, 220) }}</p></div>
          <div class="cell"><b>DURATION:</b><p dir="auto">{{ extras.duration }}</p></div>
          <div class="cell cell--dx">
            <b>DIAGNOSIS:</b>
            <ol>
              <li v-for="(text, i) in diagnoses" :key="i" dir="auto">{{ text }}</li>
            </ol>
            <small v-if="moreDiagnoses" dir="rtl">* {{ moreDiagnoses }} تشخیص دیگر در پرونده دیجیتالی.</small>
          </div>
        </section>

        <section class="vision-row">
          <table class="grid">
            <thead><tr><th colspan="3">VISION</th></tr><tr><th>R</th><th></th><th>L</th></tr></thead>
            <tbody>
              <tr><td>{{ pre?.visualAcuityOd }}</td><th>U.A.</th><td>{{ pre?.visualAcuityOs }}</td></tr>
              <tr><td>{{ extras.glassesOd }}</td><th>G.L.</th><td>{{ extras.glassesOs }}</td></tr>
            </tbody>
          </table>
          <div class="cell">
            <b>REFRACTION:</b>
            <p>R: {{ rxText('OD') }}</p>
            <p>L: {{ rxText('OS') }}</p>
            <div class="add-pd"><span><b>Add:</b> {{ adds }}</span><span><b>PD:</b> {{ data.refraction?.pd }}</span></div>
            <small v-if="data.refraction && parseRefractionExtras(data.refraction.measurements).lens.length">{{ parseRefractionExtras(data.refraction.measurements).lens.join(', ') }}</small>
          </div>
        </section>

        <section class="exam-row">
          <div class="cell exam">
            <div class="exam__head"><b>R.E.</b><b>L.E.</b></div>
            <div class="exam__art" aria-hidden="true">
              <svg v-for="n in 2" :key="n" viewBox="0 0 90 60"><g fill="none" stroke="#000"><ellipse cx="45" cy="30" rx="40" ry="28" /><path d="M8 30 Q45 2 82 30 Q45 58 8 30Z" /><circle cx="45" cy="30" r="13" /><circle cx="45" cy="30" r="6" /></g></svg>
            </div>
            <table class="exam__table">
              <tbody>
                <tr v-for="row in structures" :key="row.code">
                  <td>{{ clip(`${row.label} R`, od.values[row.code], 30) }}</td>
                  <th>{{ row.label }}</th>
                  <td>{{ clip(`${row.label} L`, os.values[row.code], 30) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="side">
            <div class="side__line"><b>IOP:</b> R {{ pre?.iopOd }} &nbsp; L {{ pre?.iopOs }}</div>
            <div class="side__line"><b>BP:</b> {{ extras.bp }}</div>
            <!-- The label is printed exactly as on the card. Whether it means syringing or surgery awaits the medical lead, so it stays empty. -->
            <div class="side__line"><b>Surging:</b></div>
            <div class="side__rx">
              <div class="rx-mark">Rx</div>
              <ol v-if="rxShown.length">
                <li v-for="item in rxShown" :key="item.id" dir="auto">{{ clip(`Rx ${item.id}`, item.drugName, 40) }} <small>{{ [item.dose, item.route, item.duration].filter(Boolean).join(' · ') }}</small></li>
              </ol>
              <small v-if="rxItems.length > rxShown.length" dir="rtl">* {{ rxItems.length - rxShown.length }} قلم دیگر در پرونده دیجیتالی.</small>
              <div class="side__sign"><b>Signature:</b> <span class="sign-line"></span></div>
            </div>
          </div>
        </section>
      </article>
    </template>
  </div>
</template>

<style scoped>
.card-screen {
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
}

.card-tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  width: 210mm;
  max-width: 100%;
  margin: 0 auto 12px;
}

.card-tools__hint {
  color: var(--xr-text);
  font-size: 12.25px;
}

.card-sheet {
  --line: 0.5pt solid #000;
  display: flex;
  flex-direction: column;
  width: 210mm;
  height: 297mm;
  margin: 0 auto;
  padding: 10mm;
  overflow: hidden;
  background: #fff;
  color: #000;
  font-family: 'Poppins', 'Vazirmatn', sans-serif;
  font-size: 9.5pt;
  line-height: 1.35;
}

.card-sheet * {
  box-sizing: border-box;
}

.card-sheet b {
  font-weight: 600;
}

.card-sheet p {
  margin: 0;
}

.card-head {
  display: grid;
  grid-template-columns: 1fr auto auto;
  align-items: center;
  gap: 6mm;
  padding: 3mm;
  border: var(--line);
}

.card-head__clinic {
  font-size: 13pt;
  font-weight: 700;
  letter-spacing: 0.04em;
}

.card-head__logo {
  max-height: 16mm;
}

.row-line {
  display: flex;
  gap: 5mm;
  padding: 1.6mm 2mm;
  border: var(--line);
  border-top: 0;
}

.grow {
  flex: 1;
  min-width: 0;
}

.tick {
  display: inline-block;
  width: 3mm;
  height: 3mm;
  border: var(--line);
  vertical-align: -0.4mm;
}

.tick--on {
  box-shadow: inset 0 0 0 3mm #000;
  print-color-adjust: exact;
  -webkit-print-color-adjust: exact;
}

.cell {
  padding: 1.6mm 2mm;
  border: var(--line);
  border-top: 0;
}

.triple {
  display: grid;
  grid-template-columns: 1.1fr 0.9fr 1.1fr;
  min-height: 40mm;
}

.triple .cell + .cell,
.vision-row .cell {
  border-inline-start: 0;
}

.cell--dx ol {
  margin: 0;
  padding-inline-start: 5mm;
}

.cell--dx li {
  min-height: 6mm;
  border-bottom: var(--line);
}

.vision-row {
  display: grid;
  grid-template-columns: 70mm 1fr;
}

.grid {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.grid th,
.grid td {
  height: 6.5mm;
  border: var(--line);
  border-top: 0;
  font-weight: 400;
  text-align: center;
}

.grid th {
  font-weight: 600;
}

.add-pd {
  display: flex;
  justify-content: space-between;
  margin-top: 2mm;
}

.exam-row {
  display: grid;
  flex: 1;
  grid-template-columns: 1.15fr 1fr;
  min-height: 0;
}

.exam {
  display: flex;
  flex-direction: column;
}

.exam__head,
.exam__art {
  display: flex;
  justify-content: space-around;
}

.exam__art svg {
  width: 30mm;
}

.exam__table {
  width: 100%;
  margin-top: 2mm;
  border-collapse: collapse;
  table-layout: fixed;
}

.exam__table th {
  width: 22mm;
  font-weight: 500;
  text-align: center;
}

.exam__table td {
  height: 8mm;
  padding: 0 1mm;
  border-bottom: 0.25pt solid #999;
  font-size: 8.5pt;
  text-align: center;
}

.side {
  display: flex;
  flex-direction: column;
  border-inline-end: var(--line);
  border-bottom: var(--line);
}

.side__line {
  padding: 1.6mm 2mm;
  border-bottom: var(--line);
}

.side__rx {
  display: flex;
  flex: 1;
  flex-direction: column;
  padding: 2mm;
}

.side__rx ol {
  margin: 0;
  padding-inline-start: 5mm;
}

.rx-mark {
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 14pt;
  font-style: italic;
  font-weight: 700;
}

.side__sign {
  display: flex;
  align-items: flex-end;
  gap: 2mm;
  margin-top: auto;
}

.sign-line {
  flex: 1;
  border-bottom: var(--line);
}

@media print {
  .card-screen {
    padding: 0;
    background: #fff;
  }

  .card-tools,
  .alert {
    display: none !important;
  }

  .card-sheet {
    margin: 0;
  }
}

@media screen {
  .card-sheet {
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
