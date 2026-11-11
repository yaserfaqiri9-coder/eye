/** Shapes of /api/pharmacy: a prescription as the pharmacy sees it, with its bills and the patient's decisions. */

export interface PharmacyRxRow {
  id: number
  rxNumber: string
  patientId: number
  patientName: string
  fatherName: string | null
  fileNumber: string
  doctorName: string | null
  visitId: number
  createdAt: string
  validUntil: string | null
  status: string
  pharmacyStatus: string
  stage: string
  version: number
}

export interface PharmacyPatient {
  id: number
  fileNumber: string
  name: string
  fatherName: string | null
  phone: string | null
  gender: string
  age: number | null
}

export interface PharmacyPatientHit extends PharmacyPatient {
  prescriptionCount: number
  lastPrescriptionAt: string | null
}

export interface PharmacyRxItem {
  id: number
  drugName: string
  dose: string | null
  route: string | null
  duration: string | null
  instructions: string | null
  quantity: number
  dispensed: number
  billed: number
  remaining: number
  productId: number | null
  unit: string | null
  salePrice: number | null
  available: number
  /** Available, Short, Out or NotStocked. */
  stock: string
}

export interface PharmacyBill {
  id: number
  saleNumber: string
  invoiceNumber: string
  total: number
  balance: number
  paid: boolean
  isReturned: boolean
  createdAt: string
  deliveredAt: string | null
  deliveredBy: string | null
  unpaidDeliveryReason: string | null
  reservationExpired: boolean
  version: number
  lines: string[]
}

export interface PharmacyRxDetail {
  prescription: PharmacyRxRow
  notes: string | null
  expired: boolean
  patient: PharmacyPatient
  items: PharmacyRxItem[]
  bills: PharmacyBill[]
  events: { status: string; note: string | null; userName: string | null; createdAt: string }[]
}

export interface PharmacyQueueBill {
  saleId: number
  saleNumber: string
  prescriptionId: number | null
  rxNumber: string | null
  patientId: number | null
  patientName: string | null
  fileNumber: string | null
  invoiceNumber: string
  total: number
  balance: number
  createdAt: string
  reservationExpired: boolean
  version: number
}

export interface PharmacyQueues {
  new: PharmacyRxRow[]
  awaitingPayment: PharmacyQueueBill[]
  readyToDeliver: PharmacyQueueBill[]
  partial: PharmacyRxRow[]
  needsDoctor: PharmacyRxRow[]
}

export const stockLabels: Record<string, { text: string; tone: string }> = {
  Available: { text: 'موجود', tone: 'green' },
  Short: { text: 'کمتر از نسخه', tone: 'orange' },
  Out: { text: 'ناموجود', tone: 'red' },
  NotStocked: { text: 'در فهرست دواخانه نیست', tone: 'red' },
}

export const stageTone: Record<string, string> = {
  AwaitingVisit: 'primary',
  AwaitingDecision: 'cyan',
  AwaitingPayment: 'orange',
  ReadyToDeliver: 'green',
  Partial: 'orange',
  Delivered: 'green',
  BuyingOutside: 'primary',
  NotNow: 'primary',
  NeedsDoctorReview: 'red',
  Cancelled: 'red',
}
