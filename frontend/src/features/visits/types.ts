export interface VisitRow {
  id: number
  visitNumber: string
  queueNumber: number | null
  patientId: number
  patientFileNumber: string
  patientName: string
  departmentId: number
  departmentName: string
  doctorName: string | null
  doctorStaffId?: number | null
  status: string
  reason: string | null
  visitDate: string
  createdAt: string
  version: number
  nextStatuses: string[]
  patientGender: string
  feeStatus: 'Paid' | 'Unpaid' | 'Refunded' | 'None'
}

export interface VisitToken {
  id: number
  queueNumber: number | null
  tokenDate: string
  departmentId: number
  departmentName: string
  doctorStaffId: number | null
  doctorName: string | null
  invoiceId: number
  invoiceNumber: string
  total: number
  discount: number
  balance: number
  invoiceVersion: number
  paid: boolean
  version: number
}

/** One visit at the desk: who, where it stands, what is owed and the number it earned. */
export interface Admission {
  visitId: number
  visitNumber: string
  queueNumber: number | null
  patientId: number
  patientFileNumber: string
  patientName: string
  departmentName: string
  doctorName: string | null
  status: string
  visitDate: string
  invoiceId: number
  invoiceNumber: string
  total: number
  discount: number
  balance: number
  invoiceVersion: number
  paid: boolean
  receiptNumber: string | null
}

export interface LookupItem {
  id: number
  name: string
}

export const visitStatusLabels: Record<string, string> = {
  AwaitingPayment: 'منتظر پرداخت',
  Registered: 'ثبت‌شده',
  Waiting: 'در انتظار',
  InPreExam: 'معاینه ابتدایی',
  ReadyForDoctor: 'آماده برای داکتر',
  InConsultation: 'نزد داکتر',
  AwaitingDiagnostic: 'منتظر رفرکشن یا عکس‌برداری',
  FollowUpRequired: 'نیازمند پیگیری',
  Completed: 'تکمیل',
  Cancelled: 'لغو شده',
}

export const visitStatusTones: Record<string, string> = {
  AwaitingPayment: 'red',
  Registered: 'cyan',
  Waiting: 'orange',
  InPreExam: 'primary',
  ReadyForDoctor: 'primary',
  InConsultation: 'primary',
  AwaitingDiagnostic: 'orange',
  FollowUpRequired: 'orange',
  Completed: 'green',
  Cancelled: 'red',
}

export const feeStatusLabels: Record<string, { label: string; tone: string }> = {
  Paid: { label: 'پرداخت‌شده', tone: 'green' },
  Unpaid: { label: 'پرداخت‌نشده', tone: 'red' },
  Refunded: { label: 'برگشت‌شده', tone: 'orange' },
}
