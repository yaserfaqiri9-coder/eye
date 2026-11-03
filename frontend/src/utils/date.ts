const ZONE = 'Asia/Kabul'

export const afghanMonths = ['حمل', 'ثور', 'جوزا', 'سرطان', 'اسد', 'سنبله', 'میزان', 'عقرب', 'قوس', 'جدی', 'دلو', 'حوت']

/** Week columns as printed on Afghan calendars: Saturday first. */
export const weekdayNames = ['شنبه', 'یکشنبه', 'دوشنبه', 'سه‌شنبه', 'چهارشنبه', 'پنجشنبه', 'جمعه']

const dateFormat = new Intl.DateTimeFormat('fa-AF-u-ca-persian', { timeZone: ZONE, year: 'numeric', month: 'long', day: 'numeric' })
const dateTimeFormat = new Intl.DateTimeFormat('fa-AF-u-ca-persian', {
  timeZone: ZONE,
  year: 'numeric',
  month: 'long',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})
const timeFormat = new Intl.DateTimeFormat('fa-AF', { timeZone: ZONE, hour: '2-digit', minute: '2-digit', hour12: false })
const partsFormat = (timeZone: string) => new Intl.DateTimeFormat('en-u-ca-persian-nu-latn', { timeZone, year: 'numeric', month: 'numeric', day: 'numeric' })
const kabulParts = partsFormat(ZONE)
const utcParts = partsFormat('UTC')

/** Accepts a UTC timestamp or a yyyy-MM-dd calendar date. */
export function formatDate(value: string | null | undefined) {
  return value ? dateFormat.format(new Date(value)) : '—'
}

export function formatDateTime(value: string | null | undefined) {
  return value ? dateTimeFormat.format(new Date(value)) : '—'
}

export function formatTime(value: string) {
  return timeFormat.format(new Date(value))
}

export interface ShamsiDate {
  year: number
  month: number
  day: number
}

function readParts(format: Intl.DateTimeFormat, date: Date): ShamsiDate {
  const parts = format.formatToParts(date)
  const get = (type: string) => Number(parts.find((p) => p.type === type)!.value)
  return { year: get('year'), month: get('month'), day: get('day') }
}

export function todayShamsi() {
  return readParts(kabulParts, new Date())
}

/** Today in Kabul as yyyy-MM-dd, whatever zone this computer is set to. */
export function todayIso() {
  const today = todayShamsi()
  return shamsiToIso(today.year, today.month, today.day)
}

export function shamsiMonthLength(year: number, month: number) {
  if (month <= 6) return 31
  if (month <= 11) return 30
  return isShamsiLeap(year) ? 30 : 29
}

export function isShamsiLeap(year: number) {
  // The 33-year approximation of the arithmetic calendar, valid for the years this system handles.
  return [1, 5, 9, 13, 17, 22, 26, 30].includes(((year % 33) + 33) % 33)
}

/** Converts a Shamsi date to yyyy-MM-dd using the standard Jalali day-count algorithm. */
export function shamsiToIso(year: number, month: number, day: number) {
  const jy = year + 1595
  let days = -355668 + 365 * jy + Math.floor(jy / 33) * 8 + Math.floor(((jy % 33) + 3) / 4) + day + (month < 7 ? (month - 1) * 31 : (month - 7) * 30 + 186)
  let gy = 400 * Math.floor(days / 146097)
  days %= 146097
  if (days > 36524) {
    gy += 100 * Math.floor(--days / 36524)
    days %= 36524
    if (days >= 365) days++
  }
  gy += 4 * Math.floor(days / 1461)
  days %= 1461
  if (days > 365) {
    gy += Math.floor((days - 1) / 365)
    days = (days - 1) % 365
  }
  let gd = days + 1
  const leap = (gy % 4 === 0 && gy % 100 !== 0) || gy % 400 === 0
  const monthDays = [0, 31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
  let gm = 1
  while (gm <= 12 && gd > monthDays[gm]) {
    gd -= monthDays[gm]
    gm++
  }
  return `${gy}-${String(gm).padStart(2, '0')}-${String(gd).padStart(2, '0')}`
}

// A calendar date has no time of day: read at UTC midnight and formatted in UTC, it cannot shift by a day in any zone.
const atUtcMidnight = (iso: string) => new Date(`${iso.slice(0, 10)}T00:00:00Z`)

/** Converts a stored yyyy-MM-dd date to its Shamsi parts. */
export function isoToShamsi(iso: string): ShamsiDate {
  return readParts(utcParts, atUtcMidnight(iso))
}

/** The calendar column of a date: 0 for Saturday through 6 for Friday. */
export function shamsiWeekColumn(year: number, month: number, day: number) {
  return (atUtcMidnight(shamsiToIso(year, month, day)).getUTCDay() + 1) % 7
}

const persianDigits = '۰۱۲۳۴۵۶۷۸۹'
const arabicDigits = '٠١٢٣٤٥٦٧٨٩'

export function latinDigits(value: string) {
  return value.replace(/[۰-۹]/g, (d) => String(persianDigits.indexOf(d))).replace(/[٠-٩]/g, (d) => String(arabicDigits.indexOf(d)))
}

export function faDigits(value: string | number) {
  return String(value).replace(/\d/g, (d) => persianDigits[Number(d)])
}

/** yyyy-MM-dd as the digits a user reads and types, such as ۱۴۰۵/۰۸/۱۲. */
export function formatShamsi(iso: string | null | undefined) {
  if (!iso) return ''
  const { year, month, day } = isoToShamsi(iso)
  return faDigits(`${year}/${String(month).padStart(2, '0')}/${String(day).padStart(2, '0')}`)
}

/** Reads a typed Shamsi date in Dari or Latin digits. Returns yyyy-MM-dd, or null when no such day exists. */
export function parseShamsi(text: string) {
  const match = latinDigits(text.trim()).match(/^(\d{4})[/\-.](\d{1,2})[/\-.](\d{1,2})$/)
  if (!match) return null
  const [year, month, day] = match.slice(1).map(Number)
  if (month < 1 || month > 12 || day < 1 || day > shamsiMonthLength(year, month)) return null
  return shamsiToIso(year, month, day)
}

/** Combines a Kabul calendar date and HH:mm time into a UTC timestamp. */
export function kabulToUtc(isoDate: string, time: string) {
  return new Date(`${isoDate}T${time}:00+04:30`).toISOString()
}
