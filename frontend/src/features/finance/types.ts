export interface InvoiceRow {
  id: number
  invoiceNumber: string
  patientId: number | null
  patientFileNumber: string | null
  patientName: string | null
  visitId: number | null
  status: string
  total: number
  discount: number
  paid: number
  refunded: number
  balance: number
  createdAt: string
  version: number
}

export interface InvoiceItemRow {
  id: number
  description: string
  quantity: number
  unitPrice: number
  lineTotal: number
  isVoid: boolean
}

export interface PaymentRow {
  id: number
  receiptNumber: string
  invoiceId: number
  invoiceNumber: string
  amount: number
  method: string
  reference: string | null
  refunded: number
  createdAt: string
  receivedBy: string | null
}

export interface InvoiceDetail {
  invoice: InvoiceRow
  items: InvoiceItemRow[]
  payments: PaymentRow[]
  discounts: { details: string | null; byUser: string | null; createdAt: string }[]
}

export interface SessionRow {
  id: number
  registerId: number
  registerName: string
  openedBy: string | null
  openingAmount: number
  openedAt: string
  closedAt: string | null
  expectedAmount: number | null
  countedAmount: number | null
  cashCollected: number
  version: number
}

export interface ServiceRow {
  id: number
  code: string
  name: string
  departmentName: string | null
  isActive: boolean
  currentPrice: number | null
}

export const invoiceStatusLabels: Record<string, string> = {
  Issued: 'پرداخت‌نشده',
  PartiallyPaid: 'پرداخت ناقص',
  Paid: 'پرداخت‌شده',
  Void: 'باطل',
}

export const invoiceStatusTones: Record<string, string> = {
  Issued: 'orange',
  PartiallyPaid: 'cyan',
  Paid: 'green',
  Void: 'red',
}
