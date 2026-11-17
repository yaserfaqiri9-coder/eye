<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useRoute } from 'vue-router'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiFilterBar from '@/components/UiFilterBar.vue'
import UiPeriodFilter from '@/components/UiPeriodFilter.vue'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime, todayIso } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import type { DateRange } from '@/utils/period'
import { invoiceStatusLabels, invoiceStatusTones, type InvoiceDetail, type InvoiceRow, type PaymentRow } from './types'

interface LedgerBalance {
  code: string
  name: string
  debit: number
  credit: number
}

interface Reconciliation {
  balanced: boolean
  totalDebit: number
  totalCredit: number
  ledgerReceivable: number
  invoiceReceivable: number
  ledgerCash: number
  paymentsLessRefundsAndExpenses: number
}

// Plain Dari names for staff who are not accountants; the ledger's own names stay in the accountant's table.
// A minus line lowers income (discounts, money handed back).
const incomeLines = [
  { code: '4000', label: 'خدمات' },
  { code: '4100', label: 'دواخانه' },
  { code: '4200', label: 'عینک' },
  { code: '4900', label: 'تخفیف', minus: true },
  { code: '4950', label: 'پول پس‌داده', minus: true },
]
const costLines = [
  { code: '5000', label: 'قیمت خرید اجناس فروخته‌شده' },
  { code: '6000', label: 'مصارف' },
  { code: '6100', label: 'کمی اجناس' },
]

interface ExpenseRow {
  id: number
  expenseNumber: string
  category: string
  description: string | null
  amount: number
  expenseDate: string
  status: string
  version: number
}

const auth = useAuthStore()
const route = useRoute()
const tab = ref('ledger')
const error = ref('')
const balances = ref<LedgerBalance[]>([])
const reconciliation = ref<Reconciliation | null>(null)
/** Debit minus credit; negated for accounts that grow on the credit side, so every amount reads as a plain number. */
function balanceOf(code: string, creditSide = false) {
  const b = balances.value.find((row) => row.code === code)
  const net = b ? b.debit - b.credit : 0
  return creditSide ? -net : net
}

const income = computed(() => incomeLines.map((l) => ({ ...l, amount: l.minus ? balanceOf(l.code) : balanceOf(l.code, true) })))
const costs = computed(() => costLines.map((l) => ({ ...l, amount: balanceOf(l.code) })))
const incomeTotal = computed(() => income.value.reduce((sum, l) => sum + (l.minus ? -l.amount : l.amount), 0))
const costTotal = computed(() => costs.value.reduce((sum, l) => sum + l.amount, 0))
const profit = computed(() => incomeTotal.value - costTotal.value)

const headline = computed(() => [
  { label: 'صندوق', amount: balanceOf('1000') },
  { label: 'باقیات مریضان', amount: balanceOf('1100') },
  { label: 'قرض به شرکت‌ها', amount: balanceOf('2000', true) },
  { label: 'ارزش گدام', amount: balanceOf('1200') },
])

const invoiceSearch = ref('')
// Each list is read a page at a time, so older invoices, receipts and expenses stay reachable however many there are.
const invoiceList = usePagedList<InvoiceRow>(() => (invoiceSearch.value.trim() ? `/invoices?q=${encodeURIComponent(invoiceSearch.value.trim())}` : '/invoices'))
const paymentDays = reactive({ from: null as string | null, to: null as string | null })
const expenseDays = reactive({ from: null as string | null, to: null as string | null })
const withDays = (path: string, days: { from: string | null; to: string | null }) => {
  const params = new URLSearchParams()
  if (days.from) params.set('from', days.from)
  if (days.to) params.set('to', days.to)
  return params.size ? `${path}?${params}` : path
}
const paymentList = usePagedList<PaymentRow>(() => withDays('/payments', paymentDays))
const expenseList = usePagedList<ExpenseRow>(() => withDays('/expenses', expenseDays))
watch(() => ({ ...paymentDays }), () => paymentList.first().catch((cause) => (error.value = errorMessage(cause))))
watch(() => ({ ...expenseDays }), () => expenseList.first().catch((cause) => (error.value = errorMessage(cause))))

