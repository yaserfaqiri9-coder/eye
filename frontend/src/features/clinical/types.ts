import type { VisitRow } from '@/features/visits/types'

export interface PreExam {
  id: number
  visitId: number
  chiefComplaint: string | null
  history: string | null
  visualAcuityOd: string | null
  visualAcuityOs: string | null
  iopOd: string | null
  iopOs: string | null
  measurements: string | null
  notes: string | null
  recordedAt: string
  completedAt: string | null
  version: number
}

export type RefractionStage = 'Current' | 'Auto' | 'Subjective' | 'Final'

export interface RefractionLine {
  stage: RefractionStage
  eye: 'OD' | 'OS'
  sphere: string | null
  cylinder: string | null
  axis: string | null
  add: string | null
  va: string | null
}

export interface Refraction {
  id: number
  visitId: number
  visitDate: string
  unaidedVaOd: string | null
  unaidedVaOs: string | null
  pinholeVaOd: string | null
  pinholeVaOs: string | null
  nearVaOd: string | null
  nearVaOs: string | null
  pd: string | null
  measurements: string | null
  notes: string | null
  recordedAt: string
  completedAt: string | null
  version: number
  lines: RefractionLine[]
}

export const refractionStageLabels: Record<RefractionStage, string> = {
  Current: 'عینک فعلی (present GL)',
  Auto: 'Auto Refraction (AR، نتیجه دستگاه)',
  Subjective: 'Subjective Refraction (ST، اندازه‌گیری متخصص)',
  Final: 'Final Refraction (نسخه نهایی)',
}

export interface Diagnosis {
  eye: string
  text: string
}

export interface Consultation {
  id: number
  visitId: number
  findingsOd: string | null
  findingsOs: string | null
  treatmentPlan: string | null
  confidentialNote: string | null
  diagnoses: Diagnosis[]
  finalizedAt: string | null
  version: number
}

export interface Amendment {
  field: string
  oldValue: string | null
  newValue: string | null
  reason: string
  byUser: string | null
  createdAt: string
}

export interface RxItem {
  id: number
  drugName: string
  dose: string | null
  route: string | null
  duration: string | null
  instructions: string | null
  quantity: number
  dispensedQuantity: number
}

export interface Prescription {
  id: number
  rxNumber: string
  patientId: number
  patientFileNumber: string
  patientName: string
  visitId: number
  notes: string | null
  status: string
  createdAt: string
  version: number
  items: RxItem[]
}

export interface OpticalRx {
  id: number
  rxNumber: string
  patientId: number
  patientFileNumber: string
  patientName: string
  visitId: number
  sphereOd: string | null
  cylinderOd: string | null
  axisOd: string | null
  addOd: string | null
  sphereOs: string | null
  cylinderOs: string | null
  axisOs: string | null
  addOs: string | null
  pd: string | null
  notes: string | null
  isCancelled: boolean
  createdAt: string
  version: number
}

export interface DiagnosticOrder {
  id: number
  orderNumber: string
  patientId: number
  patientFileNumber: string
  patientName: string
  visitId: number | null
  testId: number
  testName: string
  note: string | null
  status: string
  createdAt: string
  version: number
  resultText: string | null
  performedAt: string | null
  kind: string
  returnToDoctor: boolean
  /** Null for a lab test, which has no pay-first rule. */
  paid: boolean | null
}

export interface FollowUp {
  id: number
  dueDate: string
  note: string | null
}

export interface Chart {
  visit: VisitRow
  preExam: PreExam | null
  consultation: Consultation | null
  amendments: Amendment[]
  prescriptions: Prescription[]
  opticalPrescriptions: OpticalRx[]
  diagnosticOrders: DiagnosticOrder[]
  followUps: FollowUp[]
}

export interface HistoryItem {
  visitId: number
  visitDate: string
  departmentName: string
  doctorName: string | null
  treatmentPlan: string | null
  diagnoses: Diagnosis[]
}

export interface SurgeryRow {
  id: number
  caseNumber: string
  patientId: number
  patientFileNumber: string
  patientName: string
  visitId: number
  surgeryType: string
  eye: string
  scheduledAt: string | null
  status: string
  version: number
  nextStatuses: string[]
}

export interface SurgeryDetail {
  case: SurgeryRow
  preOpChecks: string | null
  consentRecorded: boolean
  operationReport: string | null
  postOpStatus: string | null
}

export const eyeLabels: Record<string, string> = { None: '—', OD: 'راست (OD)', OS: 'چپ (OS)', Both: 'هر دو' }

export const rxStatusLabels: Record<string, string> = {
  Issued: 'صادرشده',
  PartiallyDispensed: 'تحویل ناقص',
  Dispensed: 'تحویل‌شده',
  Cancelled: 'لغو شده',
}

export const diagnosticStatusLabels: Record<string, string> = {
  Requested: 'درخواست‌شده',
  InProgress: 'در حال انجام',
  ResultReady: 'نتیجه آماده',
  Cancelled: 'لغو شده',
}

export const diagnosticStatusTones: Record<string, string> = {
  Requested: 'orange',
  InProgress: 'cyan',
  ResultReady: 'green',
  Cancelled: 'red',
}

export const surgeryStatusLabels: Record<string, string> = {
  Proposed: 'پیشنهادشده',
  Scheduled: 'برنامه‌ریزی‌شده',
  InProgress: 'در حال انجام',
  Completed: 'انجام‌شده',
  FollowUp: 'پیگیری',
  Closed: 'بسته',
  Cancelled: 'لغو شده',
}
