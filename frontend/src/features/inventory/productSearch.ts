import { api } from '@/api/http'
import type { ComboOption } from '@/components/UiComboBox.vue'
import { money, quantity } from '@/utils/money'
import { drugFormOf } from './drugForms'

export interface ProductOption {
  id: number
  name: string
  kind: string
  /** For a drug, its dosage form (تابلیت، قطره چشمی …); for other products a free category. */
  category?: string | null
  salePrice: number
  onHand: number
  unit: string
  isActive: boolean
}

/** Stock and price change all day, so they are read from the server each time and never cached. */
export function productSearch(kinds: string[], remember: (rows: ProductOption[]) => void, withStock = true) {
  return async (query: string, signal: AbortSignal): Promise<ComboOption[]> => {
    const params = new URLSearchParams({ pageSize: '30' })
    if (query.trim()) params.set('q', query.trim())
    if (kinds.length === 1) params.set('kind', kinds[0])
    const page = await api<{ items: ProductOption[] }>(`/inventory/products?${params}`, { signal })
    const rows = page.items.filter((p) => p.isActive && (!kinds.length || kinds.includes(p.kind)))
    remember(rows)
    return rows.map((p) => {
      // The drug form and the stock unit are shown apart: «قطره چشمی · موجودی: ۱۲ دانه».
      const form = drugFormOf(p)
      const parts = [form ? `نوعیت: ${form}` : null, withStock ? `موجودی: ${quantity(p.onHand)} ${p.unit}` : null, withStock ? money(p.salePrice) : null]
      const hint = parts.filter(Boolean).join(' · ')
      return { value: p.id, label: p.name, hint: hint || undefined }
    })
  }
}