/** The shared period filter speaks in whole ranges; null is every day. */
function periodOf(days: { from: string | null; to: string | null }) {
  return computed<DateRange | null>({
    get: () => (days.from || days.to ? { from: (days.from ?? days.to)!, to: (days.to ?? days.from)! } : null),
    set: (range) => Object.assign(days, range ?? { from: null, to: null }),
  })
}
const paymentPeriod = periodOf(paymentDays)
const expensePeriod = periodOf(expenseDays)
const invoices = invoiceList.rows
const payments = paymentList.rows
const expenses = expenseList.rows

/** Every category already used, offered while typing, so one kind of expense is not split in the reports by a spelling. */
const expenseCategories = computed(() => [...new Set(expenses.value.map((row) => row.category.trim()).filter(Boolean))].sort())

let searchTimer: ReturnType<typeof setTimeout> | undefined
watch(invoiceSearch, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(() => invoiceList.first().catch((cause) => (error.value = errorMessage(cause))), 300)
})
onBeforeUnmount(() => clearTimeout(searchTimer))

function turn(list: { goTo: (page: number) => Promise<void> }, page: number) {
  list.goTo(page).catch((cause) => (error.value = errorMessage(cause)))
}

const tabs = computed(() =>
  [
    { key: 'ledger', label: 'خلاصه مالی', visible: auth.can('ledger.view') },
    { key: 'invoices', label: 'فاکتورها', visible: auth.can('invoices.view') },
    { key: 'payments', label: 'دریافت‌ها و برگشت', visible: auth.can('payments.view') },
    { key: 'expenses', label: 'مصارف', visible: auth.can('expenses.view') },
  ].filter((t) => t.visible),
)

