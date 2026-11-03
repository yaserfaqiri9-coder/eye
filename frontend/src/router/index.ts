import { ref } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { pages } from './pages'

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
    component: pages.home,
    meta: { title: 'صفحه اصلی' },
  },
  {
    path: 'reception',
    name: 'reception',
    component: pages.reception,
    meta: { permission: 'visits.view', title: 'پذیرش' },
  },
  {
    path: 'patients',
    name: 'patients',
    component: pages.patients,
    meta: { permission: 'patients.view', title: 'مریضان' },
  },
  {
    path: 'patients/new',
    name: 'patient-new',
    component: pages.patientForm,
    meta: { permission: 'patients.create', title: 'مریض جدید' },
  },
  {
    path: 'patients/:id(\\d+)/edit',
    name: 'patient-edit',
    component: pages.patientForm,
    props: true,
    meta: { permission: 'patients.edit', title: 'ویرایش مریض' },
  },
  {
    path: 'patients/:id(\\d+)',
    name: 'patient',
    component: pages.patient,
    props: true,
    meta: { permission: 'patients.view', title: 'پرونده مریض' },
  },
  {
    path: 'triage',
    name: 'triage',
    component: pages.triage,
    meta: { permission: 'preexam.edit', title: 'معاینه ابتدایی' },
  },
  {
    path: 'triage/:visitId(\\d+)',
    name: 'triage-exam',
    component: pages.triageExam,
    props: true,
    meta: { permission: 'preexam.edit', title: 'معاینه ابتدایی' },
  },
  {
    path: 'refraction',
    name: 'refraction-queue',
    component: pages.refractionQueue,
    meta: { permission: 'preexam.edit', title: 'بینایی‌سنجی' },
  },
  {
    path: 'refraction/:visitId(\\d+)',
    name: 'refraction',
    component: pages.refraction,
    props: true,
    meta: { permission: 'preexam.view', title: 'بینایی‌سنجی' },
  },
  {
    path: 'doctor',
    name: 'doctor',
    component: pages.doctor,
    meta: { permission: 'consultation.edit', title: 'صف داکتر' },
  },
  {
    path: 'chart/:visitId(\\d+)',
    name: 'chart',
    component: pages.chart,
    props: true,
    meta: { permission: 'consultation.view', title: 'پرونده طبی' },
  },
  {
    path: 'lab',
    name: 'lab',
    component: pages.lab,
    meta: { permission: 'diagnostics.edit', title: 'آزمایشگاه' },
  },
  {
    path: 'lab/:id(\\d+)/result',
    name: 'lab-result',
    component: pages.labResult,
    props: true,
    meta: { permission: 'diagnostics.approve', title: 'نتیجه آزمایش' },
  },
  {
    path: 'surgery',
    name: 'surgery',
    component: pages.surgery,
    meta: { permission: 'surgery.view', title: 'عملیات' },
  },
  {
    path: 'surgery/:id(\\d+)',
    name: 'surgery-case',
    component: pages.surgeryCase,
    props: true,
    meta: { permission: 'surgery.edit', title: 'پرونده عملیات' },
  },
  {
    path: 'pharmacy',
    name: 'pharmacy',
    component: pages.pharmacy,
    meta: { permission: 'pharmacy.create', title: 'دواخانه' },
  },
  {
    path: 'pharmacy/sale',
    name: 'pharmacy-sale',
    component: pages.pharmacySale,
    meta: { permission: 'pharmacy.create', title: 'فروش دواخانه' },
  },
  {
    path: 'optical',
    name: 'optical',
    component: pages.optical,
    meta: { permission: 'optical.view', title: 'عینک' },
  },
  {
    path: 'optical/order',
    name: 'optical-order',
    component: pages.opticalOrder,
    meta: { permission: 'optical.create', title: 'سفارش عینک' },
  },
  {
    path: 'inventory',
    name: 'inventory',
    component: pages.inventory,
    meta: { permission: 'inventory.view', title: 'گدام' },
  },
  {
    path: 'inventory/purchase',
    name: 'inventory-purchase',
    component: pages.purchase,
    meta: { permission: 'inventory.create', title: 'ثبت خرید' },
  },
  {
    path: 'cashier',
    name: 'cashier',
    component: pages.cashier,
    meta: { permission: 'payments.create', title: 'صندوق' },
  },
  {
    path: 'cashier/invoice',
    name: 'invoice-new',
    component: pages.invoiceForm,
    meta: { permission: 'invoices.create', title: 'فاکتور مستقل' },
  },
  {
    path: 'accounting',
    name: 'accounting',
    component: pages.accounting,
    meta: { permission: 'invoices.view', title: 'حسابداری' },
  },
  {
    path: 'reports',
    name: 'reports',
    component: pages.reports,
    meta: { permission: 'reports.view', title: 'گزارش‌ها' },
  },
  {
    path: 'settings',
    name: 'settings',
    component: pages.settings,
    meta: { permission: 'settings.view', title: 'تنظیمات' },
  },
  {
    path: 'audio-station',
    name: 'audio-station',
    component: pages.audioStation,
    meta: { permission: 'calls.manage', title: 'مرکز پخش صوت' },
  },
  {
    path: 'audit',
    name: 'audit',
    component: pages.audit,
    meta: { permission: 'audit.view', title: 'ثبت فعالیت‌ها' },
  },
  {
    path: 'staff',
    name: 'staff',
    component: pages.staff,
    meta: { permission: 'users.view', title: 'کارمندان' },
  },
  {
    path: 'users',
    name: 'users',
    component: pages.users,
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
  // Clinic paper forms. Outside the app layout, and built only from records already in the system.
  {
    path: '/print/dossier/:visitId(\\d+)',
    name: 'print-dossier',
    component: () => import('@/features/print/DossierPrint.vue'),
    props: true,
  },
  {
    path: '/print/refraction/:visitId(\\d+)',
    name: 'print-refraction',
    component: () => import('@/features/print/RefractionSlipPrint.vue'),
    props: true,
  },
  {
    path: '/print/optical/:id(\\d+)',
    name: 'print-optical',
    component: () => import('@/features/print/OpticalSlipPrint.vue'),
    props: true,
  },
  {
    path: '/print/ot-card/:id(\\d+)',
    name: 'print-ot-card',
    component: () => import('@/features/print/OtCardPrint.vue'),
    props: true,
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
