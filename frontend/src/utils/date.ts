const ZONE = 'Asia/Kabul'

export const afghanMonths = ['حمل', 'ثور', 'جوزا', 'سرطان', 'اسد', 'سنبله', 'میزان', 'عقرب', 'قوس', 'جدی', 'دلو', 'حوت']

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

export function todayShamsi() {
  const parts = new Intl.DateTimeFormat('en-u-ca-persian-nu-latn', { timeZone: ZONE, year: 'numeric', month: 'numeric', day: 'numeric' }).formatToParts(new Date())
  const get = (type: string) => Number(parts.find((p) => p.type === type)!.value)
  return { year: get('year'), month: get('month'), day: get('day') }
}

export function shamsiMonthLength(year: number, month: number) {
  if (month <= 6) return 31
  if (month <= 11) return 30
  return isShamsiLeap(year) ? 30 : 29
}

function isShamsiLeap(year: number) {
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

/** Combines a Kabul calendar date and HH:mm time into a UTC timestamp. */
export function kabulToUtc(isoDate: string, time: string) {
  return new Date(`${isoDate}T${time}:00+04:30`).toISOString()
}
