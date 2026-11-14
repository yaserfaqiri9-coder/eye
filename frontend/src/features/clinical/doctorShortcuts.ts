/**
 * Typing aids for the prescription on the dossier, taken from the hospital's previous paper-form system.
 * Nothing here is a rule: the doctor can always write something else.
 */

/** Doses as the old list wrote them ("1x3" = one drop three times a day). */
export const doseOptions = ['1x1', '1x2', '1x3', '1x4', '1x6']

export interface RecentDrug {
  name: string
  dose: string
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

export function rememberDrugs(userId: number | undefined, items: { drugName: string; dose: string; quantity: number }[]) {
  if (!userId) return
  const list = recentDrugs(userId)
  for (const item of items) {
    const name = item.drugName.trim()
    if (!name) continue
    const found = list.find((d) => d.name.toLowerCase() === name.toLowerCase())
    if (found) Object.assign(found, { dose: item.dose, quantity: item.quantity, count: found.count + 1 })
    else list.push({ name, dose: item.dose, quantity: item.quantity, count: 1 })
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
]
