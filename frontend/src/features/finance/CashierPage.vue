<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money } from '@/utils/money'
import { invoiceStatusLabels, invoiceStatusTones, type InvoiceDetail, type InvoiceRow, type PaymentRow, type SessionRow } from './types'

const auth = useAuthStore()
const invoices = ref<InvoiceRow[]>([])
const sessions = ref<SessionRow[]>([])
const registers = ref<{ id: number; name: string }[]>([])
const q = ref('')
const loading = ref(true)
const error = ref('')

const openSession = computed(() => sessions.value.find((s) => !s.closedAt) ?? null)
const pendingTotal = computed(() => invoices.value.reduce((sum, i) => sum + i.balance, 0))

let controller: AbortController | null = null
async function loadInvoices() {
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  try {
    const params = new URLSearchParams({ unpaid: 'true', pageSize: '100' })
    if (q.value.trim()) params.set('q', q.value.trim())
    invoices.value = (await api<{ items: InvoiceRow[] }>(`/invoices?${params}`, { signal })).items
    error.value = ''
  } catch (cause) {
    if (signal.aborted) return
    error.value = errorMessage(cause)
  }
  loading.value = false
}

async function loadSessions() {
  sessions.value = await api<SessionRow[]>('/cash/sessions?mine=true')
}

async function refresh() {
  try {
    await Promise.all([loadInvoices(), loadSessions()])
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

const opening = reactive({ registerId: null as number | null, amount: 0, saving: false })

async function open() {
  opening.saving = true
  error.value = ''
  try {
    await api('/cash/sessions', { method: 'POST', body: { registerId: opening.registerId, openingAmount: opening.amount } })
    await loadSessions()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    opening.saving = false
  }
}

const closing = reactive({ open: false, counted: 0, saving: false, error: '' })

async function close() {
  closing.saving = true
  closing.error = ''
  try {
    await api(`/cash/sessions/${openSession.value!.id}/close`, { method: 'POST', body: { countedAmount: closing.counted, version: openSession.value!.version } })
    closing.open = false
    await loadSessions()
  } catch (cause) {
    closing.error = errorMessage(cause)
  } finally {
    closing.saving = false
  }
}

const pay = reactive({ detail: null as InvoiceDetail | null, amount: 0, method: 'Cash', reference: '', saving: false, error: '', key: '', receipt: null as PaymentRow | null })

async function openPayment(row: InvoiceRow) {
  Object.assign(pay, { detail: null, amount: row.balance, method: 'Cash', reference: '', saving: false, error: '', key: newIdempotencyKey(), receipt: null })
  try {
    pay.detail = await api<InvoiceDetail>(`/invoices/${row.id}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

async function submitPayment() {
  pay.saving = true
  pay.error = ''
  try {
    pay.receipt = await api<PaymentRow>('/payments', {
      method: 'POST',
      idempotencyKey: pay.key,
      body: { invoiceId: pay.detail!.invoice.id, amount: pay.amount, method: pay.method, reference: pay.reference, invoiceVersion: pay.detail!.invoice.version },
    })
    await refresh()
  } catch (cause) {
    pay.error = errorMessage(cause)
    // A definite refusal may be retried with new values; an unknown outcome keeps the key so a retry cannot double-charge.
    if (cause instanceof ApiError && !cause.outcomeUnknown) pay.key = newIdempotencyKey()
    if (cause instanceof ApiError && cause.code === 'invoice_changed') {
      pay.detail = await api<InvoiceDetail>(`/invoices/${pay.detail!.invoice.id}`)
      pay.amount = pay.detail.invoice.balance
    }
  } finally {
    pay.saving = false
  }
}

const voiding = reactive({ row: null as InvoiceRow | null, reason: '', saving: false, error: '' })

async function submitVoid() {
  voiding.saving = true
  voiding.error = ''
  try {
    await api(`/invoices/${voiding.row!.id}/void`, { method: 'POST', body: { reason: voiding.reason, version: voiding.row!.version } })
    voiding.row = null
    await loadInvoices()
  } catch (cause) {
    voiding.error = errorMessage(cause)
  } finally {
    voiding.saving = false
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(loadInvoices, 300)
})

useLiveRefresh(['InvoiceIssued', 'PaymentReceived'], refresh)
onMounted(async () => {
  try {
    registers.value = await api<typeof registers.value>('/cash/registers')
    opening.registerId = registers.value[0]?.id ?? null
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
})
onBeforeUnmount(() => clearTimeout(debounce))
</script>

<template>
  <div class="d-grid gap-4">
    <div class="cash-stats">
      <StatCard label="پرداخت‌های در انتظار" :value="invoices.length" icon="ri-receipt-fill" tone="orange" />
      <StatCard label="مجموع مانده" :value="money(pendingTotal)" icon="ri-bill-fill" tone="red" />
      <StatCard label="دریافت نقدی این صندوق" :value="money(openSession?.cashCollected ?? 0)" icon="ri-wallet-3-fill" />
    </div>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <UiCard title="صندوق">
      <template v-if="openSession">
        <div class="d-flex flex-wrap align-items-center justify-content-between gap-3">
          <dl class="xr-facts flex-grow-1">
            <div><dt>صندوق باز</dt><dd>{{ openSession.registerName }}</dd></div>
            <div><dt>موجودی شروع</dt><dd>{{ money(openSession.openingAmount) }}</dd></div>
            <div><dt>زمان باز شدن</dt><dd>{{ formatDateTime(openSession.openedAt) }}</dd></div>
          </dl>
          <button v-if="auth.can('cash.edit')" type="button" class="btn btn-soft" @click="closing.open = true; closing.counted = 0; closing.error = ''">بستن صندوق</button>
        </div>
      </template>
      <form v-else-if="auth.can('cash.create')" class="row g-3 align-items-end" @submit.prevent="open">
        <div class="col-md-4">
          <label class="form-label" for="c-register">صندوق</label>
          <select id="c-register" v-model.number="opening.registerId" class="form-select" required>
            <option v-for="r in registers" :key="r.id" :value="r.id">{{ r.name }}</option>
          </select>
        </div>
        <div class="col-md-4">
          <label class="form-label" for="c-opening">موجودی شروع</label>
          <input id="c-opening" v-model.number="opening.amount" class="form-control" type="number" min="0" step="0.01" required />
        </div>
        <div class="col-md-4"><button type="submit" class="btn btn-primary xr-field-height" :disabled="opening.saving">باز کردن صندوق</button></div>
      </form>
    </UiCard>

    <UiCard title="فاکتورهای قابل دریافت">
      <template #actions>
        <div class="d-flex gap-2">
          <input v-model="q" type="search" class="form-control" placeholder="شماره فاکتور، پرونده یا نام" aria-label="جستجوی فاکتور" />
          <RouterLink v-if="auth.can('invoices.create')" :to="{ name: 'invoice-new' }" class="btn btn-soft text-nowrap">فاکتور مستقل</RouterLink>
        </div>
      </template>
      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="invoices.length === 0" class="xr-state">فاکتور قابل دریافتی نیست.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>شماره</th><th>مریض</th><th>مجموع</th><th>پرداخت‌شده</th><th>مانده</th><th>وضعیت</th><th>عملیات</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in invoices" :key="row.id">
              <td><span class="ltr-num">{{ row.invoiceNumber }}</span></td>
              <td>{{ row.patientName ?? 'بدون مریض' }}</td>
              <td>{{ money(row.total - row.discount) }}</td>
              <td>{{ money(row.paid) }}</td>
              <td>{{ money(row.balance) }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${invoiceStatusTones[row.status]}`">{{ invoiceStatusLabels[row.status] }}</span></td>
              <td class="text-nowrap">
                <button v-if="auth.can('payments.create')" type="button" class="btn btn-soft btn-sm" :disabled="!openSession" @click="openPayment(row)">دریافت</button>
                <RouterLink v-if="auth.can('invoices.print')" :to="`/print/invoice/${row.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">فاکتور</RouterLink>
                <button v-if="row.paid === 0 && row.discount === 0 && auth.can('invoices.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="Object.assign(voiding, { row, reason: '', error: '' })">باطل</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <UiModal v-if="pay.detail" :title="`فاکتور ${pay.detail.invoice.invoiceNumber}`" @close="pay.detail = null">
    <ul class="list-unstyled small mb-3">
      <li v-for="item in pay.detail.items.filter((i) => !i.isVoid)" :key="item.id">{{ item.description }}: {{ money(item.lineTotal) }}</li>
    </ul>
    <p>مانده: <strong>{{ money(pay.detail.invoice.balance) }}</strong></p>
    <div v-if="pay.receipt" class="alert alert-success" role="status">
      دریافت ثبت شد. شماره رسید: <span class="ltr-num">{{ pay.receipt.receiptNumber }}</span>
      <RouterLink v-if="auth.can('payments.print')" :to="`/print/receipt/${pay.receipt.id}`" target="_blank" class="btn btn-soft btn-sm ms-2">چاپ رسید</RouterLink>
    </div>
    <form v-else @submit.prevent="submitPayment">
      <div class="row g-3 mb-3">
        <div class="col-6">
          <label class="form-label" for="pay-amount">مبلغ</label>
          <input id="pay-amount" v-model.number="pay.amount" class="form-control" type="number" min="0.01" step="0.01" :max="pay.detail.invoice.balance" required />
        </div>
        <div class="col-6">
          <label class="form-label" for="pay-method">روش</label>
          <select id="pay-method" v-model="pay.method" class="form-select">
            <option value="Cash">نقد</option>
            <option value="Other">غیرنقدی</option>
          </select>
        </div>
        <div v-if="pay.method === 'Other'" class="col-12">
          <label class="form-label" for="pay-ref">شماره مرجع</label>
          <input id="pay-ref" v-model="pay.reference" class="form-control" dir="ltr" maxlength="100" />
        </div>
      </div>
      <div v-if="pay.error" class="alert alert-danger" role="alert">{{ pay.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="pay.saving">ثبت دریافت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="closing.open && openSession" title="بستن صندوق" @close="closing.open = false">
    <form @submit.prevent="close">
      <p>دریافت نقدی این جلسه: {{ money(openSession.cashCollected) }}<br />مبلغ مورد انتظار: {{ money(openSession.openingAmount + openSession.cashCollected) }}</p>
      <p class="small text-muted">این مبلغ فقط موجودی آغاز و دریافت نقدی این جلسه است. برگشت وجه و مصارف از آن کم نشده است.</p>
      <label class="form-label" for="c-counted">مبلغ شمارش‌شده</label>
      <input id="c-counted" v-model.number="closing.counted" class="form-control mb-3" type="number" min="0" step="0.01" required />
      <div v-if="closing.error" class="alert alert-danger" role="alert">{{ closing.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="closing.saving">بستن صندوق</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="voiding.row" :title="`باطل کردن ${voiding.row.invoiceNumber}`" @close="voiding.row = null">
    <form @submit.prevent="submitVoid">
      <label class="form-label" for="v-reason">دلیل</label>
      <input id="v-reason" v-model="voiding.reason" class="form-control mb-3" maxlength="300" required />
      <div v-if="voiding.error" class="alert alert-danger" role="alert">{{ voiding.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="voiding.saving">باطل کردن فاکتور</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

</template>

<style scoped>
.cash-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: var(--xr-gap);
}
</style>