async function loadAll() {
  error.value = ''
  try {
    if (auth.can('ledger.view')) {
      ;[balances.value, reconciliation.value] = await Promise.all([api<LedgerBalance[]>('/ledger/balances'), api<Reconciliation>('/ledger/reconciliation')])
    }
    if (auth.can('invoices.view')) await invoiceList.load()
    if (auth.can('payments.view')) await paymentList.load()
    if (auth.can('expenses.view')) await expenseList.load()
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

const discount = reactive({ detail: null as InvoiceDetail | null, amount: 0, reason: '', saving: false, error: '' })

async function openDiscount(row: InvoiceRow) {
  Object.assign(discount, { detail: null, amount: 0, reason: '', saving: false, error: '' })
  try {
    discount.detail = await api<InvoiceDetail>(`/invoices/${row.id}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

async function submitDiscount() {
  discount.saving = true
  discount.error = ''
  try {
    await api(`/invoices/${discount.detail!.invoice.id}/discount`, { method: 'POST', body: { amount: discount.amount, reason: discount.reason, version: discount.detail!.invoice.version } })
    discount.detail = null
    await loadAll()
  } catch (cause) {
    discount.error = errorMessage(cause)
  } finally {
    discount.saving = false
  }
}

const refund = reactive({ payment: null as PaymentRow | null, amount: 0, reason: '', saving: false, error: '', key: '', printedId: null as number | null })

function openRefund(row: PaymentRow) {
  // Rounded because subtracting decimals in floating point can leave a tail the server rightly refuses.
  Object.assign(refund, { payment: row, amount: Number((row.amount - row.refunded).toFixed(2)), reason: '', saving: false, error: '', key: newIdempotencyKey(), printedId: null })
}

async function submitRefund() {
  refund.saving = true
  refund.error = ''
  try {
    const result = await api<{ id: number }>('/refunds', { method: 'POST', idempotencyKey: refund.key, body: { paymentId: refund.payment!.id, amount: refund.amount, reason: refund.reason } })
    refund.printedId = result.id
    await loadAll()
  } catch (cause) {
    refund.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) refund.key = newIdempotencyKey()
  } finally {
    refund.saving = false
  }
}

const undoDiscount = reactive({ row: null as InvoiceRow | null, reason: '', saving: false, error: '' })

async function submitUndoDiscount() {
  undoDiscount.saving = true
  undoDiscount.error = ''
  try {
    await api(`/invoices/${undoDiscount.row!.id}/discount/cancel`, { method: 'POST', body: { reason: undoDiscount.reason, version: undoDiscount.row!.version } })
    undoDiscount.row = null
    await loadAll()
  } catch (cause) {
    undoDiscount.error = errorMessage(cause)
  } finally {
    undoDiscount.saving = false
  }
}

const expense = reactive({ open: false, correcting: null as ExpenseRow | null, reason: '', category: '', description: '', amount: 0, date: todayIso() as string | null, saving: false, error: '', key: '' })

function openExpense(row?: ExpenseRow) {
  Object.assign(expense, { open: true, correcting: row ?? null, reason: '', category: row?.category ?? '', description: row?.description ?? '', amount: row?.amount ?? 0, date: row?.expenseDate ?? todayIso(), saving: false, error: '', key: newIdempotencyKey() })
}

async function submitExpense() {
  if (!expense.date) {
    expense.error = 'تاریخ را وارد کنید.'
    return
  }
  expense.saving = true
  expense.error = ''
  try {
    const body = { category: expense.category, description: expense.description, amount: expense.amount, expenseDate: expense.date }
    if (expense.correcting) await api(`/expenses/${expense.correcting.id}/correct`, { method: 'POST', body: { ...body, reason: expense.reason, version: expense.correcting.version } })
    else await api('/expenses', { method: 'POST', idempotencyKey: expense.key, body })
    expense.open = false
    await loadAll()
  } catch (cause) {
    expense.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) expense.key = newIdempotencyKey()
  } finally {
    expense.saving = false
  }
}

const cancelling = reactive({ row: null as ExpenseRow | null, reason: '', saving: false, error: '' })

async function submitCancel() {
  cancelling.saving = true
  cancelling.error = ''
  try {
    await api(`/expenses/${cancelling.row!.id}/cancel`, { method: 'POST', body: { reason: cancelling.reason, version: cancelling.row!.version } })
    cancelling.row = null
    await loadAll()
  } catch (cause) {
    cancelling.error = errorMessage(cause)
  } finally {
    cancelling.saving = false
  }
}

onMounted(() => {
  // Chosen before loading, so a tab the user clicks while data is still arriving stays selected.
  // A link from a report names the tab it means; one this user may not see falls back to the first.
  const asked = tabs.value.find((t) => t.key === route.query.tab)
  tab.value = asked?.key ?? tabs.value[0]?.key ?? 'ledger'
  loadAll()
})
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>

  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های حسابداری">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
    </div>
    <div class="xr-card__body">
      <template v-if="tab === 'ledger'">
        <div v-if="reconciliation && !reconciliation.balanced" class="alert alert-danger" role="alert">حساب‌ها برابر نیست. به مدیر سیستم خبر دهید.</div>

        <div class="fin-head">
          <span v-if="reconciliation?.balanced" class="fin-ok" role="status">حساب‌ها درست است</span>
          <span class="fin-since">از شروع سیستم تا امروز</span>
        </div>

        <div class="fin-tiles">
          <div v-for="h in headline" :key="h.label" class="fin-tile">
            <span class="fin-tile__label">{{ h.label }}</span>
            <span class="fin-tile__value">{{ money(h.amount) }}</span>
          </div>
        </div>

        <div class="fin-panels">
          <section class="fin-panel" aria-label="عاید">
            <h3 class="fin-panel__title">عاید</h3>
            <div v-for="l in income" :key="l.code" class="fin-line" :class="{ 'fin-line--minus': l.minus }">
              <span>{{ l.label }}</span>
              <span>{{ l.minus && l.amount ? '− ' : '' }}{{ money(l.amount) }}</span>
            </div>
            <div class="fin-line fin-line--total"><span>عاید خالص</span><span>{{ money(incomeTotal) }}</span></div>
          </section>
          <section class="fin-panel" aria-label="مصارف">
            <h3 class="fin-panel__title">مصارف</h3>
            <div v-for="l in costs" :key="l.code" class="fin-line">
              <span>{{ l.label }}</span>
              <span>{{ money(l.amount) }}</span>
            </div>
            <div class="fin-line fin-line--total"><span>مجموع مصارف</span><span>{{ money(costTotal) }}</span></div>
          </section>
        </div>

        <div class="fin-result" :class="profit >= 0 ? 'fin-result--gain' : 'fin-result--loss'">
          <span>{{ profit >= 0 ? 'مفاد' : 'ضرر' }}</span>
          <strong>{{ money(Math.abs(profit)) }}</strong>
        </div>

        <details class="fin-detail">
          <summary>جدول حسابداری</summary>
          <div class="mt-3">
            <div class="xr-table-wrap">
              <table class="xr-table">
                <thead><tr><th>کود</th><th>حساب</th><th>بردگی</th><th>رسیدگی</th><th>باقی</th></tr></thead>
                <tbody>
                  <tr v-for="b in balances" :key="b.code">
                    <td><span class="ltr-num">{{ b.code }}</span></td>
                    <td>{{ b.name }}</td>
                    <td>{{ money(b.debit) }}</td>
                    <td>{{ money(b.credit) }}</td>
                    <td>{{ money(b.debit - b.credit) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </details>
      </template>

      <template v-else-if="tab === 'invoices'">
        <UiFilterBar
          v-model:search="invoiceSearch"
          flat
          search-label="جستجوی فاکتور"
          search-placeholder="شماره فاکتور، شماره پرونده یا نام"
          :count="`${quantity(invoiceList.total.value)} فاکتور`"
        />
        <p v-if="invoices.length === 0" class="xr-state">فاکتوری پیدا نشد.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>مریض</th><th>مجموع</th><th>تخفیف</th><th>پرداخت</th><th>برگشت</th><th>مانده</th><th>وضعیت</th><th v-if="auth.can('invoices.approve')">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in invoices" :key="row.id">
                <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
                <td>{{ row.patientName ?? '—' }}</td>
                <td>{{ money(row.total) }}</td>
                <td>{{ money(row.discount) }}</td>
                <td>{{ money(row.paid) }}</td>
                <td>{{ money(row.refunded) }}</td>
                <td>{{ money(row.balance) }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${invoiceStatusTones[row.status]}`">{{ invoiceStatusLabels[row.status] }}</span></td>
                <td v-if="auth.can('invoices.approve')" class="text-nowrap">
                  <button v-if="row.balance > 0" type="button" class="btn btn-soft btn-sm" @click="openDiscount(row)">تخفیف</button>
                  <button v-if="row.discount > 0 && row.paid === 0 && row.status !== 'Void'" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(undoDiscount, { row, reason: '', saving: false, error: '' })">برگشت تخفیف</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="invoiceList.page.value" :page-count="invoiceList.pageCount.value" @go="turn(invoiceList, $event)" />
      </template>

      <template v-else-if="tab === 'payments'">
        <UiFilterBar flat :count="`${quantity(paymentList.total.value)} رسید`">
          <UiPeriodFilter v-model="paymentPeriod" allow-all />
        </UiFilterBar>
        <p v-if="payments.length === 0" class="xr-state">{{ paymentDays.from || paymentDays.to ? 'در این روزها دریافتی ثبت نشده است.' : 'دریافتی ثبت نشده است.' }}</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>رسید</th><th>فاکتور</th><th>مبلغ</th><th>برگشت‌شده</th><th>دریافت‌کننده</th><th>زمان</th><th v-if="auth.can('payments.refund')">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in payments" :key="row.id">
                <td><span class="ltr-num">{{ row.receiptNumber }}</span></td>
                <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
                <td>{{ money(row.amount) }}</td>
                <td>{{ money(row.refunded) }}</td>
                <td>{{ row.receivedBy ?? '—' }}</td>
                <td>{{ formatDateTime(row.createdAt) }}</td>
                <td v-if="auth.can('payments.refund')"><button v-if="row.refunded < row.amount" type="button" class="btn btn-soft btn-sm" @click="openRefund(row)">برگشت وجه</button></td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="paymentList.page.value" :page-count="paymentList.pageCount.value" @go="turn(paymentList, $event)" />
      </template>

      <template v-else>
        <UiFilterBar flat :count="`${quantity(expenseList.total.value)} مصرف`">
          <UiPeriodFilter v-model="expensePeriod" allow-all />
          <template v-if="auth.can('expenses.create')" #actions>
            <button type="button" class="btn btn-primary" @click="openExpense()">ثبت مصرف</button>
          </template>
        </UiFilterBar>
        <p v-if="expenses.length === 0" class="xr-state">{{ expenseDays.from || expenseDays.to ? 'در این روزها مصرفی ثبت نشده است.' : 'مصرفی ثبت نشده است.' }}</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>دسته</th><th>شرح</th><th>مبلغ</th><th>تاریخ</th><th>وضعیت</th><th v-if="auth.can('expenses.cancel')">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in expenses" :key="row.id">
                <td><span class="ltr-num">{{ row.expenseNumber }}</span></td>
                <td>{{ row.category }}</td>
                <td>{{ row.description ?? '—' }}</td>
                <td>{{ money(row.amount) }}</td>
                <td>{{ formatDate(row.expenseDate) }}</td>
                <td>{{ row.status === 'Recorded' ? 'ثبت‌شده' : 'لغو شده' }}</td>
                <td v-if="auth.can('expenses.cancel')" class="text-nowrap">
                  <template v-if="row.status === 'Recorded'">
                    <button v-if="auth.can('expenses.create')" type="button" class="btn btn-soft btn-sm" @click="openExpense(row)">اصلاح</button>
                    <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(cancelling, { row, reason: '', error: '' })">لغو</button>
                  </template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="expenseList.page.value" :page-count="expenseList.pageCount.value" @go="turn(expenseList, $event)" />
      </template>
    </div>
  </section>

  <UiModal v-if="discount.detail" :title="`تخفیف ${discount.detail.invoice.invoiceNumber}`" @close="discount.detail = null">
    <form @submit.prevent="submitDiscount">
      <p>مانده: {{ money(discount.detail.invoice.balance) }}</p>
      <ul v-if="discount.detail.discounts.length" class="list-unstyled small mb-3" aria-label="سابقه تخفیف">
        <li v-for="(d, i) in discount.detail.discounts" :key="i">{{ d.details }} ({{ d.byUser ?? '—' }}، {{ formatDateTime(d.createdAt) }})</li>
      </ul>
      <label class="form-label" for="d-amount">مبلغ تخفیف</label>
      <input id="d-amount" v-model.number="discount.amount" class="form-control mb-3" type="number" min="0.01" step="0.01" :max="discount.detail.invoice.balance" required />
      <label class="form-label" for="d-reason">دلیل</label>
      <input id="d-reason" v-model="discount.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="discount.error" class="alert alert-danger" role="alert">{{ discount.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="discount.saving">ثبت تخفیف</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="refund.payment" :title="`برگشت وجه ${refund.payment.receiptNumber}`" @close="refund.payment = null">
    <div v-if="refund.printedId" class="xr-form-actions">
      <RouterLink :to="`/print/refund/${refund.printedId}`" target="_blank" class="btn btn-soft">چاپ رسید برگشت وجه</RouterLink>
      <button type="button" class="btn btn-soft-danger" data-modal-cancel>بستن</button>
    </div>
    <form v-else @submit.prevent="submitRefund">
      <label class="form-label" for="rf-amount">مبلغ برگشت</label>
      <input id="rf-amount" v-model.number="refund.amount" class="form-control mb-3" type="number" min="0.01" step="0.01" :max="refund.payment.amount - refund.payment.refunded" required />
      <label class="form-label" for="rf-reason">دلیل</label>
      <input id="rf-reason" v-model="refund.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="refund.error" class="alert alert-danger" role="alert">{{ refund.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="refund.saving">تأیید برگشت وجه</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="undoDiscount.row" :title="`برگشت تخفیف ${undoDiscount.row.invoiceNumber}`" @close="undoDiscount.row = null">
    <form @submit.prevent="submitUndoDiscount">
      <p>تخفیف {{ money(undoDiscount.row.discount) }} برمی‌گردد و مانده فاکتور بیشتر می‌شود.</p>
      <label class="form-label" for="ud-reason">دلیل</label>
      <input id="ud-reason" v-model="undoDiscount.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="undoDiscount.error" class="alert alert-danger" role="alert">{{ undoDiscount.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="undoDiscount.saving">برگشت تخفیف</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="expense.open" :title="expense.correcting ? `اصلاح مصرف ${expense.correcting.expenseNumber}` : 'ثبت مصرف'" @close="expense.open = false">
    <form @submit.prevent="submitExpense">
      <p v-if="expense.correcting" class="small text-muted">مصرف فعلی لغو و مصرف تازه ثبت می‌شود. سابقه هر دو می‌ماند.</p>
      <div class="mb-3">
        <label class="form-label" for="e-category">دسته</label>
        <input id="e-category" v-model="expense.category" class="form-control" maxlength="100" list="e-category-options" autocomplete="off" required />
        <datalist id="e-category-options"><option v-for="category in expenseCategories" :key="category" :value="category" /></datalist>
      </div>
      <div class="mb-3">
        <label class="form-label" for="e-desc">شرح</label>
        <input id="e-desc" v-model="expense.description" class="form-control" maxlength="300" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="e-amount">مبلغ</label>
        <input id="e-amount" v-model.number="expense.amount" class="form-control" type="number" min="0.01" step="0.01" required />
      </div>
      <div class="mb-3"><ShamsiDateInput v-model="expense.date" label="تاریخ" required /></div>
      <div v-if="expense.correcting" class="mb-3"><label class="form-label" for="e-reason">دلیل اصلاح</label><input id="e-reason" v-model="expense.reason" class="form-control" minlength="3" maxlength="300" required /></div>
      <div v-if="expense.error" class="alert alert-danger" role="alert">{{ expense.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="expense.saving">{{ expense.correcting ? 'ثبت اصلاح' : 'ثبت مصرف' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="cancelling.row" :title="`لغو مصرف ${cancelling.row.expenseNumber}`" @close="cancelling.row = null">
    <form @submit.prevent="submitCancel">
      <label class="form-label" for="x-reason">دلیل</label>
      <input id="x-reason" v-model="cancelling.reason" class="form-control mb-3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="cancelling.saving">لغو مصرف</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.fin-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 14px;
}

.fin-ok {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 10px;
  border-radius: 999px;
  background: var(--xr-tone-green-bg);
  color: var(--xr-tone-green-text);
  font-size: 12.5px;
}

.fin-ok::before {
  content: '';
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}

.fin-since {
  margin-inline-start: auto;
  color: var(--xr-text-muted);
  font-size: 12.5px;
}

.fin-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
  gap: 12px;
  margin-bottom: 20px;
}

.fin-tile {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 16px 18px;
  border: 1px solid var(--xr-border);
  border-radius: var(--xr-radius-tile, var(--xr-radius-control));
}

.fin-tile__label {
  color: var(--xr-text-soft);
  font-size: 13px;
}

.fin-tile__value {
  color: var(--xr-text);
  font-size: var(--xr-fs-stat);
  font-weight: 600;
  line-height: 1.2;
  white-space: nowrap;
}

.fin-panels {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 12px;
}

.fin-panel {
  padding: 16px 18px 6px;
  border: 1px solid var(--xr-border);
  border-radius: var(--xr-radius-tile, var(--xr-radius-control));
}

.fin-panel__title {
  margin: 0 0 6px;
  color: var(--xr-text-soft);
  font-size: 13px;
  font-weight: 500;
}

.fin-line {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  padding: 9px 0;
  border-bottom: 1px solid var(--xr-row-border);
  color: var(--xr-text);
}

.fin-line > span:last-child {
  white-space: nowrap;
}

.fin-line--minus {
  color: var(--xr-text-soft);
}

.fin-line--total {
  border-bottom: 0;
  font-weight: 600;
}

.fin-result {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  margin-top: 12px;
  padding: 16px 18px;
  border-radius: var(--xr-radius-tile, var(--xr-radius-control));
  font-size: 15px;
}

.fin-result strong {
  font-size: var(--xr-fs-stat);
  font-weight: 600;
  white-space: nowrap;
}

.fin-result--gain {
  background: var(--xr-tone-green-bg);
  color: var(--xr-tone-green-text);
}

.fin-result--loss {
  background: var(--xr-tone-red-bg);
  color: var(--xr-tone-red-text);
}

.fin-detail {
  margin-top: 20px;
}

.fin-detail > summary {
  width: fit-content;
  color: var(--xr-text-muted);
  font-size: 13px;
  cursor: pointer;
}
</style>
