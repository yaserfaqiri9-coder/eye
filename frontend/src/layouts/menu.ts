export interface MenuItem {
  label: string
  icon: string
  to: string
  permission?: string
  /** Extra route names that belong to this navigation flow but do not share its URL prefix. */
  activeOn?: string[]
}

/** A top-level row that opens a submenu instead of going to a page. */
export interface MenuParent {
  label: string
  icon: string
  children: MenuItem[]
}

export type MenuEntry = MenuItem | MenuParent

export interface MenuSection {
  title: string
  items: MenuEntry[]
}

export function isMenuParent(entry: MenuEntry): entry is MenuParent {
  return 'children' in entry
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
    title: 'بخش‌ها',
    items: [
      {
        label: 'بخش‌های طبی',
        icon: 'ri-stethoscope-fill',
        children: [
          { label: 'معاینه ابتدایی', icon: 'ri-glasses-2-fill', to: '/triage', permission: 'preexam.edit' },
          { label: 'بینایی‌سنجی', icon: 'ri-contrast-2-fill', to: '/refraction', permission: 'preexam.edit' },
          {
            label: 'صف داکتر',
            icon: 'ri-eye-fill',
            to: '/doctor',
            permission: 'consultation.edit',
            activeOn: ['chart'],
          },
          { label: 'آزمایشگاه', icon: 'ri-test-tube-fill', to: '/lab', permission: 'diagnostics.edit' },
          { label: 'عملیات', icon: 'ri-surgical-mask-fill', to: '/surgery', permission: 'surgery.view' },
        ],
      },
      {
        label: 'دواخانه و عینک',
        icon: 'ri-medicine-bottle-fill',
        children: [
          { label: 'دواخانه', icon: 'ri-capsule-fill', to: '/pharmacy', permission: 'pharmacy.create' },
          { label: 'عینک', icon: 'ri-glasses-fill', to: '/optical', permission: 'optical.view' },
          { label: 'گدام', icon: 'ri-archive-fill', to: '/inventory', permission: 'inventory.view' },
        ],
      },
      {
        label: 'مالی',
        icon: 'ri-wallet-3-fill',
        children: [
          { label: 'صندوق', icon: 'ri-money-dollar-circle-fill', to: '/cashier', permission: 'payments.create' },
          { label: 'حسابداری', icon: 'ri-book-2-fill', to: '/accounting', permission: 'invoices.view' },
        ],
      },
    ],
  },
  {
    title: 'مدیریت',
    items: [
      { label: 'گزارش‌ها', icon: 'ri-file-chart-fill', to: '/reports', permission: 'reports.view' },
      {
        label: 'مدیریت سازمان',
        icon: 'ri-building-2-fill',
        children: [
          { label: 'فهرست داکتران', icon: 'ri-stethoscope-fill', to: '/doctors', permission: 'users.view' },
          { label: 'مدیریت بخش‌ها', icon: 'ri-hospital-fill', to: '/departments', permission: 'settings.view' },
          { label: 'تأمین‌کنندگان', icon: 'ri-truck-fill', to: '/suppliers', permission: 'inventory.view' },
          { label: 'کارمندان', icon: 'ri-nurse-fill', to: '/staff', permission: 'users.view' },
        ],
      },
      {
        label: 'مدیریت سیستم',
        icon: 'ri-settings-4-fill',
        children: [
          { label: 'مرکز پخش صوت', icon: 'ri-volume-up-fill', to: '/audio-station', permission: 'calls.manage' },
          { label: 'تنظیمات', icon: 'ri-settings-3-fill', to: '/settings', permission: 'settings.view' },
          { label: 'کاربران', icon: 'ri-group-fill', to: '/users', permission: 'users.view' },
          { label: 'ثبت فعالیت‌ها', icon: 'ri-shield-keyhole-fill', to: '/audit', permission: 'audit.view' },
        ],
      },
    ],
  },
]
