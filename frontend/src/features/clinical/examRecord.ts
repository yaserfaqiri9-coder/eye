// Structured clinical entry stored inside the existing free-text columns, so no table changes.
// Text stays readable wherever it is shown raw, and older free text survives as the note.

export interface ExamRow {
  code: string
  label: string
  options: string[]
}

/** Provisional until the medical lead confirms them (WORKFLOWS.md M-2). Nothing is ever selected by default. */
export const anteriorRows: ExamRow[] = [
  { code: 'Lids', label: 'پلک', options: ['Ptosis', 'Blepharitis', 'Chalazion', 'Entropion', 'Ectropion'] },
  { code: 'Conjunctiva', label: 'ملتحمه', options: ['Injection', 'Pterygium', 'Pinguecula', 'Follicles', 'Papillae'] },
  { code: 'Cornea', label: 'قرنیه', options: ['Clear', 'Edema', 'Opacity', 'Ulcer', 'Keratic precipitates'] },
  { code: 'Episclera', label: 'اپی‌اسکلرا', options: [] },
  { code: 'Sclera', label: 'صلبیه', options: ['Injection', 'Thinning', 'Nodule'] },
  { code: 'AC', label: 'اتاق قدامی', options: ['Deep', 'Shallow', 'Cells', 'Flare', 'Hypopyon', 'Hyphema'] },
  { code: 'Iris', label: 'عنبیه', options: ['Rubeosis', 'Synechiae', 'Atrophy', 'Coloboma'] },
  { code: 'Pupil', label: 'مردمک', options: ['Round', 'Irregular', 'RAPD', 'Fixed', 'Dilated'] },
  { code: 'Lens', label: 'عدسیه', options: ['Clear', 'Nuclear sclerosis', 'Cortical cataract', 'PSC', 'Pseudophakia', 'Aphakia'] },
  { code: 'Vitreous', label: 'زجاجیه', options: ['Clear', 'Floaters', 'Hemorrhage', 'PVD', 'Cells'] },
  { code: 'EOM', label: 'حرکات چشم', options: [] },
]

// The drop and lens codes are the paper's own. Their meaning awaits confirmation (M-10), so they are offered untranslated.
export const posteriorRows: ExamRow[] = [
  { code: 'Dilatation', label: 'گشادسازی مردمک', options: ['T', 'T+Ph', 'Ph', 'C'] },
  { code: 'DilateTime', label: 'زمان گشادسازی', options: [] },
  { code: 'Method', label: 'روش معاینه', options: ['90D', '78D', 'Direct', 'Indirect'] },
  { code: 'Fundus', label: 'فوندوس', options: [] },
  { code: 'Disc', label: 'دیسک بینایی', options: ['Pink', 'Pale', 'Swollen', 'Cupped'] },
  { code: 'CDR', label: 'نسبت کاپ به دیسک', options: [] },
  { code: 'Macula', label: 'ماکولا', options: ['Foveal reflex present', 'Edema', 'Drusen', 'Hole', 'Scar'] },
  { code: 'Vessels', label: 'عروق', options: ['Attenuated', 'Tortuous', 'Hemorrhages', 'Exudates', 'Neovascularization'] },
  { code: 'Periphery', label: 'محیط شبکیه', options: ['Attached', 'Tear', 'Detachment', 'Lattice'] },
  { code: 'Other', label: 'سایر', options: [] },
]

const examCodes = [...anteriorRows, ...posteriorRows].map((row) => row.code)

export interface EyeFindings {
  values: Record<string, string>
  note: string
}

/** Reads `Code: value` lines into their rows. Every other line is kept as the free note, so nothing is lost. */
export function parseFindings(text: string | null | undefined): EyeFindings {
  const values: Record<string, string> = {}
  const note: string[] = []
  for (const line of (text ?? '').split('\n')) {
    const match = line.match(/^([A-Za-z]+):\s*(.*)$/)
    if (match && examCodes.includes(match[1]) && !(match[1] in values)) values[match[1]] = match[2].trim()
    else note.push(line)
  }
  return { values, note: note.join('\n').trim() }
}

export function serializeFindings(findings: EyeFindings) {
  const lines = examCodes.filter((code) => findings.values[code]?.trim()).map((code) => `${code}: ${findings.values[code].trim()}`)
  if (findings.note.trim()) lines.push(findings.note.trim())
  return lines.join('\n')
}

