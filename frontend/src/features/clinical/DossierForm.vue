<script lang="ts">
import type { EyeFindings } from './examRecord'

/** What the doctor writes on the dossier. The page that owns it saves it; this form only shows and edits it. */
export interface DossierExam {
  od: EyeFindings
  os: EyeFindings
  history: string
  advice: string
  dxOd: string[]
  dxOs: string[]
  dxOther: { eye: string; text: string }[]
}

export interface RxLine {
  drugName: string
  dose: string
  route: string
  duration: string
  instructions: string
  quantity: number
}

export interface DossierFollowUp {
  editing: number | null
  dueDate: string | null
  note: string
}
</script>

<script setup lang="ts">
import { computed } from 'vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { formatDate } from '@/utils/date'
import DrugPicker from './DrugPicker.vue'
import { doseOptions } from './doctorShortcuts'
import { anteriorRows, fundusOptions, parseMeasurements, parseRefractionExtras, systemicConditions } from './examRecord'
import type { Chart, Refraction, RefractionLine } from './types'

/**
 * The hospital's paper file on screen, filled in the way the previous system did it: every examination box
 * opens the same list of choices, a click on the drawing of an eye picks its fundus finding, and diagnosis,
 * advice, drugs and follow-up are written on the back. What triage and the optometrist recorded is shown
 * where the paper has it, and stays theirs.
 */
const props = defineProps<{
  chart: Chart
  refraction: Refraction | null
  exam: DossierExam
  rxLines: RxLine[]
  followUp: DossierFollowUp
  examEditable: boolean
  rxEditable: boolean
  followUpEditable: boolean
}>()

const emit = defineEmits<{
  pickDrug: [name: string]
  editFollowUp: [f: { id: number; dueDate: string; note: string | null }]
  removeFollowUp: [f: { id: number; dueDate: string }]
  clearFollowUp: []
}>()

const pre = computed(() => props.chart.preExam)
const extras = computed(() => parseMeasurements(pre.value?.measurements))
const investigations = computed(() => parseRefractionExtras(props.refraction?.measurements))

/** The ten rows printed on the paper, with its short names. */
const short: Record<string, string> = { Conjunctiva: 'Conj', Vitreous: 'Vit', EOM: 'E.O.M' }
const slitRows = anteriorRows.filter((row) => row.code !== 'Episclera').map((row) => ({ ...row, short: short[row.code] ?? row.code }))

/** A value written before the lists existed stays selectable, so opening a record never changes it. */
const choices = (options: string[], current: string | undefined) => (current && !options.includes(current) ? [current, ...options] : options)

const eyes = computed(() => [
  { code: 'OD' as const, findings: props.exam.od },
  { code: 'OS' as const, findings: props.exam.os },
])

const stageLine = (stage: string, eye: 'OD' | 'OS') => props.refraction?.lines.find((l) => l.stage === stage && l.eye === eye) ?? null
const rxText = (line: RefractionLine | null) =>
  line ? [line.sphere, line.cylinder, line.axis ? `× ${line.axis}` : null, line.add ? `Add ${line.add}` : null, line.va ? `(${line.va})` : null].filter(Boolean).join(' ') : ''

const measured = computed(() => {
  const i = investigations.value
  return [i.rbs, i.jcc, ...Object.values(i.od), ...Object.values(i.os)].some((v) => v?.trim())
})

const sides = computed(() => [
  { code: 'OD' as const, lines: props.exam.dxOd },
  { code: 'OS' as const, lines: props.exam.dxOs },
])
</script>

