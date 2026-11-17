import { isoToShamsi, shamsiMonthLength, shamsiToIso, shamsiWeekColumn, todayIso } from '@/utils/date'

export type PeriodKind = 'today' | 'yesterday' | 'week' | 'month' | 'last-month' | 'custom'
export type QuickKind = Exclude<PeriodKind, 'custom'>

export const periodChoices: { kind: PeriodKind; label: string }[] = [
  { kind: 'today', label: 'امروز' },
  { kind: 'yesterday', label: 'دیروز' },
  { kind: 'week', label: 'این هفته' },
  { kind: 'month', label: 'این ماه' },
  { kind: 'last-month', label: 'ماه گذشته' },
  { kind: 'custom', label: 'دوره دلخواه' },
]

export interface DateRange {
  from: string
  to: string
}

export function addDays(iso: string, days: number) {
  const date = new Date(`${iso}T00:00:00Z`)
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

/** The same number of days just before a range, so a period is always compared with one of equal length. */
export function previousRange(range: DateRange): DateRange {
  const days = (Date.parse(`${range.to}T00:00:00Z`) - Date.parse(`${range.from}T00:00:00Z`)) / 86_400_000 + 1
  const to = addDays(range.from, -1)
  return { from: addDays(to, -(days - 1)), to }
}

/** The dates of a quick choice, on the Kabul calendar. A running week or month ends today, so no empty future days are reported. */
export function quickRange(kind: QuickKind, today = todayIso()): DateRange {
  const { year, month, day } = isoToShamsi(today)
  switch (kind) {
    case 'today':
      return { from: today, to: today }
    case 'yesterday': {
      const yesterday = addDays(today, -1)
      return { from: yesterday, to: yesterday }
    }
    case 'week':
      return { from: addDays(today, -shamsiWeekColumn(year, month, day)), to: today }
    case 'month':
      return { from: shamsiToIso(year, month, 1), to: today }
    case 'last-month': {
      const lastYear = month === 1 ? year - 1 : year
      const last = month === 1 ? 12 : month - 1
      return { from: shamsiToIso(lastYear, last, 1), to: shamsiToIso(lastYear, last, shamsiMonthLength(lastYear, last)) }
    }
  }
}

/** Which quick choice a range is, or custom when it matches none. */
export function kindOf(range: DateRange, today = todayIso()): PeriodKind {
  for (const { kind } of periodChoices) {
    if (kind === 'custom') continue
    const quick = quickRange(kind, today)
    if (quick.from === range.from && quick.to === range.to) return kind
  }
  return 'custom'
}
