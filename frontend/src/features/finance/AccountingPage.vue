<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime, todayIso } from '@/utils/date'
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
  tab.value = tabs.value[0]?.key ?? 'ledger'
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
        <div v-if="reconciliation" class="alert" :class="reconciliation.balanced ? 'alert-success' : 'alert-danger'" role="status">
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
                <td v-if="auth.can('invoices.approve')" class="text-nowrap">
                  <button v-if="row.balance > 0" type="button" class="btn btn-soft btn-sm" @click="openDiscount(row)">تخفیف</button>
                  <button v-if="row.discount > 0 && row.paid === 0 && row.status !== 'Void'" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(undoDiscount, { row, reason: '', saving: false, error: '' })">برگشت تخفیف</button>
                </td>
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
        <button v-if="auth.can('expenses.create')" type="button" class="btn btn-primary mb-3" @click="openExpense()">ثبت مصرف</button>
        <p v-if="expenses.length === 0" class="xr-state">مصرفی ثبت نشده است.</p>
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
        <button type="submit" class="btn btn-primary" :disabled="refund.saving">تأیید برگشت وجه</button>
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
        <button type="submit" class="btn btn-primary" :disabled="undoDiscount.saving">برگشت تخفیف</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="expense.open" :title="expense.correcting ? `اصلاح مصرف ${expense.correcting.expenseNumber}` : 'ثبت مصرف'" @close="expense.open = false">
    <form @submit.prevent="submitExpense">
      <p v-if="expense.correcting" class="small text-muted">مصرف فعلی لغو و مصرف تازه ثبت می‌شود. سابقه هر دو می‌ماند.</p>
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
        <button type="submit" class="btn btn-primary" :disabled="cancelling.saving">لغو مصرف</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
