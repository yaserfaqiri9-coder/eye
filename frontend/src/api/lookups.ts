import { api } from './http'
import type { ComboOption } from '@/components/UiComboBox.vue'

const TTL_MS = 60_000

const cache = new Map<string, { at: number; value: Promise<unknown> }>()

/**
 * Short-lived cache for reference lists that rarely change, shared by every form that opens during a shift.
 * Prices, stock and clinical data must not go through here: they are always read fresh.
 */
function cached<T>(path: string) {
  const hit = cache.get(path)
  if (hit && Date.now() - hit.at < TTL_MS) return hit.value as Promise<T>
  const value = api<T>(path)
  cache.set(path, { at: Date.now(), value })
  value.catch(() => cache.delete(path))
  return value
}

/** Called on sign-in and sign-out, so one user's lists are never served to the next. */
export function clearLookups() {
  cache.clear()
}

interface LookupRow {
  id: number
  name: string
}

const toOptions = (rows: LookupRow[]): ComboOption[] => rows.map((row) => ({ value: row.id, label: row.name }))

export const departmentOptions = () => cached<LookupRow[]>('/lookups/departments').then(toOptions)

export const doctorOptions = () => cached<LookupRow[]>('/lookups/doctors').then(toOptions)

export const testOptions = () =>
  cached<(LookupRow & { isActive: boolean; kind: string })[]>('/diagnostic-tests').then((rows) =>
    rows.filter((t) => t.isActive).map((t) => ({ value: t.id, label: t.name, kind: t.kind })),
  )

/** Departments and doctors are worth warming before the first form needs them. */
export function warmLookups(can: (permission: string) => boolean) {
  if (!can('visits.create') && !can('appointments.create')) return
  departmentOptions().catch(() => {})
  doctorOptions().catch(() => {})
}
