export interface MenuItem {
  label: string
  icon: string
  to: string
  permission?: string
}

export interface MenuSection {
  title: string
  items: MenuItem[]
}

/** Only pages that exist are listed; items a user lacks permission for are hidden by the sidebar. */
export const menu: MenuSection[] = [
  {
    title: 'داشبورد',
    items: [{ label: 'صفحه اصلی', icon: 'bi-house-door-fill', to: '/' }],
  },
  {
    title: 'مریضان',
    items: [
      { label: 'پذیرش و صف', icon: 'bi-clipboard2-pulse-fill', to: '/reception', permission: 'visits.view' },
      { label: 'مریضان', icon: 'bi-person-vcard-fill', to: '/patients', permission: 'patients.view' },
    ],
  },
  {
    title: 'بخش‌های طبی',
    items: [
      { label: 'معاینه ابتدایی', icon: 'bi-eyeglasses', to: '/triage', permission: 'preexam.edit' },
      { label: 'صف داکتر', icon: 'bi-eye-fill', to: '/doctor', permission: 'consultation.edit' },
      { label: 'آزمایشگاه', icon: 'bi-eyedropper', to: '/lab', permission: 'diagnostics.edit' },
      { label: 'عملیات', icon: 'bi-bandaid-fill', to: '/surgery', permission: 'surgery.view' },
    ],
  },
  {
    title: 'دواخانه و عینک',
    items: [
      { label: 'دواخانه', icon: 'bi-capsule', to: '/pharmacy', permission: 'pharmacy.create' },
      { label: 'عینک', icon: 'bi-sunglasses', to: '/optical', permission: 'optical.view' },
      { label: 'گدام', icon: 'bi-box-seam-fill', to: '/inventory', permission: 'inventory.view' },
    ],
  },
  {
    title: 'مالی',
    items: [
      { label: 'صندوق', icon: 'bi-cash-coin', to: '/cashier', permission: 'payments.create' },
      { label: 'حسابداری', icon: 'bi-journal-text', to: '/accounting', permission: 'invoices.view' },
    ],
  },
  {
    title: 'مدیریت',
    items: [
      { label: 'گزارش‌ها', icon: 'bi-clipboard-data-fill', to: '/reports', permission: 'reports.view' },
      { label: 'تنظیمات', icon: 'bi-gear-fill', to: '/settings', permission: 'settings.view' },
      { label: 'کارمندان', icon: 'bi-person-badge-fill', to: '/staff', permission: 'users.view' },
      { label: 'کاربران', icon: 'bi-people-fill', to: '/users', permission: 'users.view' },
      { label: 'ثبت فعالیت‌ها', icon: 'bi-shield-lock-fill', to: '/audit', permission: 'audit.view' },
    ],
  },
]
