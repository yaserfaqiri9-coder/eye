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
