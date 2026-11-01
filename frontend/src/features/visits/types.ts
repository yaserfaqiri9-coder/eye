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
  status: string
  reason: string | null
  visitDate: string
  createdAt: string
  version: number
  nextStatuses: string[]
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
  AwaitingDiagnostic: 'منتظر آزمایش',
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
