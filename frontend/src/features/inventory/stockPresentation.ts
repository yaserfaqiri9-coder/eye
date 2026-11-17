import { productGroupLabels } from './drugForms'

export type StockStatus = 'available' | 'low' | 'out'
export interface InventoryListRow {
  id: number
  name: string
  code: string
  kind: string
  category?: string | null
  /** For a drug, its active ingredients ("Tobramycin + Dexamethasone"). */
  genericName?: string | null
  quantity: number
  unit?: string
  reorderLevel?: number | null
  locations: { code: string; name: string }[]
  isActive?: boolean
  /** Average purchase price of the stock on hand. */
  purchasePrice?: number | null
  salePrice?: number | null
  nearestExpiry?: string | null
  batchCount?: number
}

export const stockStatusLabels: Record<StockStatus, string> = { available: 'موجود', low: 'موجودی کم', out: 'ناموجود' }
export const stockStatusTones: Record<StockStatus, string> = { available: 'green', low: 'orange', out: 'red' }
export function stockStatus(row: Pick<InventoryListRow, 'quantity' | 'reorderLevel'>): StockStatus {
  if (row.quantity <= 0) return 'out'
  return row.reorderLevel != null && row.reorderLevel > 0 && row.quantity <= row.reorderLevel ? 'low' : 'available'
}
export const stockCategory = (row: InventoryListRow) => row.category || productGroupLabels[row.kind] || row.kind
export const stockSearchText = (text: string) => text.toLowerCase().replace(/ي/g, 'ی').replace(/ك/g, 'ک').replace(/\s+/g, ' ').trim()

/** A small icon for the kind of product: drop, syringe, bottle, capsule, glasses, lens or box. */
export function stockIcon(row: Pick<InventoryListRow, 'kind' | 'category' | 'name'>) {
  const text = stockSearchText(`${row.category || ''} ${row.name}`)
  if (row.kind === 'Frame') return 'ri-glasses-fill'
  if (row.kind === 'Lens') return 'ri-focus-2-fill'
  if (/قطره|محلول|drop|solution/.test(text)) return 'ri-contrast-drop-fill'
  if (/آمپول|تزریق|سرم|injection|syringe/.test(text)) return 'ri-syringe-fill'
  if (/شربت|بوتل|ویال|پماد|syrup|vial|ointment/.test(text)) return 'ri-medicine-bottle-fill'
  if (row.kind === 'Drug') return 'ri-capsule-fill'
  return 'ri-box-3-fill'
}

/** Days until a batch expires; negative once it has expired, null when it has no expiry date. */
export function expiryDays(date: string | null | undefined) {
  return date ? Math.floor((new Date(date).getTime() - Date.now()) / 86_400_000) : null
}

/** An expired batch is red, one ending within 90 days orange. */
export function expiryTone(date: string | null | undefined) {
  const days = expiryDays(date)
  return days == null ? '' : days < 0 ? 'xr-badge xr-badge--red' : days <= 90 ? 'xr-badge xr-badge--orange' : ''
}
