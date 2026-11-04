export interface DashboardDay {
  date: string
  completed: number
  active: number
  cancelled: number
  male: number
  female: number
}

export interface DashboardReport {
  today: string
  yesterdayVisits: number
  days: DashboardDay[]
  visitsByDepartment: { name: string; count: number }[]
  todayByStatus: { name: string; count: number }[]
  lowStockProducts: number
}

export interface FinancialReport {
  receipts: number
  expenses: number
  outstandingNow: number
}

export type ReportValueKind = 'count' | 'money' | 'quantity' | 'text' | 'date' | 'datetime'

export type ReportCell = string | number | null

export interface ReportTile {
  label: string
  value: number
  kind: ReportValueKind
}

export interface ReportColumn {
  label: string
  kind: ReportValueKind
}

export interface ReportTable {
  title: string
  columns: ReportColumn[]
  rows: ReportCell[][]
  total: ReportCell[] | null
  note: string | null
}

export interface ReportTrend {
  title: string
  kind: ReportValueKind
  points: { date: string; value: number }[]
}

/** One report in the single shape the page shows, prints and exports. From and to are null on a report of the present moment. */
export interface ReportDocument {
  title: string
  description: string
  from: string | null
  to: string | null
  tiles: ReportTile[]
  trend: ReportTrend | null
  tables: ReportTable[]
}
