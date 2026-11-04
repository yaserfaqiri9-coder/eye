import { isoToShamsi, shamsiMonthLength, shamsiToIso, shamsiWeekColumn, todayIso } from '@/utils/date'

export interface ReportEntry {
  key: string
  name: string
  /** Any one of these opens the report. */
  needs: string[]
  /** Answers for the present moment, so it has no period to choose. */
  noPeriod?: boolean
}

export interface ReportGroup {
  title: string
  reports: ReportEntry[]
}

export const reportGroups: ReportGroup[] = [
  {
    title: 'کار شفاخانه',
    reports: [
      { key: 'overview', name: 'خلاصه کار', needs: ['reports.view'] },
      { key: 'patients', name: 'مریضان و مراجعات', needs: ['reports.view'] },
      { key: 'doctors', name: 'کار داکتران', needs: ['reports.view'] },
      { key: 'diagnostics', name: 'آزمایش، رفرکشن و عکس‌برداری', needs: ['reports.view'] },
      { key: 'surgeries', name: 'عملیات', needs: ['reports.view'] },
      { key: 'diseases', name: 'بیماری‌های شایع', needs: ['reports.view'] },
    ],
  },
  {
    title: 'پول و حساب',
    reports: [
      { key: 'income', name: 'درآمد و مصارف', needs: ['ledger.view'] },
      { key: 'cash', name: 'صندوق و دریافت‌ها', needs: ['ledger.view'] },
      { key: 'outstanding', name: 'پول‌های گرفته‌نشده', needs: ['ledger.view'], noPeriod: true },
      { key: 'adjustments', name: 'تخفیف، برگشت وجه و باطل', needs: ['ledger.view'] },
      { key: 'expenses', name: 'مصارف', needs: ['ledger.view'] },
    ],
  },
  {
    title: 'دوا و گدام',
    reports: [
      { key: 'sales', name: 'فروش دواخانه و عینک', needs: ['pharmacy.view', 'optical.view'] },
      { key: 'stock', name: 'موجودی گدام', needs: ['inventory.view'], noPeriod: true },
    ],
  },
]

export type PeriodKind = 'today' | 'yesterday' | 'week' | 'month' | 'last-month' | 'custom'

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

function addDays(iso: string, days: number) {
  const date = new Date(`${iso}T00:00:00Z`)
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

/** The dates of a quick choice, on the Kabul calendar. A running week or month ends today, so no empty future days are reported. */
export function quickRange(kind: Exclude<PeriodKind, 'custom'>, today = todayIso()): DateRange {
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
