<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime, shamsiToIso, todayShamsi } from '@/utils/date'
import { money } from '@/utils/money'
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
const tab = ref('ledger')
const error = ref('')
const balances = ref<LedgerBalance[]>([])
const reconciliation = ref<Reconciliation | null>(null)
const invoices = ref<InvoiceRow[]>([])
const payments = ref<PaymentRow[]>([])
const expenses = ref<ExpenseRow[]>([])

const tabs = computed(() =>
  [
    { key: 'ledger', label: 'دفتر مالی', visible: auth.can('ledger.view') },
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
    if (auth.can('invoices.view')) invoices.value = (await api<{ items: InvoiceRow[] }>('/invoices?pageSize=100')).items
    if (auth.can('payments.view')) payments.value = (await api<{ items: PaymentRow[] }>('/payments?pageSize=100')).items
    if (auth.can('expenses.view')) expenses.value = (await api<{ items: ExpenseRow[] }>('/expenses?pageSize=100')).items
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

const refund = reactive({ payment: null as PaymentRow | null, amount: 0, reason: '', saving: false, error: '', key: '' })

function openRefund(row: PaymentRow) {
  Object.assign(refund, { payment: row, amount: row.amount - row.refunded, reason: '', saving: false, error: '', key: newIdempotencyKey() })
}

async function submitRefund() {
  refund.saving = true
  refund.error = ''
  try {
    await api('/refunds', { method: 'POST', idempotencyKey: refund.key, body: { paymentId: refund.payment!.id, amount: refund.amount, reason: refund.reason } })
    refund.payment = null
    await loadAll()
  } catch (cause) {
    refund.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) refund.key = newIdempotencyKey()
  } finally {
    refund.saving = false
  }
}

const today = todayShamsi()
const expense = reactive({ open: false, category: '', description: '', amount: 0, date: shamsiToIso(today.year, today.month, today.day) as string | null, saving: false, error: '', key: '' })

function openExpense() {
  Object.assign(expense, { open: true, category: '', description: '', amount: 0, saving: false, error: '', key: newIdempotencyKey() })
}

async function submitExpense() {
  if (!expense.date) {
    expense.error = 'تاریخ را وارد کنید.'
    return
  }
  expense.saving = true
  expense.error = ''
  try {
    await api('/expenses', { method: 'POST', idempotencyKey: expense.key, body: { category: expense.category, description: expense.description, amount: expense.amount, expenseDate: expense.date } })
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

onMounted(async () => {
  await loadAll()
  tab.value = tabs.value[0]?.key ?? 'ledger'
})
</script>

<template>
  <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>

  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های حسابداری">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
    </div>
    <div class="xr-card__body">
      <template v-if="tab === 'ledger'">
        <div v-if="reconciliation" class="alert py-2" :class="reconciliation.balanced ? 'alert-success' : 'alert-danger'" role="status">
          {{ reconciliation.balanced ? 'دفتر مالی با فاکتورها و دریافت‌ها تطبیق دارد.' : 'دفتر مالی با تراکنش‌ها تطبیق ندارد. فوراً بررسی شود.' }}
        </div>
        <div class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>کد</th><th>حساب</th><th>بدهکار</th><th>بستانکار</th><th>مانده</th></tr></thead>
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
      </template>

      <template v-else-if="tab === 'invoices'">
        <div class="xr-table-wrap">
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
                <td v-if="auth.can('invoices.approve')"><button v-if="row.balance > 0" type="button" class="btn btn-soft btn-sm" @click="openDiscount(row)">تخفیف</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'payments'">
        <div class="xr-table-wrap">
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
      </template>

      <template v-else>
        <button v-if="auth.can('expenses.create')" type="button" class="btn btn-primary mb-3" @click="openExpense">ثبت مصرف</button>
        <p v-if="expenses.length === 0" class="text-muted">مصرفی ثبت نشده است.</p>
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
                <td v-if="auth.can('expenses.cancel')"><button v-if="row.status === 'Recorded'" type="button" class="btn btn-soft btn-sm" @click="Object.assign(cancelling, { row, reason: '', error: '' })">لغو</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </div>
  </section>

  <UiModal v-if="discount.detail" :title="`تخفیف ${discount.detail.invoice.invoiceNumber}`" @close="discount.detail = null">
    <form @submit.prevent="submitDiscount">
      <p>مانده: {{ money(discount.detail.invoice.balance) }}</p>
      <label class="form-label" for="d-amount">مبلغ تخفیف</label>
      <input id="d-amount" v-model.number="discount.amount" class="form-control mb-3" type="number" min="0.01" step="0.01" :max="discount.detail.invoice.balance" required />
      <label class="form-label" for="d-reason">دلیل</label>
      <input id="d-reason" v-model="discount.reason" class="form-control mb-3" maxlength="300" required />
      <div v-if="discount.error" class="alert alert-danger py-2" role="alert">{{ discount.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="discount.saving">ثبت تخفیف</button>
    </form>
  </UiModal>

  <UiModal v-if="refund.payment" :title="`برگشت وجه ${refund.payment.receiptNumber}`" @close="refund.payment = null">
    <form @submit.prevent="submitRefund">
      <label class="form-label" for="rf-amount">مبلغ برگشت</label>
      <input id="rf-amount" v-model.number="refund.amount" class="form-control mb-3" type="number" min="0.01" step="0.01" :max="refund.payment.amount - refund.payment.refunded" required />
      <label class="form-label" for="rf-reason">دلیل</label>
      <input id="rf-reason" v-model="refund.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="refund.error" class="alert alert-danger py-2" role="alert">{{ refund.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="refund.saving">تأیید برگشت وجه</button>
    </form>
  </UiModal>

  <UiModal v-if="expense.open" title="ثبت مصرف" @close="expense.open = false">
    <form @submit.prevent="submitExpense">
      <div class="mb-3">
        <label class="form-label" for="e-category">دسته</label>
        <input id="e-category" v-model="expense.category" class="form-control" maxlength="100" required />
      </div>
      <div class="mb-3">
        <label class="form-label" for="e-desc">شرح</label>
        <input id="e-desc" v-model="expense.description" class="form-control" maxlength="300" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="e-amount">مبلغ</label>
        <input id="e-amount" v-model.number="expense.amount" class="form-control" type="number" min="0.01" step="0.01" required />
      </div>
      <div class="mb-3"><ShamsiDateInput v-model="expense.date" label="تاریخ" :initial="today" /></div>
      <div v-if="expense.error" class="alert alert-danger py-2" role="alert">{{ expense.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="expense.saving">ثبت مصرف</button>
    </form>
  </UiModal>

  <UiModal v-if="cancelling.row" :title="`لغو مصرف ${cancelling.row.expenseNumber}`" @close="cancelling.row = null">
    <form @submit.prevent="submitCancel">
      <label class="form-label" for="x-reason">دلیل</label>
      <input id="x-reason" v-model="cancelling.reason" class="form-control mb-3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger py-2" role="alert">{{ cancelling.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="cancelling.saving">لغو مصرف</button>
    </form>
  </UiModal>
</template>
