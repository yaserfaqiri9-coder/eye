<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDate, todayShamsi } from '@/utils/date'
import { money } from '@/utils/money'
import type { FinancialReport, OperationsReport } from './types'

const auth = useAuthStore()
const today = todayShamsi()
const from = ref<string | null>(null)
const to = ref<string | null>(null)
const operations = ref<OperationsReport | null>(null)
const financial = ref<FinancialReport | null>(null)
const loading = ref(false)
const error = ref('')

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams()
    if (from.value) params.set('from', from.value)
    if (to.value) params.set('to', to.value)
    operations.value = await api<OperationsReport>(`/reports/operations?${params}`)
    if (auth.can('ledger.view')) financial.value = await api<FinancialReport>(`/reports/financial?${params}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function print() {
  window.print()
}

onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <UiCard title="گزارش‌ها" class="no-print">
      <form class="row g-3 align-items-end" @submit.prevent="load">
        <div class="col-md-5"><ShamsiDateInput v-model="from" label="از تاریخ" :initial="today" /></div>
        <div class="col-md-5"><ShamsiDateInput v-model="to" label="تا تاریخ" :initial="today" /></div>
        <div class="col-md-2 d-flex gap-2">
          <button type="submit" class="btn btn-primary" :disabled="loading">نمایش</button>
          <button v-if="auth.can('reports.print')" type="button" class="btn btn-soft" @click="print">چاپ</button>
        </div>
      </form>
    </UiCard>

    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <template v-if="operations">
      <p class="mb-0">دوره گزارش: {{ formatDate(operations.from) }} تا {{ formatDate(operations.to) }}</p>
      <div class="report-stats">
        <StatCard label="مراجعات" :value="operations.visits" icon="ri-group-fill" />
        <StatCard label="مراجعات تکمیل‌شده" :value="operations.completedVisits" icon="ri-check-line" tone="cyan" />
        <StatCard label="عملیات" :value="operations.surgeries" icon="ri-surgical-mask-fill" tone="orange" />
        <StatCard label="نسخه‌های دوا" :value="operations.prescriptions" icon="ri-capsule-fill" tone="red" />
      </div>

      <div class="report-columns">
        <UiCard title="مراجعات بر اساس بخش">
          <p v-if="operations.visitsByDepartment.length === 0" class="text-muted mb-0">موردی نیست.</p>
          <table v-else class="xr-table"><tbody><tr v-for="row in operations.visitsByDepartment" :key="row.name"><td>{{ row.name }}</td><td>{{ row.count }}</td></tr></tbody></table>
        </UiCard>
        <UiCard title="آزمایش‌ها">
          <p v-if="operations.diagnosticsByTest.length === 0" class="text-muted mb-0">موردی نیست.</p>
          <table v-else class="xr-table"><tbody><tr v-for="row in operations.diagnosticsByTest" :key="row.name"><td>{{ row.name }}</td><td>{{ row.count }}</td></tr></tbody></table>
        </UiCard>
      </div>

      <UiCard title="فعالیت داکتران">
        <p v-if="operations.doctors.length === 0" class="text-muted mb-0">موردی نیست.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>داکتر</th><th>مراجعات</th><th>معاینات نهایی‌شده</th></tr></thead>
            <tbody><tr v-for="row in operations.doctors" :key="row.doctor"><td>{{ row.doctor }}</td><td>{{ row.visits }}</td><td>{{ row.finalized }}</td></tr></tbody>
          </table>
        </div>
      </UiCard>
    </template>

    <template v-if="financial">
      <UiCard title="گزارش مالی">
        <div class="xr-table-wrap">
          <table class="xr-table">
            <tbody>
              <tr><td>درآمد خدمات</td><td>{{ money(financial.serviceRevenue) }}</td></tr>
              <tr><td>فروش دواخانه</td><td>{{ money(financial.pharmacyRevenue) }}</td></tr>
              <tr><td>فروش عینک</td><td>{{ money(financial.opticalRevenue) }}</td></tr>
              <tr><td>تخفیف‌ها</td><td>{{ money(financial.discounts) }}</td></tr>
              <tr><td>برگشت وجه</td><td>{{ money(financial.refunds) }}</td></tr>
              <tr><th>درآمد خالص</th><th>{{ money(financial.netRevenue) }}</th></tr>
              <tr><td>قیمت تمام‌شده فروش</td><td>{{ money(financial.costOfGoods) }}</td></tr>
              <tr><td>مصارف</td><td>{{ money(financial.expenses) }}</td></tr>
              <tr><td>دریافت‌های صندوق</td><td>{{ money(financial.receipts) }}</td></tr>
              <tr><td>باقی حساب‌ها (اکنون)</td><td>{{ money(financial.outstandingNow) }} — {{ financial.openInvoicesNow }} فاکتور</td></tr>
              <tr><td>ارزش موجودی (اکنون)</td><td>{{ money(financial.stockValueNow) }}</td></tr>
            </tbody>
          </table>
        </div>
      </UiCard>

      <UiCard v-if="financial.receiptsByCashier.length" title="دریافت‌ها بر اساس صندوق‌دار">
        <table class="xr-table"><tbody><tr v-for="row in financial.receiptsByCashier" :key="row.cashier"><td>{{ row.cashier }}</td><td>{{ money(row.total) }}</td></tr></tbody></table>
      </UiCard>
    </template>
  </div>
</template>

<style scoped>
.report-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--xr-gap);
}

.report-columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: var(--xr-gap);
}
</style>
