import { shamsiPaper } from '@/features/print/paper'
import { formatTime, latinDigits, todayIso } from '@/utils/date'
import { isDateText, isNumericKind, periodWords } from './reportFormat'
import type { ReportCell, ReportDocument, ReportValueKind } from './types'

const BOM = '﻿'
const LINE = '\r\n'

/** Excel runs a cell that starts like a formula, and patient names and reasons are free text anyone can type. */
export function neutralise(text: string) {
  return /^[=+\-@\t\r]/.test(text) ? `'${text}` : text
}

function quote(field: string) {
  return /[",\r\n]/.test(field) ? `"${field.replaceAll('"', '""')}"` : field
}

const textField = (text: string) => quote(neutralise(text))

function cellField(value: ReportCell, kind: ReportValueKind) {
  if (value === null) return ''
  // Only a real number is written bare, so that a negative amount stays a number and nothing else can pass as one.
  if (typeof value === 'number' && isNumericKind(kind)) return String(value)
  if (typeof value === 'string' && isDateText(value)) {
    if (kind === 'date') return shamsiPaper(value)
    if (kind === 'datetime') return `${shamsiPaper(value)} ${latinDigits(formatTime(value))}`
  }
  return textField(String(value))
}

export function buildCsv(doc: ReportDocument) {
  const lines: string[] = [textField(doc.title), textField(periodWords(doc.from, doc.to)), '']

  for (const tile of doc.tiles) lines.push(`${textField(tile.label)},${cellField(tile.value, tile.kind)}`)

  for (const table of doc.tables) {
    lines.push('', textField(table.title), table.columns.map((column) => textField(column.label)).join(','))
    const row = (cells: ReportCell[]) => cells.map((cell, index) => cellField(cell, table.columns[index]?.kind ?? 'text')).join(',')
    for (const cells of table.rows) lines.push(row(cells))
    if (table.total) lines.push(row(table.total))
    if (table.note) lines.push(textField(table.note))
  }

  return BOM + lines.join(LINE) + LINE
}

/** A safe ASCII name whatever the report holds, such as report-patients-1405-01-01.csv. */
export function csvFileName(key: string, doc: ReportDocument) {
  return `report-${key}-${shamsiPaper(doc.from ?? todayIso()).replaceAll('/', '-')}.csv`
}

export function downloadCsv(key: string, doc: ReportDocument) {
  const url = URL.createObjectURL(new Blob([buildCsv(doc)], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = csvFileName(key, doc)
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
