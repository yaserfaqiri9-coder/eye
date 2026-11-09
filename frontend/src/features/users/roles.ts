export const roles = [
  { value: 'SuperAdmin', label: 'مدیر سیستم' },
  { value: 'Manager', label: 'مدیر شفاخانه' },
  { value: 'Receptionist', label: 'پذیرش' },
  { value: 'Triage', label: 'معاینه ابتدایی' },
  { value: 'Doctor', label: 'داکتر چشم' },
  { value: 'Lab', label: 'آزمایشگاه' },
  { value: 'Surgery', label: 'عملیات' },
  { value: 'Pharmacist', label: 'دواخانه' },
  { value: 'Optical', label: 'عینک' },
  { value: 'Cashier', label: 'صندوق‌دار' },
  { value: 'Accountant', label: 'حسابدار' },
  { value: 'Inventory', label: 'مسئول گدام' },
  { value: 'AudioStation', label: 'مرکز پخش صوت' },
  { value: 'DoctorReportsViewer', label: 'بیننده گزارش‌ها (آنلاین)' },
]

export function roleLabel(value: string) {
  return roles.find((role) => role.value === value)?.label ?? value
}