const historyTag = '[شرح حال]\n'
const planTag = '[پلان]\n'
const adviceTag = '[توصیه]\n'
const planTags = [historyTag, planTag, adviceTag]

export interface PlanParts {
  history: string
  plan: string
  advice: string
}

/** The doctor's history and advice share the plan column. A plan alone is stored untagged, exactly as before. */
export function parsePlan(text: string | null | undefined): PlanParts {
  const value = text ?? ''
  const parts: PlanParts = { history: '', plan: '', advice: '' }
  if (!planTags.some((tag) => value.startsWith(tag))) {
    parts.plan = value
    return parts
  }
  const sections = value.split(/\n(?=\[(?:شرح حال|پلان|توصیه)\]\n)/)
  for (const section of sections) {
    if (section.startsWith(historyTag)) parts.history = section.slice(historyTag.length)
    else if (section.startsWith(planTag)) parts.plan = section.slice(planTag.length)
    else if (section.startsWith(adviceTag)) parts.advice = section.slice(adviceTag.length)
  }
  return parts
}

export function serializePlan(parts: PlanParts) {
  const history = parts.history.trim()
  const plan = parts.plan.trim()
  const advice = parts.advice.trim()
  if (!history && !advice) return plan
  return [history && `${historyTag}${history}`, plan && `${planTag}${plan}`, advice && `${adviceTag}${advice}`].filter(Boolean).join('\n')
}

/** Medical history asked on the paper file: answered yes or no, with how long the patient has been on treatment. */
export const systemicConditions = [
  { code: 'Diabetes', label: 'Diabetes' },
  { code: 'Cardiac', label: 'Cardiac' },
  { code: 'Arthritis', label: 'Arthritis' },
  { code: 'Asthma', label: 'Asthma' },
  { code: 'Thyroid', label: 'Thyroid D' },
  { code: 'HTN', label: 'HTN' },
] as const

export type SystemicCode = (typeof systemicConditions)[number]['code']

export interface SystemicAnswer {
  answer: '' | 'Y' | 'N'
  years: string
}

export const blankSystemic = () =>
  Object.fromEntries(systemicConditions.map((c) => [c.code, { answer: '', years: '' }])) as Record<SystemicCode, SystemicAnswer>

export interface PreExamExtras {
  eye: string
  duration: string
  pinholeOd: string
  pinholeOs: string
  glassesOd: string
  glassesOs: string
  nearOd: string
  nearOs: string
  bp: string
  syringingOd: string
  syringingOs: string
  systemic: Record<SystemicCode, SystemicAnswer>
  allergy: '' | 'Y' | 'N'
  allergicTo: string
  measurements: string
}

const preExamLines: [string, Exclude<keyof PreExamExtras, 'systemic' | 'allergy' | 'allergicTo' | 'measurements'>][] = [
  ['Eye', 'eye'],
  ['Duration', 'duration'],
  ['PH OD', 'pinholeOd'],
  ['PH OS', 'pinholeOs'],
  ['GL OD', 'glassesOd'],
  ['GL OS', 'glassesOs'],
  ['NV OD', 'nearOd'],
  ['NV OS', 'nearOs'],
  ['BP', 'bp'],
  ['Syringing OD', 'syringingOd'],
  ['Syringing OS', 'syringingOs'],
]

/** Everything the columns of the pre-exam have no place for shares its measurements column, as labelled lines. */
export function parseMeasurements(text: string | null | undefined): PreExamExtras {
  const extras: PreExamExtras = {
    eye: '', duration: '', pinholeOd: '', pinholeOs: '', glassesOd: '', glassesOs: '', nearOd: '', nearOs: '', bp: '', syringingOd: '', syringingOs: '',
    systemic: blankSystemic(), allergy: '', allergicTo: '', measurements: '',
  }
  const rest: string[] = []
  for (const line of (text ?? '').split('\n')) {
    const separator = line.indexOf(':')
    const key = separator > 0 ? line.slice(0, separator) : ''
    const value = separator > 0 ? line.slice(separator + 1).trim() : ''
    const simple = preExamLines.find(([label]) => label === key)
    if (simple) {
      extras[simple[1]] = value
      continue
    }
    // A line that merely looks like one of ours stays in the free text, so older notes are never altered.
    const [answer, ...detail] = value.split(' ')
    const answered = answer === 'Y' || answer === 'N'
    const condition = key.startsWith('Hx ') ? systemicConditions.find((c) => c.code === key.slice(3)) : undefined
    if (condition && answered) {
      extras.systemic[condition.code] = { answer, years: detail.join(' ') }
    } else if (key === 'Allergy' && answered) {
      extras.allergy = answer
      extras.allergicTo = detail.join(' ')
    } else {
      rest.push(line)
    }
  }
  extras.measurements = rest.join('\n').trim()
  return extras
}

