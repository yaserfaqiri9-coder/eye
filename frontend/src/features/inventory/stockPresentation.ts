import { productGroupLabels } from './drugForms'

export type StockStatus = 'available' | 'low' | 'out'
export interface InventoryListRow {
  id: number
  name: string
  code: string
  kind: string
  category?: string | null
  quantity: number
  unit?: string
  reorderLevel?: number | null
  locations: { code: string; name: string }[]
  isActive?: boolean
}

export const stockStatusLabels: Record<StockStatus, string> = { available: 'موجود', low: 'موجودی کم', out: 'ناموجود' }
export function stockStatus(row: Pick<InventoryListRow, 'quantity' | 'reorderLevel'>): StockStatus {
  if (row.quantity <= 0) return 'out'
  return row.reorderLevel != null && row.reorderLevel > 0 && row.quantity <= row.reorderLevel ? 'low' : 'available'
}
// The reference uses Latin digits, no leading zeroes, and right-aligned tabular quantities.
const digits = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3, useGrouping: true })
export const stockNumber = (value: number) => digits.format(value)
export const stockCategory = (row: InventoryListRow) => row.category || productGroupLabels[row.kind] || row.kind
export const stockSearchText = (text: string) => text.toLowerCase().replace(/ي/g, 'ی').replace(/ك/g, 'ک').replace(/\s+/g, ' ').trim()

/** Small filled product silhouettes, with the same monochrome treatment as the reference. */
export function stockIcon(row: Pick<InventoryListRow, 'kind' | 'category' | 'name'>) {
  const text = stockSearchText(`${row.category || ''} ${row.name}`)
  if (/headphone/.test(text)) return 'ri-headphone-fill'
  if (/watch/.test(text)) return 'ri-time-fill'
  if (/keyboard/.test(text)) return 'ri-keyboard-fill'
  if (/mouse/.test(text)) return 'ri-mouse-fill'
  if (row.kind === 'Frame') return 'ri-glasses-fill'
  if (row.kind === 'Lens') return 'ri-focus-2-fill'
  if (/قطره|محلول|drop|solution/.test(text)) return 'ri-contrast-drop-fill'
  if (/آمپول|تزریق|سرم|injection|syringe/.test(text)) return 'ri-syringe-fill'
  if (/شربت|بوتل|ویال|پماد|syrup|vial|ointment/.test(text)) return 'ri-medicine-bottle-fill'
  if (row.kind === 'Drug') return 'ri-capsule-fill'
  return 'ri-box-3-fill'
}
