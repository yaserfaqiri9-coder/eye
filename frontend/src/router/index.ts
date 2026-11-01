import { ref } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    public?: boolean
    permission?: string
    title?: string
  }
}

const appChildren: RouteRecordRaw[] = [
  {
    path: '',
    name: 'home',
    component: () => import('@/features/home/HomePage.vue'),
    meta: { title: 'صفحه اصلی' },
  },
  {
    path: 'reception',
    name: 'reception',
    component: () => import('@/features/visits/ReceptionPage.vue'),
    meta: { permission: 'visits.view', title: 'پذیرش' },
  },
  {
    path: 'patients',
    name: 'patients',
    component: () => import('@/features/patients/PatientsPage.vue'),
    meta: { permission: 'patients.view', title: 'مریضان' },
  },
  {
    path: 'patients/:id(\\d+)',
    name: 'patient',
    component: () => import('@/features/patients/PatientDetailPage.vue'),
    props: true,
    meta: { permission: 'patients.view', title: 'پرونده مریض' },
  },
  {
    path: 'triage',
    name: 'triage',
    component: () => import('@/features/clinical/TriagePage.vue'),
    meta: { permission: 'preexam.edit', title: 'معاینه ابتدایی' },
  },
  {
    path: 'doctor',
    name: 'doctor',
    component: () => import('@/features/clinical/DoctorPage.vue'),
    meta: { permission: 'consultation.edit', title: 'صف داکتر' },
  },
  {
    path: 'chart/:visitId(\\d+)',
    name: 'chart',
    component: () => import('@/features/clinical/ChartPage.vue'),
    props: true,
    meta: { permission: 'consultation.view', title: 'پرونده طبی' },
  },
  {
    path: 'lab',
    name: 'lab',
    component: () => import('@/features/clinical/LabPage.vue'),
    meta: { permission: 'diagnostics.edit', title: 'آزمایشگاه' },
  },
  {
    path: 'surgery',
    name: 'surgery',
    component: () => import('@/features/clinical/SurgeryPage.vue'),
    meta: { permission: 'surgery.view', title: 'عملیات' },
  },
  {
    path: 'pharmacy',
    name: 'pharmacy',
    component: () => import('@/features/inventory/PharmacyPage.vue'),
    meta: { permission: 'pharmacy.create', title: 'دواخانه' },
  },
  {
    path: 'optical',
    name: 'optical',
    component: () => import('@/features/inventory/OpticalPage.vue'),
    meta: { permission: 'optical.view', title: 'عینک' },
  },
  {
    path: 'inventory',
    name: 'inventory',
    component: () => import('@/features/inventory/InventoryPage.vue'),
    meta: { permission: 'inventory.view', title: 'گدام' },
  },
  {
    path: 'cashier',
    name: 'cashier',
    component: () => import('@/features/finance/CashierPage.vue'),
    meta: { permission: 'payments.create', title: 'صندوق' },
  },
  {
    path: 'accounting',
    name: 'accounting',
    component: () => import('@/features/finance/AccountingPage.vue'),
    meta: { permission: 'invoices.view', title: 'حسابداری' },
  },
  {
    path: 'reports',
    name: 'reports',
    component: () => import('@/features/reports/ReportsPage.vue'),
    meta: { permission: 'reports.view', title: 'گزارش‌ها' },
  },
  {
    path: 'settings',
    name: 'settings',
    component: () => import('@/features/settings/SettingsPage.vue'),
    meta: { permission: 'settings.view', title: 'تنظیمات' },
  },
  {
    path: 'audit',
    name: 'audit',
    component: () => import('@/features/users/AuditPage.vue'),
    meta: { permission: 'audit.view', title: 'ثبت فعالیت‌ها' },
  },
  {
    path: 'staff',
    name: 'staff',
    component: () => import('@/features/users/StaffPage.vue'),
    meta: { permission: 'users.view', title: 'کارمندان' },
  },
  {
    path: 'users',
    name: 'users',
    component: () => import('@/features/users/UsersPage.vue'),
    meta: { permission: 'users.view', title: 'کاربران' },
  },
  {
    path: ':pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/features/home/NotFoundPage.vue'),
    meta: { title: 'پیدا نشد' },
  },
]

// Design comparison page, excluded from production builds.
if (import.meta.env.DEV) {
  appChildren.unshift({
    path: '__design',
    component: () => import('@/features/design/DesignPreview.vue'),
    meta: { title: 'نمونه طراحی' },
  })
}

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/features/auth/LoginPage.vue'),
    meta: { public: true },
  },
  {
    // Outside the app layout so the page holds nothing but the document. The API enforces each document's permission.
    path: '/print/:kind/:id(\\d+)',
    name: 'print',
    component: () => import('@/features/print/PrintPage.vue'),
    props: true,
  },
  {
    path: '/',
    component: () => import('@/layouts/AppLayout.vue'),
    children: appChildren,
  },
]

const roleWorkspaces = [
  { role: 'Receptionist', route: 'reception' },
  { role: 'Triage', route: 'triage' },
  { role: 'Doctor', route: 'doctor' },
  { role: 'Lab', route: 'lab' },
  { role: 'Surgery', route: 'surgery' },
  { role: 'Pharmacist', route: 'pharmacy' },
  { role: 'Optical', route: 'optical' },
  { role: 'Cashier', route: 'cashier' },
  { role: 'Accountant', route: 'accounting' },
  { role: 'Inventory', route: 'inventory' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

/** Set when a page cannot be opened at all; App.vue shows it instead of leaving an empty screen. */
export const bootError = ref('')

router.onError(() => {
  bootError.value = 'بارگذاری این صفحه انجام نشد. اتصال را بررسی کنید و دوباره تلاش کنید.'
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  try {
    await auth.load()
  } catch {
    bootError.value = 'سرور در دسترس نیست. از روشن بودن سیستم مطمئن شوید و دوباره تلاش کنید.'
    return false
  }

  if (to.meta.public) {
    return auth.user ? { name: 'home' } : true
  }

  // Each role lands on its own workspace.
  // Managers and multi-role overseers keep the dashboard as home; everyone else goes straight to work.
  if (to.name === 'home' && !auth.user?.roles.some((r) => r === 'Manager' || r === 'SuperAdmin')) {
    const workspace = roleWorkspaces.find((w) => auth.user?.roles.includes(w.role))
    if (workspace) return { name: workspace.route }
  }

  if (!auth.user) {
    return { name: 'login', query: to.fullPath === '/' ? {} : { next: to.fullPath } }
  }

  // Convenience only: the API enforces the same permission on every request.
  if (to.meta.permission && !auth.can(to.meta.permission)) {
    return { name: 'home' }
  }

  return true
})
