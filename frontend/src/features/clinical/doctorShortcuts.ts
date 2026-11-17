/**
 * Typing aids for the prescription on the dossier, taken from the hospital's previous paper-form system.
 * Nothing here is a rule: the doctor can always write something else.
 */

/** Doses as the old list wrote them ("1x3" = one drop three times a day). */
export const doseOptions = ['1x1', '1x2', '1x3', '1x4', '1x6']

/** A pharmacy drug as the prescription shows it: brand, form, (generic) and the dose usually written. */
export interface DrugOption {
  name: string
  form: string | null
  generic?: string | null
  defaultDose?: string | null
  /** From the old system's list, not the pharmacy: the pharmacy cannot give it from stock. */
  outside?: boolean
}

/** The English word the old prescriptions put before a drug, from the pharmacy's form. Same table as the printed prescription. */
const prescriptionForms: Record<string, string> = {
  'قطره چشمی': 'Eye drop',
  پماد: 'Eye ointment',
  'پماد چشمی': 'Eye ointment',
  تابلیت: 'Tablet',
  قرص: 'Tablet',
  کپسول: 'Capsule',
  شربت: 'Syrup',
  'شربت/سوسپانسیون خوراکی': 'Syrup',
  'قطره خوراکی': 'Oral drop',
  ویال: 'Vial',
  'آمپول/تزریقی': 'Injection',
  'قرص جوشان': 'Effervescent tablet',
  لوشن: 'Lotion',
  شامپو: 'Shampoo',
  سرم: 'Infusion',
  سیروم: 'Infusion',
}

/** "Eye drop" for «قطره چشمی»; the stored form itself when the old list had no word for it. */
export function prescriptionFormOf(form: string | null | undefined) {
  const value = form?.trim()
  return value ? (prescriptionForms[value] ?? value) : null
}

/** How long a drug is used, as offered beside the dose. */
export const durationOptions = ['3 days', '5 days', '1 week', '10 days', '2 weeks', '3 weeks', '1 month', '2 months', '3 months', 'Continue']

/** Common eye diagnoses offered while typing one. Any other text can still be written. */
export const commonDiagnoses = [
  'Refractive error', 'Myopia', 'Hypermetropia', 'Astigmatism', 'Presbyopia', 'Amblyopia',
  'Allergic conjunctivitis', 'Bacterial conjunctivitis', 'Viral conjunctivitis', 'Vernal keratoconjunctivitis',
  'Dry eye', 'Blepharitis', 'Chalazion', 'Stye (Hordeolum)', 'Pterygium', 'Pinguecula', 'Subconjunctival hemorrhage',
  'Corneal ulcer', 'Corneal abrasion', 'Corneal foreign body', 'Keratitis', 'Keratoconus', 'Corneal opacity',
  'Senile cataract', 'Immature cataract', 'Mature cataract', 'Posterior capsular opacification', 'Pseudophakia', 'Aphakia',
  'Primary open angle glaucoma', 'Angle closure glaucoma', 'Glaucoma suspect', 'Ocular hypertension',
  'Anterior uveitis', 'Posterior uveitis', 'Diabetic retinopathy', 'Hypertensive retinopathy', 'ARMD',
  'Retinal detachment', 'Vitreous hemorrhage', 'Central retinal vein occlusion', 'Optic atrophy', 'Papilledema',
  'Strabismus', 'Esotropia', 'Exotropia', 'Ptosis', 'Nasolacrimal duct obstruction', 'Dacryocystitis', 'Trauma',
]

/**
 * The words of what the patient is allergic to that also appear in a drug's name, if any.
 * Plain matching on words of three letters or more: it warns, the doctor decides.
 */
export function allergyMatch(allergy: string | null | undefined, drugName: string) {
  const name = drugName.toLowerCase()
  return (allergy ?? '')
    .toLowerCase()
    .split(/[\s,،؛;/+()-]+/)
    .filter((word) => word.length >= 3 && name.includes(word))
}

export interface RecentDrug {
  name: string
  dose: string
  /** Missing in lists saved before the duration was written on the dossier. */
  duration?: string
  quantity: number
  count: number
}

const LIMIT = 60
const drugsKey = (userId: number) => `alshifa.doctor.${userId}.drugs`

/** Browser storage can be missing or blocked; the page works the same without it. */
function read<T>(key: string): T[] {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? '[]')
    return Array.isArray(value) ? value : []
  } catch {
    return []
  }
}

/** The doctor's own drugs, each with the dose and quantity they last wrote. */
export function recentDrugs(userId: number | undefined): RecentDrug[] {
  if (!userId) return []
  return read<RecentDrug>(drugsKey(userId)).filter((d) => typeof d?.name === 'string' && d.name.trim())
}

