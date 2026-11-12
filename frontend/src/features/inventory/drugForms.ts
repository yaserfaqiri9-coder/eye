import { api } from '@/api/http'

/**
 * Dosage forms offered for a drug. The form lives in the product's Category, where the opening stock import put it.
 * It is not the stock unit (دانه، پاکت، قطی، بکس) and not the product group (دوا، عمومی).
 */
export const standardDrugForms = ['تابلیت', 'کپسول', 'شربت', 'آمپول/تزریقی', 'ویال', 'قطره چشمی', 'پماد', 'سرم', 'محلول']

/** Common counting units, offered as suggestions only; any other unit may still be typed. */
export const stockUnits = ['دانه', 'عدد', 'پاکت', 'قطی', 'بکس', 'بوتل', 'جوره']

/** The product group, which is a different thing from the drug form. */
export const productGroupLabels: Record<string, string> = { Drug: 'دوا', Frame: 'فریم', Lens: 'عدسی', General: 'عمومی' }

/** The drug form to show for a product, or null when the product is not a drug. */
export function drugFormOf(row: { kind: string; category?: string | null }) {
  return row.kind === 'Drug' ? row.category || null : null
}

/** Standard forms first, then the forms already stored (imported values such as «پماد چشمی» are kept), then the current one. */
export function drugFormChoices(stored: string[], current: string) {
  const choices = [...standardDrugForms]
  for (const form of [...stored, current]) {
    const value = form.trim()
    if (value && !choices.includes(value)) choices.push(value)
  }
  return choices
}

export async function loadStoredDrugForms() {
  return api<string[]>('/inventory/drug-forms')
}
