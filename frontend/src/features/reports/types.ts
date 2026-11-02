export interface OperationsReport {
  from: string
  to: string
  visits: number
  completedVisits: number
  visitsByDepartment: { name: string; count: number }[]
  doctors: { doctor: string; visits: number; finalized: number }[]
  diagnosticsByTest: { name: string; count: number }[]
  surgeries: number
  prescriptions: number
  opticalOrders: number
  lowStockProducts: number
}

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
}

export interface FinancialReport {
  from: string
  to: string
  serviceRevenue: number
  pharmacyRevenue: number
  opticalRevenue: number
  discounts: number
  refunds: number
  netRevenue: number
  costOfGoods: number
  expenses: number
  receipts: number
  receiptsByCashier: { cashier: string; total: number }[]
  outstandingNow: number
  openInvoicesNow: number
  stockValueNow: number
}
