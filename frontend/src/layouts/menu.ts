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
    items: [{ label: 'صفحه اصلی', icon: 'ri-home-4-fill', to: '/' }],
  },
  {
    title: 'مریضان',
    items: [
      { label: 'پذیرش و صف', icon: 'ri-survey-fill', to: '/reception', permission: 'visits.view' },
      { label: 'مریضان', icon: 'ri-user-heart-fill', to: '/patients', permission: 'patients.view' },
    ],
  },
  {
    title: 'بخش‌های طبی',
    items: [
      { label: 'معاینه ابتدایی', icon: 'ri-glasses-2-fill', to: '/triage', permission: 'preexam.edit' },
      { label: 'صف داکتر', icon: 'ri-eye-fill', to: '/doctor', permission: 'consultation.edit' },
      { label: 'آزمایشگاه', icon: 'ri-test-tube-fill', to: '/lab', permission: 'diagnostics.edit' },
      { label: 'عملیات', icon: 'ri-surgical-mask-fill', to: '/surgery', permission: 'surgery.view' },
    ],
  },
  {
    title: 'دواخانه و عینک',
    items: [
      { label: 'دواخانه', icon: 'ri-capsule-fill', to: '/pharmacy', permission: 'pharmacy.create' },
      { label: 'عینک', icon: 'ri-glasses-fill', to: '/optical', permission: 'optical.view' },
      { label: 'گدام', icon: 'ri-archive-fill', to: '/inventory', permission: 'inventory.view' },
    ],
  },
  {
    title: 'مالی',
    items: [
      { label: 'صندوق', icon: 'ri-money-dollar-circle-fill', to: '/cashier', permission: 'payments.create' },
      { label: 'حسابداری', icon: 'ri-book-2-fill', to: '/accounting', permission: 'invoices.view' },
    ],
  },
  {
    title: 'مدیریت',
    items: [
      { label: 'گزارش‌ها', icon: 'ri-file-chart-fill', to: '/reports', permission: 'reports.view' },
      { label: 'تنظیمات', icon: 'ri-settings-3-fill', to: '/settings', permission: 'settings.view' },
      { label: 'کارمندان', icon: 'ri-nurse-fill', to: '/staff', permission: 'users.view' },
      { label: 'کاربران', icon: 'ri-group-fill', to: '/users', permission: 'users.view' },
      { label: 'ثبت فعالیت‌ها', icon: 'ri-shield-keyhole-fill', to: '/audit', permission: 'audit.view' },
    ],
  },
]
