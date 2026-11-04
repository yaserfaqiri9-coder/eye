import { afghanMonths, faDigits, formatDate, formatDateTime, isoToShamsi } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import type { ReportCell, ReportValueKind } from './types'

export function isNumericKind(kind: ReportValueKind) {
  return kind === 'count' || kind === 'money' || kind === 'quantity'
}

/** A negative money value is a deduction. Its sign alone is easy to miss in a column, so it also gets its own colour. */
export function isDeduction(value: ReportCell, kind: ReportValueKind) {
  return kind === 'money' && typeof value === 'number' && value < 0
}

export function numberText(value: number, kind: ReportValueKind) {
  // The server can send a negated zero, which would otherwise show a minus sign.
  const plain = value === 0 ? 0 : value
  return kind === 'money' ? money(plain) : quantity(plain)
}

/** A total row writes «مجموع» under a date column, so a column's kind does not promise that a cell holds a date. */
export function isDateText(value: string) {
  return /^\d{4}-\d{2}-\d{2}/.test(value) && !Number.isNaN(Date.parse(value))
}

export function cellText(value: ReportCell, kind: ReportValueKind) {
  if (value === null || value === '') return '—'
  if (typeof value === 'number') return isNumericKind(kind) ? numberText(value, kind) : String(value)
  if (kind === 'date' && isDateText(value)) return formatDate(value)
  if (kind === 'datetime' && isDateText(value)) return formatDateTime(value)
  return value
}

/** A period in plain words, such as «۱ حمل تا ۳۱ حمل ۱۴۰۵». A report of the present moment has no dates. */
export function periodWords(from: string | null, to: string | null) {
  if (!from || !to) return 'وضعیت همین حالا'
  const start = isoToShamsi(from)
  const end = isoToShamsi(to)
  const dayMonth = (date: { day: number; month: number }) => `${faDigits(date.day)} ${afghanMonths[date.month - 1]}`
  const last = `${dayMonth(end)} ${faDigits(end.year)}`
  if (from === to) return last
  return `${dayMonth(start)}${start.year === end.year ? '' : ` ${faDigits(start.year)}`} تا ${last}`
}
