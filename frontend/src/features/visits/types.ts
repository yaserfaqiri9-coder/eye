export interface VisitRow {
  id: number
  visitNumber: string
  queueNumber: number
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
}

export interface VisitToken {
  id: number
  queueNumber: number
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

export interface ReceptionFee {
  invoiceId: number
  invoiceNumber: string
  patientName: string | null
  patientFileNumber: string | null
  description: string
  total: number
  discount: number
  balance: number
  invoiceVersion: number
}

export interface LookupItem {
  id: number
  name: string
}

export const visitStatusLabels: Record<string, string> = {
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