<template>
  <div class="dossier">
    <!-- Front of the file. -->
    <article class="sheet" dir="ltr" aria-label="روی دوسیه">
      <section class="pair">
        <div class="box hx">
          <b>Hx:</b>
          <p v-if="pre?.chiefComplaint" class="given" dir="auto">{{ pre.chiefComplaint }}<template v-if="extras.duration"> — {{ extras.duration }}</template><template v-if="extras.eye"> ({{ extras.eye }})</template></p>
          <p v-if="pre?.history" class="given" dir="auto">{{ pre.history }}</p>
          <textarea v-model="exam.history" class="cell cell--area" dir="auto" rows="3" maxlength="1500" :disabled="!examEditable" aria-label="شرح حال داکتر"></textarea>
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
          <div class="sys__wide">
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
        <div>
          <h2 class="display">Slit Lamp Examination</h2>
          <!-- Each eye is a button: a click opens the fundus choices, and the choice is written under the drawing. -->
          <div class="eyes">
            <label v-for="eye in eyes" :key="eye.code" class="eye" :class="{ 'eye--set': eye.findings.values.Fundus, 'eye--locked': !examEditable }">
              <svg viewBox="0 0 150 96" aria-hidden="true">
                <g fill="none" stroke="currentColor" stroke-width="1.2">
                  <path :d="eye.code === 'OD' ? 'M18 34 Q72 8 132 36 Q72 24 18 34Z' : 'M18 36 Q78 8 132 34 Q78 24 18 36Z'" />
                  <path d="M16 62 Q75 26 134 62 Q75 94 16 62Z" />
                  <circle cx="75" cy="61" r="15" /><circle cx="75" cy="61" r="6" />
                </g>
              </svg>
              <span class="eye__code">{{ eye.code }}</span>
              <span class="eye__value">{{ eye.findings.values.Fundus || (examEditable ? 'برای انتخاب کلیک کنید' : '') }}</span>
              <select v-model="eye.findings.values.Fundus" class="eye__select" :disabled="!examEditable" :aria-label="`فوندوس ${eye.code}`">
                <option value=""></option>
                <option v-for="option in choices(fundusOptions, eye.findings.values.Fundus)" :key="option" :value="option">{{ option }}</option>
              </select>
            </label>
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

      <section v-if="measured" class="box meas">
        <span><b>Color vision:</b> OD: {{ investigations.od.colorVision }} &nbsp; OS: {{ investigations.os.colorVision }}</span>
        <span><b>RBS:</b> {{ investigations.rbs }} <small>mg/dl</small></span>
        <span><b>JCC:</b> {{ investigations.jcc }}</span>
        <span><b>K1/K2</b> OD: {{ [investigations.od.k1, investigations.od.k2].filter(Boolean).join(' / ') }} &nbsp; OS: {{ [investigations.os.k1, investigations.os.k2].filter(Boolean).join(' / ') }}</span>
        <span><b>AL</b> OD: {{ investigations.od.al }} &nbsp; OS: {{ investigations.os.al }}</span>
        <span><b>IOL</b> OD: {{ investigations.od.iol }} &nbsp; OS: {{ investigations.os.iol }}</span>
      </section>
    </article>

    <!-- Back of the file. -->
    <article class="sheet" dir="ltr" aria-label="پشت دوسیه">
      <section class="box">
        <label class="back-label" for="dossier-advice">Advice:</label>
        <textarea id="dossier-advice" v-model="exam.advice" class="cell cell--area" dir="auto" rows="3" maxlength="800" :disabled="!examEditable" aria-label="Advice"></textarea>
      </section>

      <section class="box dx">
        <div v-for="side in sides" :key="side.code">
          <div><b v-if="side.code === 'OD'" class="back-label">Diagnosis:</b> <b>{{ side.code }}</b></div>
          <ol>
            <li v-for="(_, i) in side.lines" :key="i">
              <input v-model="side.lines[i]" class="cell cell--line" dir="auto" maxlength="500" autocomplete="off" :disabled="!examEditable" :aria-label="`تشخیص ${side.code} ${i + 1}`" />
            </li>
          </ol>
        </div>
        <!-- Older records may hold a diagnosis for both eyes or for neither; it stays visible and editable. -->
        <div v-for="(d, i) in exam.dxOther" :key="`other-${i}`" class="dx__other">
          <b>{{ d.eye === 'Both' ? 'OU' : 'General' }}:</b>
          <input v-model="d.text" class="cell cell--line" dir="auto" maxlength="500" autocomplete="off" :disabled="!examEditable" :aria-label="`تشخیص عمومی ${i + 1}`" />
        </div>
      </section>

      <section class="box rx">
        <div class="rx__head">
          <div class="rx-mark">Rx:</div>
          <slot name="rx-meta" />
        </div>
        <div v-if="rxEditable" dir="rtl"><DrugPicker @pick="emit('pickDrug', $event)" /></div>
        <datalist id="dossier-doses"><option v-for="d in doseOptions" :key="d" :value="d"></option></datalist>
        <table v-if="rxLines.length" class="rx-table">
          <thead><tr><th class="rx-table__no">#</th><th>Drug</th><th class="rx-table__dose">Dose</th><th class="rx-table__qty">Qty</th><th class="rx-table__del"></th></tr></thead>
          <tbody>
            <tr v-for="(item, i) in rxLines" :key="i">
              <td class="rx-table__no">{{ i + 1 }}.</td>
              <td><input v-model="item.drugName" class="cell cell--drug" dir="auto" maxlength="200" autocomplete="off" :disabled="!rxEditable" :aria-label="`نام دوا ${i + 1}`" /></td>
              <td><input v-model="item.dose" class="cell" dir="auto" list="dossier-doses" maxlength="100" autocomplete="off" :disabled="!rxEditable" :aria-label="`دوز ${i + 1}`" /></td>
              <td><input v-model.number="item.quantity" class="cell" type="number" min="0.001" step="any" required :disabled="!rxEditable" :aria-label="`تعداد ${i + 1}`" /></td>
              <td class="rx-table__del"><button v-if="rxEditable" type="button" class="remove" :aria-label="`حذف دوا ${i + 1}`" title="حذف دوا" @click="rxLines.splice(i, 1)">×</button></td>
            </tr>
          </tbody>
        </table>
        <div dir="rtl"><slot name="rx-extra" /></div>

        <div class="fu">
          <b class="back-label">F / U within</b>
          <span v-for="f in chart.followUps" :key="f.id" class="fu__item" dir="rtl">
            {{ formatDate(f.dueDate) }}<template v-if="f.note"> · {{ f.note }}</template>
            <template v-if="followUpEditable">
              <button type="button" class="link" @click="emit('editFollowUp', f)">ویرایش</button>
              <button type="button" class="link link--danger" @click="emit('removeFollowUp', f)">حذف</button>
            </template>
          </span>
          <div v-if="followUpEditable" class="fu__new" dir="rtl">
            <ShamsiDateInput v-model="followUp.dueDate" label="تاریخ پیگیری" />
            <label class="fu__note">
              <span class="form-label">یادداشت پیگیری</span>
              <input v-model="followUp.note" class="form-control" maxlength="300" />
            </label>
            <button v-if="followUp.editing" type="button" class="link link--danger" @click="emit('clearFollowUp')">انصراف</button>
          </div>
        </div>
        <div class="sign"><b>Ophthalmologist's Signature</b> <span class="sign__line"></span></div>
      </section>
    </article>
  </div>