export function serializeMeasurements(extras: PreExamExtras) {
  const lines: string[] = []
  for (const [label, key] of preExamLines) {
    const value = extras[key].trim()
    if (value) lines.push(`${label}: ${value}`)
  }
  for (const { code } of systemicConditions) {
    const { answer, years } = extras.systemic[code]
    if (answer) lines.push(`Hx ${code}: ${[answer, answer === 'Y' ? years.trim() : ''].filter(Boolean).join(' ')}`)
  }
  if (extras.allergy) lines.push(`Allergy: ${[extras.allergy, extras.allergy === 'Y' ? extras.allergicTo.trim() : ''].filter(Boolean).join(' ')}`)
  if (extras.measurements.trim()) lines.push(extras.measurements.trim())
  return lines.join('\n')
}

/** Investigations from the paper file. Stored as labelled lines in the refraction's measurements column. */
export const investigationEyeRows = [
  { key: 'retinoscopy', code: 'Retinoscopy', label: 'Retinoscopy' },
  { key: 'colorVision', code: 'Color', label: 'Color vision' },
  { key: 'k1', code: 'K1', label: 'K1 (H)' },
  { key: 'k2', code: 'K2', label: 'K2 (V)' },
  { key: 'al', code: 'AL', label: 'AL' },
  { key: 'iol', code: 'IOL', label: 'IOL' },
] as const

export type InvestigationKey = (typeof investigationEyeRows)[number]['key']

export interface RefractionExtras {
  od: Record<InvestigationKey, string>
  os: Record<InvestigationKey, string>
  rbs: string
  jcc: string
  lens: string[]
  measurements: string
}

const blankInvestigations = () => Object.fromEntries(investigationEyeRows.map((r) => [r.key, ''])) as Record<InvestigationKey, string>

export function parseRefractionExtras(text: string | null | undefined): RefractionExtras {
  const extras: RefractionExtras = { od: blankInvestigations(), os: blankInvestigations(), rbs: '', jcc: '', lens: [], measurements: '' }
  const rest: string[] = []
  for (const line of (text ?? '').split('\n')) {
    const separator = line.indexOf(':')
    const key = separator > 0 ? line.slice(0, separator) : ''
    const value = separator > 0 ? line.slice(separator + 1).trim() : ''
    const eyeLine = key.match(/^([A-Za-z0-9]+) (OD|OS)$/)
    const row = eyeLine ? investigationEyeRows.find((r) => r.code === eyeLine[1]) : undefined
    if (row && eyeLine) extras[eyeLine[2] === 'OD' ? 'od' : 'os'][row.key] = value
    else if (key === 'RBS') extras.rbs = value
    else if (key === 'JCC') extras.jcc = value
    else if (key === 'Lens' && isLensLine(value)) extras.lens = parseLens(value)
    else rest.push(line)
  }
  extras.measurements = rest.join('\n').trim()
  return extras
}

export function serializeRefractionExtras(extras: RefractionExtras) {
  const lines: string[] = []
  for (const row of investigationEyeRows) {
    if (extras.od[row.key].trim()) lines.push(`${row.code} OD: ${extras.od[row.key].trim()}`)
    if (extras.os[row.key].trim()) lines.push(`${row.code} OS: ${extras.os[row.key].trim()}`)
  }
  if (extras.rbs.trim()) lines.push(`RBS: ${extras.rbs.trim()}`)
  if (extras.jcc.trim()) lines.push(`JCC: ${extras.jcc.trim()}`)
  if (extras.lens.length) lines.push(`Lens: ${extras.lens.join(', ')}`)
  if (extras.measurements.trim()) lines.push(extras.measurements.trim())
  return lines.join('\n')
}

