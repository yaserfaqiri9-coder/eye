const format = new Intl.NumberFormat('fa-AF', { maximumFractionDigits: 2 })

/** Currency label stays fixed until the finance lead confirms currencies and rounding (WORKFLOWS.md F-1). */
export function money(value: number | null | undefined) {
  return value === null || value === undefined ? '—' : `${format.format(value)} افغانی`
}

export function quantity(value: number) {
  return format.format(value)
}