export function rememberDrugs(userId: number | undefined, items: { drugName: string; dose: string; duration: string; quantity: number }[]) {
  if (!userId) return
  const list = recentDrugs(userId)
  for (const item of items) {
    const name = item.drugName.trim()
    if (!name) continue
    const found = list.find((d) => d.name.toLowerCase() === name.toLowerCase())
    if (found) Object.assign(found, { dose: item.dose, duration: item.duration, quantity: item.quantity, count: found.count + 1 })
    else list.push({ name, dose: item.dose, duration: item.duration, quantity: item.quantity, count: 1 })
  }
  try {
    localStorage.setItem(drugsKey(userId), JSON.stringify(list.sort((a, b) => b.count - a.count).slice(0, LIMIT)))
  } catch {
    // Only a convenience: losing it changes nothing on the record.
  }
}

const brandOf = (line: string) =>
  line
    .replace(/^(Eye drop|Eye ointment|Tablet|Syrup|Capsule|Vial|Injection)\s+/i, '')
    .split(/[\s(]/)[0]
    .toLowerCase()

/**
 * The usual dose the old list gave a drug, found by its brand name (the first word of the pharmacy's name).
 * "Vigamox (Moxifloxacin) 0.5%" finds "Eye drop Vigamox (Moxifloxacin) 1x3" and returns "1x3".
 */
export function paperDose(drugName: string) {
  const brand = drugName.trim().split(/[\s(]/)[0].toLowerCase()
  if (brand.length < 3) return ''
  const line = paperDrugList.find((entry) => brandOf(entry) === brand)
  return line?.match(/(\d+x\d+)\s*$/)?.[1] ?? ''
}
/** The previous system's drug list, as its doctors wrote each line: form, brand, (generic) and the usual dose. */
export const paperDrugList = [
  'Eye drop Optipred (Prednisolone) 1x3',
  'Eye drop Barvante (Prednisolone) 1x3',
  'Eye drop Prems (Prednisolone) 1x3',
  'Eye drop Medidex (Dexamethasone) 1x3',
  'Eye drop Ophth-Dex (Dexamethasone) 1x3',
  'Eye drop Cortane (Prednisolone) 1x3',
  'Eye drop BFL (Fluorometholone) 1x3',
  'Eye drop OcuFlu (Fluorometholone) 1x3',
  'Eye ointment Betamethasone (Betamethasone) 1x1',
  'Eye ointment Bethisol-N (Betamethasone + Neomycin) 1x1',
  'Eye ointment Hydrocortisone (Hydrocortisone) 1x1',
  'Eye ointment Cortispirin (Hydrocortisone) 1x1',
  'Eye drop Lenside (Potassium iodide + Sodium iodide) 1x3',
  'Eye drop Sodium Chloride 1x3',
  'Eye drop Homatropine 2% 1x3',
  'Eye drop Atropine 1% 1x3',
  'Eye drop Virocid (Trifluridine 1%) 1x3',
  'Eye ointment Sentover (Acyclovir 2%) 1x1',
  'Eye ointment Virolyse (Acyclovir 2%) 1x1',
  'Tablet Vision Healthy',
  'Tablet Opticare',
  'Tablet Surlex-2',
  'Tablet Multi-Power',
  'Tablet Neurobion (B1 + B6 + B12)',
  'Syrup Acebex',
  'Syrup Vit-A',
  'Capsule Omega-3',
  'Syrup Vitkal',
  'Syrup Kalvital',
  'Syrup Calcivit-Pro-T',
  'Syrup Vitatook',
  'Tablet Multivitamin Eff.',
  'Tablet Vitamin C Eff.',
  'Tablet Vitamin C',
  'Eye drop Vigamox (Moxifloxacin) 1x3',
  'Eye drop Intobac (Tobramycin) 1x3',
  'Eye drop Levoxin (Levofloxacin) 1x3',
  'Eye drop Natasan (Natamycin) 1x3',
  'Eye drop Megamox (Moxifloxacin) 1x3',
  'Eye drop Medimox (Moxifloxacin) 1x3',
  'Eye drop Moxquin (Moxifloxacin) 1x3',
  'Eye drop Santuchlor (Chloramphenicol) 1x3',
  'Eye drop Optiflox (Ofloxacin) 1x3',
  'Eye drop Moxigan (Moxifloxacin) 1x3',
  'Eye drop Ciof (Ofloxacin) 1x3',
  'Eye drop Moximed (Moxifloxacin) 1x3',
  'Eye drop Oflumed (Ofloxacin) 1x3',
  'Eye ointment Tobrex (Tobramycin) 1x1',
  'Eye ointment Optiflox (Ofloxacin) 1x1',
  'Eye ointment Tetracyclin (Tetracycline) 1x1',
  'Eye ointment Erythromycin (Erythromycin) 1x1',
  'Eye drop Xalatan (Latanoprost) 1x2',
  'Eye drop Xalacom (Latanoprost + Timolol) 1x2',
  'Eye drop Travatan (Travoprost) 1x2',
  'Eye drop Brimodin (Brimonidine) 1x2',
  'Eye drop Alphagan (Brimonidine) 1x2',
  'Eye drop Co-Dorzal (Dorzolamide + Timolol) 1x2',
  'Eye drop Cosopt (Dorzolamide + Timolol) 1x2',
  'Eye drop Beta-Sar (Levobunolol) 1x2',
  'Eye drop Timozol (Dorzolamide + Timolol) 1x2',
  'Eye drop Signigan (Dorzolamide + Timolol) 1x2',
  'Eye drop Pilocarpine 2% (Pilocarpine) 1x2',
  'Eye drop Pilocarpine 4% (Pilocarpine) 1x2',
  'Eye drop Brimodin Plus (Brimonidine + Timolol) 1x2',
  'Eye drop Timolol (Timolol) 1x2',
  'Eye drop Zilomole (Dorzolamide + Timolol) 1x2',
  'Eye drop Dorzamide (Dorzolamide) 1x2',
  'Eye drop Tobradex (Tobramycin + Dexamethasone) 1x3',
  'Eye drop Eyebradex (Tobramycin + Dexamethasone) 1x3',
  'Eye drop Orbidex-T (Tobramycin + Dexamethasone) 1x3',
  'Eye drop Pextobac (Tobramycin + Dexamethasone) 1x3',
  'Eye drop Dexaflox (Ofloxacin + Dexamethasone) 1x3',
  'Eye drop Comox (Moxifloxacin + Dexamethasone) 1x3',
  'Eye drop Prednicol (Prednisolone + Chloramphenicol) 1x3',
  'Eye drop Metachlor (Dexamethasone + Chloramphenicol) 1x3',
  'Eye drop Blephapred (Prednisolone + Sulphacetamide) 1x3',
  'Eye ointment Metachlorex (Dexamethasone + Chloramphenicol) 1x1',
  'Eye ointment Eyebradex (Tobramycin + Dexamethasone) 1x1',
  'Eye ointment Santodex (Tobramycin + Dexamethasone) 1x1',
  'Eye ointment Ophth-Tob-D (Tobramycin + Dexamethasone) 1x1',
  'Eye ointment Blephapred (Prednisolone + Sulphacetamide) 1x1',
  'Eye drop Avanep (Nepafenac) 1x3',
  'Eye drop Nepac (Nepafenac) 1x3',
  'Eye drop Fenap (Nepafenac) 1x3',
  'Eye drop Nevanac (Nepafenac) 1x3',
  'Eye drop Nepamil (Nepafenac) 1x3',
  'Eye drop Etrovac (Ketorolac) 1x3',
  'Eye drop Diclomin (Diclofenac) 1x3',
  'Vial Ceftriaxone 1 g (Ceftriaxone)',
  'Vial Ceftriaxone 500 mg (Ceftriaxone)',
  'Injection Gentamycin 80 mg / 2 ml (Gentamycin)',
  'Tablet Cefgel 400 mg (Cefpodoxime)',
  'Tablet Cefgel 200 mg (Cefpodoxime)',
  'Tablet Augmentin 625 mg (Amoxicillin + Clavulanic acid)',
  'Tablet Augmentin 375 mg (Amoxicillin + Clavulanic acid)',
  'Tablet Chymoral 250 mg (Trypsin + Chymotrypsin)',
  'Tablet Loprin 75 mg (Aspirin low dose)',
  'Tablet Prednisolone 5 mg (Prednisolone)',
  'Tablet Prednisolone 10 mg (Prednisolone)',
  'Tablet Prednisolone 20 mg (Prednisolone)',
  'Capsule Sefixim (Cefixime)',
  'Tablet Vit B1 300 mg (Thiamine)',
  'Tablet Calergot (Ergotamine)',
  'Tablet Lorazepam 1 mg (Lorazepam)',
  'Tablet Lorazepam 2 mg (Lorazepam)',
  'Tablet Azithromycin 500 mg (Azithromycin)',
  'Tablet Azithromycin 250 mg (Azithromycin)',
  'Eye ointment Tearfix Gel (Lubricant) 1x1',
  'Eye ointment Barilube (Lubricant) 1x1',
  'Eye ointment Vit-H (Vitamin A + ?) 1x1',
  'Eye drop Recuva-PF (Lubricant) 1x3',
  'Eye drop Optanol (Olopatadine) 1x3',
  'Eye drop Xepat (Olopatadine) 1x3',
  'Eye drop Curine (Naphazoline) 1x3',
  'Eye drop Ketotifine (Ketotifen) 1x3',
  'Eye drop Neprivine (Naphazoline) 1x3',
  'Eye drop Cuvall-H (Antazoline + Naphazoline) 1x3',
  'Eye drop Ocuzol (Antazoline + Naphazoline) 1x3',
  'Eye drop Fluorozil (Fluorometholone + Sodium Cromoglycate) 1x3',
  'Eye drop Vegacrom (Sodium Cromoglycate + Tetrahydrozoline) 1x3',
  'Tablet Cetirizine (Cetirizine)',
  'Syrup Cetirizine (Cetirizine)',
  'Tablet Ebastine (Ebastine)',
  'Tablet Fexofenadine 60 mg (Fexofenadine)',
  'Tablet Fexofenadine 120 mg (Fexofenadine)',
  // Written on the old system's prescriptions (backup of 2026-11-14) but missing from its list above.
  'Eye drop Maxidex (Dexamethasone) 1x3',
  'Eye drop FML (Fluorometholone) 1x3',
  'Eye drop Pred Fort (Prednisolone) 1x3',
  'Eye drop Dex-Med (Dexamethasone) 1x3',
  'Eye drop Olomed (Olopatadine) 1x3',
  'Eye drop Salatan (Latanoprost) 1x2',
  'Eye drop Beta-bar (Levobunolol) 1x2',
  'Eye drop Cuvall-A (Antazoline + Naphazoline) 1x3',
  'Eye drop Haxami 1x3',
  'Eye drop Afamin 1x3',
  'Eye drop Tear Plus 1x3',
  'Eye drop Full Fresh 1x3',
  'Eye drop Tobrax',
  'Eye drop Vegamox',
  'Eye ointment Optaveir (Acyclovir 2%)',
  'Tablet Surbex T 1x1',
  'Tablet Ibuprofen 400 mg (Ibuprofen) 1x2',
  'Tablet Acetazolamide 250 mg (Acetazolamide)',
  'Tablet Acyclovir 400 mg (Acyclovir)',
  'Tablet Parol 500 mg',
  'Capsule Indometacin 25 mg (Indometacin)',
  'Capsule Omeprazol 40 mg (Omeprazole) 1x1',
  'Capsule Sibelium 5 mg 1x1',
  'Injection Methylprednisolone (Methylprednisolone)',
]

/** The old list's English form word, for the badge beside a drug that is not in the pharmacy. */
const paperForms: Record<string, string> = {
  'eye drop': 'قطره چشمی',
  'eye ointment': 'پماد چشمی',
  tablet: 'تابلیت',
  capsule: 'کپسول',
  syrup: 'شربت',
  vial: 'ویال',
  injection: 'آمپول/تزریقی',
}

/** "Eye drop Tobradex (Tobramycin + Dexamethasone) 1x3" as a drug: brand Tobradex, form, generic and dose. */
export function paperDrug(line: string): DrugOption {
  const formWord = line.match(/^(Eye drop|Eye ointment|Tablet|Syrup|Capsule|Vial|Injection)\s+/i)
  let rest = formWord ? line.slice(formWord[0].length) : line
  const dose = rest.match(/\s(\d+x\d+)\s*$/)
  if (dose) rest = rest.slice(0, dose.index).trim()
  const generic = rest.match(/\(([^)]*)\)\s*$/)
  const name = (generic ? rest.slice(0, generic.index) : rest).trim()
  return { name, form: formWord ? paperForms[formWord[1].toLowerCase()] ?? null : null, generic: generic?.[1].trim() || null, defaultDose: dose?.[1] ?? null, outside: true }
}

/**
 * Drugs of the old list that match the typed text and are not already among the pharmacy's answers,
 * so a doctor still finds every drug the old system offered. They are written on the prescription as named here.
 */
export function paperMatches(term: string, pharmacy: DrugOption[], limit = 10): DrugOption[] {
  const text = term.trim().toLowerCase()
  if (text.length < 2) return []
  const brands = new Set(pharmacy.map((d) => brandOf(d.name)))
  const seen = new Set<string>()
  const found: DrugOption[] = []
  for (const line of paperDrugList) {
    if (!line.toLowerCase().includes(text)) continue
    const drug = paperDrug(line)
    const key = drug.name.toLowerCase()
    if (!drug.name || brands.has(brandOf(drug.name)) || seen.has(key)) continue
    seen.add(key)
    found.push(drug)
    if (found.length >= limit) break
  }
  return found
}