</template>

<style scoped>
.dossier {
  display: grid;
  gap: 18px;
  overflow-x: auto;
  padding: 4px;
}

/* Each side is drawn like the printed form: black rules on white, the same blocks in the same places. */
.sheet {
  --line: 1px solid #1f2328;
  display: grid;
  gap: 8px;
  width: 100%;
  min-width: 760px;
  max-width: 980px;
  margin: 0 auto;
  padding: 18px 20px;
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
  padding: 6px 8px;
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
  height: 30px;
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
  margin: 0 0 6px;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 22px;
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

/* A box on the paper: empty until it is clicked, written in blue like ink. */
.cell {
  width: 100%;
  height: 100%;
  min-height: 30px;
  padding: 2px 6px;
  border: 0;
  background: transparent;
  color: #0b3d91;
  font: inherit;
  font-weight: 500;
}

.cell:hover:not(:disabled) {
  background: #f1f7ff;
}

.cell:focus {
  outline: 2px solid color-mix(in srgb, var(--xr-primary) 45%, transparent);
  outline-offset: -2px;
  background: #eef5ff;
}

.cell:disabled {
  color: #111;
  opacity: 1;
}

/* A choice box shows no arrow until the pointer is on it, so the form stays as clean as the paper. */
.cell--pick {
  appearance: none;
  cursor: pointer;
  text-align: center;
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

.hx {
  min-height: 120px;
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
  grid-template-columns: 330px minmax(0, 1fr);
  gap: 12px;
}

.slit .grid th:first-child {
  width: 80px;
}

.eyes {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.eye {
  position: relative;
  display: grid;
  justify-items: center;
  gap: 2px;
  margin: 0;
  padding: 4px 4px 8px;
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
  color: #0b3d91;
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
  padding: 8px 8px 8px 0;
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
  min-height: 40px;
  border: var(--line);
}

.tag {
  display: flex;
  align-items: center;
  justify-content: center;
  align-self: stretch;
  width: 40px;
  background: #1f2328;
  color: #fff;
  font-family: Georgia, 'Times New Roman', serif;
  font-size: 17px;
}

.meas {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 4px 14px;
  font-size: 12.5px;
}

/* Back */
.back-label {
  font-size: 15px;
  font-weight: 700;
}

.dx {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px 36px;
  padding: 10px 14px;
}

.dx ol {
  margin: 4px 0 0;
  padding-inline-start: 22px;
}

.dx li {
  padding-block: 2px;
}

.dx__other {
  display: flex;
  grid-column: 1 / -1;
  align-items: center;
  gap: 8px;
}

.rx {
  display: grid;
  gap: 10px;
  padding: 10px 14px;
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
  font-size: 30px;
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
  padding: 4px 6px;
  border-bottom: var(--line);
  color: #555;
  font-size: 11.5px;
  font-weight: 600;
  text-align: start;
}

.rx-table td {
  padding: 0 2px;
  border-bottom: 1px dotted #888;
  vertical-align: middle;
}

.rx-table__no {
  width: 34px;
  text-align: center !important;
  font-weight: 600;
}

.rx-table__dose {
  width: 140px;
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
  color: var(--xr-red, #c62828);
  font-size: 18px;
  font-weight: 700;
  line-height: 1;
}

.fu {
  display: flex;
  flex-wrap: wrap;
  align-items: end;
  gap: 8px 16px;
  margin-top: 6px;
  padding-top: 10px;
  border-top: var(--line);
}

.fu__item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
}

.fu__new {
  display: grid;
  flex: 1;
  grid-template-columns: 240px minmax(0, 1fr) auto;
  gap: 10px;
  align-items: end;
  min-width: 520px;
}

.fu__note {
  margin: 0;
}

.sign {
  display: flex;
  align-items: flex-end;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 6px;
}

.sign__line {
  width: 200px;
  border-bottom: var(--line);
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
</style>