/** Lens choices exactly as the glasses slip offers them. */
export const lensOptions = ['ph-Brown', 'Ph-Gray', 'White', 'Distance', 'Near', 'Bifocal', 'Progressive', 'Antireflex', 'Plastic']

const parseLens = (value: string) => value.split(',').map((v) => v.trim()).filter((v) => lensOptions.includes(v))
/** Anything else after "Lens:" is someone's free text, not a choice, and is left where it was written. */
const isLensLine = (value: string) => value.split(',').every((v) => lensOptions.includes(v.trim()))

export interface OpticalExtras {
  lens: string[]
  vaOd: string
  vaOs: string
  uvaOd: string
  uvaOs: string
  notes: string
}

/** The glasses prescription has one notes column; lens type and the vision values travel in it as labelled lines. */
export function parseOpticalNotes(text: string | null | undefined): OpticalExtras {
  const extras: OpticalExtras = { lens: [], vaOd: '', vaOs: '', uvaOd: '', uvaOs: '', notes: '' }
  const rest: string[] = []
  for (const line of (text ?? '').split('\n')) {
    const match = line.match(/^(Lens|VA OD|VA OS|UVA OD|UVA OS):\s*(.*)$/)
    if (match?.[1] === 'Lens' && isLensLine(match[2])) extras.lens = parseLens(match[2])
    else if (match?.[1] === 'Lens') rest.push(line)
    else if (match?.[1] === 'VA OD') extras.vaOd = match[2].trim()
    else if (match?.[1] === 'VA OS') extras.vaOs = match[2].trim()
    else if (match?.[1] === 'UVA OD') extras.uvaOd = match[2].trim()
    else if (match?.[1] === 'UVA OS') extras.uvaOs = match[2].trim()
    else rest.push(line)
  }
  extras.notes = rest.join('\n').trim()
  return extras
}

export function serializeOpticalNotes(extras: OpticalExtras) {
  const lines: string[] = []
  if (extras.lens.length) lines.push(`Lens: ${extras.lens.join(', ')}`)
  if (extras.uvaOd.trim()) lines.push(`UVA OD: ${extras.uvaOd.trim()}`)
  if (extras.uvaOs.trim()) lines.push(`UVA OS: ${extras.uvaOs.trim()}`)
  if (extras.vaOd.trim()) lines.push(`VA OD: ${extras.vaOd.trim()}`)
  if (extras.vaOs.trim()) lines.push(`VA OS: ${extras.vaOs.trim()}`)
  if (extras.notes.trim()) lines.push(extras.notes.trim())
  return lines.join('\n')
}

const persianDigits = '۰۱۲۳۴۵۶۷۸۹'
const arabicDigits = '٠١٢٣٤٥٦٧٨٩'

/** Latin digits and a plain minus, so a value typed on a Dari keyboard reads the same everywhere. */
export function normalizeDigits(value: string) {
  return value
    .replace(/[۰-۹]/g, (d) => String(persianDigits.indexOf(d)))
    .replace(/[٠-٩]/g, (d) => String(arabicDigits.indexOf(d)))
    .replace(/[−–—]/g, '-')
    .replace(/٫/g, '.')
    .trim()
}

/** Tidies a signed dioptre to two decimals. An unsigned value stays as typed: the sign is never guessed. */
export function formatDioptre(value: string) {
  const clean = normalizeDigits(value)
  const match = clean.match(/^([+-])(\d+(?:\.\d*)?)$/)
  return match ? `${match[1]}${Number(match[2]).toFixed(2)}` : clean
}

export function dioptreWarning(value: string | null | undefined) {
  const clean = normalizeDigits(value ?? '')
  if (!clean || /^[+-]?0*(\.0*)?$/.test(clean)) return ''
  if (/^\d/.test(clean)) return 'علامت + یا − را مشخص کنید.'
  return /^[+-]\d+(\.\d+)?$/.test(clean) ? '' : 'مقدار عددی معتبر نیست.'
}

export function axisWarning(value: string | null | undefined) {
  const clean = normalizeDigits(value ?? '')
  if (!clean) return ''
  const axis = Number(clean)
  return Number.isInteger(axis) && axis >= 0 && axis <= 180 ? '' : 'محور باید عددی از ۰ تا ۱۸۰ باشد.'
}
