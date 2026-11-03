// Page loaders in one place, so the router and the role-based prefetch share the same chunks.
export const pages = {
  home: () => import('@/features/home/HomePage.vue'),
  reception: () => import('@/features/visits/ReceptionPage.vue'),
  patients: () => import('@/features/patients/PatientsPage.vue'),
  patient: () => import('@/features/patients/PatientDetailPage.vue'),
  patientForm: () => import('@/features/patients/PatientFormPage.vue'),
  triage: () => import('@/features/clinical/TriagePage.vue'),
  triageExam: () => import('@/features/clinical/TriageExamPage.vue'),
  refractionQueue: () => import('@/features/clinical/RefractionQueuePage.vue'),
  refraction: () => import('@/features/clinical/RefractionPage.vue'),
  doctor: () => import('@/features/clinical/DoctorPage.vue'),
  chart: () => import('@/features/clinical/ChartPage.vue'),
  lab: () => import('@/features/clinical/LabPage.vue'),
  labResult: () => import('@/features/clinical/LabResultPage.vue'),
  surgery: () => import('@/features/clinical/SurgeryPage.vue'),
  surgeryCase: () => import('@/features/clinical/SurgeryCasePage.vue'),
  pharmacy: () => import('@/features/inventory/PharmacyPage.vue'),
  pharmacySale: () => import('@/features/inventory/PharmacySalePage.vue'),
  optical: () => import('@/features/inventory/OpticalPage.vue'),
  opticalOrder: () => import('@/features/inventory/OpticalOrderPage.vue'),
  inventory: () => import('@/features/inventory/InventoryPage.vue'),
  purchase: () => import('@/features/inventory/PurchasePage.vue'),
  cashier: () => import('@/features/finance/CashierPage.vue'),
  invoiceForm: () => import('@/features/finance/InvoiceFormPage.vue'),
  accounting: () => import('@/features/finance/AccountingPage.vue'),
  reports: () => import('@/features/reports/ReportsPage.vue'),
  settings: () => import('@/features/settings/SettingsPage.vue'),
  audit: () => import('@/features/users/AuditPage.vue'),
  audioStation: () => import('@/features/calls/AudioStationPage.vue'),
  directories: () => import('@/features/directories/DirectoryPage.vue'),
  doctors: () => import('@/features/users/DoctorsPage.vue'),
  staff: () => import('@/features/users/StaffPage.vue'),
  users: () => import('@/features/users/UsersPage.vue'),
}

/** The pages each role opens all day. Their code is fetched while idle, so the first click is as fast as the rest. */
const rolePages: Record<string, (keyof typeof pages)[]> = {
  Receptionist: ['reception', 'patients', 'patient', 'patientForm'],
  Triage: ['triage', 'triageExam', 'refractionQueue', 'refraction'],
  Doctor: ['doctor', 'chart', 'refraction', 'patient'],
  Lab: ['lab', 'labResult'],
  Surgery: ['surgery', 'surgeryCase', 'chart'],
  Pharmacist: ['pharmacy', 'pharmacySale'],
  Optical: ['optical', 'opticalOrder'],
  Cashier: ['cashier', 'invoiceForm'],
  Accountant: ['accounting'],
  Inventory: ['inventory', 'purchase'],
  Manager: ['reports', 'patients', 'patient'],
}

export function prefetchForRoles(roles: string[]) {
  const wanted = new Set(roles.flatMap((role) => rolePages[role] ?? []))
  const load = () => wanted.forEach((name) => void pages[name]().catch(() => {}))
  // A failed prefetch costs nothing: the page simply loads on the click, as usual.
  if ('requestIdleCallback' in window) window.requestIdleCallback(load, { timeout: 3000 })
  else setTimeout(load, 1000)
}
