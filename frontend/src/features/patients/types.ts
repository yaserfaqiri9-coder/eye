export interface PatientListItem {
  id: number
  fileNumber: string
  firstName: string
  lastName: string | null
  fatherName: string | null
  gender: string
  ageYears: number | null
  phone: string | null
}

export interface Patient extends PatientListItem {
  birthDate: string | null
  estimatedBirthYear: number | null
  address: string | null
  createdAt: string
  version: number
}

export const genderLabels: Record<string, string> = { Male: 'مرد', Female: 'زن', Other: 'دیگر' }

export function patientName(p: { firstName: string; lastName: string | null }) {
  return [p.firstName, p.lastName].filter(Boolean).join(' ')
}
