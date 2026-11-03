import { api } from '@/api/http'
import type { ComboOption } from '@/components/UiComboBox.vue'
import { money, quantity } from '@/utils/money'

export interface ProductOption {
  id: number
  name: string
  kind: string
  salePrice: number
  onHand: number
  unit: string
}

/** Stock and price change all day, so they are read from the server each time and never cached. */
export function productSearch(kinds: string[], remember: (rows: ProductOption[]) => void, withStock = true) {
  return async (query: string, signal: AbortSignal): Promise<ComboOption[]> => {
    const params = new URLSearchParams({ pageSize: '30' })
    if (query) params.set('q', query)
    if (kinds.length === 1) params.set('kind', kinds[0])
    const page = await api<{ items: ProductOption[] }>(`/inventory/products?${params}`, { signal })
    const rows = kinds.length ? page.items.filter((p) => kinds.includes(p.kind)) : page.items
    remember(rows)
    return rows.map((p) => ({
      value: p.id,
      label: p.name,
      hint: withStock ? `موجودی: ${quantity(p.onHand)} ${p.unit} · ${money(p.salePrice)}` : undefined,
    }))
  }
}
