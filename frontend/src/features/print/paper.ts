import { nextTick, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { isoToShamsi } from '@/utils/date'

export interface Hospital {
  hospitalName: string | null
  address: string | null
  phone: string | null
  printFooter: string | null
  logoAttachmentId: number | null
}

/** Clinic details come only from Settings. Nothing is copied from the scanned forms, whose names and numbers differ from each other. */
export function useHospital() {
  const hospital = ref<Hospital | null>(null)
  const load = async () => {
    hospital.value = await api<Hospital>('/settings').catch(() => null)
  }
  return { hospital, load }
}

/** Shamsi date in Latin digits, as the printed clinical forms use. */
export function shamsiPaper(value: string | null | undefined) {
  if (!value) return ''
  const iso = value.length > 10 ? new Date(value).toLocaleDateString('en-CA', { timeZone: 'Asia/Kabul' }) : value
  const { year, month, day } = isoToShamsi(iso)
  return `${year}/${String(month).padStart(2, '0')}/${String(day).padStart(2, '0')}`
}

export interface Clip {
  field: string
  shown: number
  total: number
}

/**
 * A paper form has fixed boxes. Text that does not fit is cut with a visible mark and listed for the user,
 * never dropped silently or shrunk until unreadable. The full text always stays in the digital record.
 */
export function useClipper() {
  const clipped = ref<Clip[]>([])
  function clip(field: string, text: string | null | undefined, limit: number) {
    const value = (text ?? '').trim()
    if (value.length <= limit) return value
    if (!clipped.value.some((c) => c.field === field)) clipped.value.push({ field, shown: limit, total: value.length })
    return `${value.slice(0, limit).trimEnd()} …*`
  }
  return { clipped, clip }
}

export function usePaperPage<T>(fetcher: () => Promise<T>) {
  const data = ref<T | null>(null)
  const error = ref('')
  const loading = ref(true)
  async function load() {
    loading.value = true
    try {
      data.value = await fetcher()
    } catch (cause) {
      error.value = errorMessage(cause)
    } finally {
      loading.value = false
    }
  }
  return { data, error, loading, load }
}

export function printPaper() {
  window.print()
}

/** A fixed-size sheet hides what overflows, so its last lines (often the signature) would vanish on paper without any warning. */
export async function sheetOverflows(selector: string) {
  await nextTick()
  await document.fonts?.ready
  const sheet = document.querySelector<HTMLElement>(selector)
  return !!sheet && sheet.scrollHeight > sheet.clientHeight + 1
}
