<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'
import type { FinancialReport, OperationsReport } from '@/features/reports/types'
import { roleLabel } from '@/features/users/roles'
import { useAuthStore } from '@/stores/auth'
import { money } from '@/utils/money'

const auth = useAuthStore()
const operations = ref<OperationsReport | null>(null)
const financial = ref<FinancialReport | null>(null)
const error = ref('')

async function load() {
  try {
    if (auth.can('reports.view')) operations.value = await api<OperationsReport>('/reports/operations')
    if (auth.can('ledger.view')) financial.value = await api<FinancialReport>('/reports/financial')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PaymentReceived'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <UiCard title="خوش آمدید">
      <p class="mb-1">{{ auth.user?.fullName }}</p>
      <p class="mb-0 text-muted">نقش: {{ auth.user?.roles.map(roleLabel).join('، ') }}</p>
    </UiCard>

    <div v-if="error" class="alert alert-danger py-2 mb-0" role="alert">{{ error }}</div>

    <div v-if="operations" class="home-stats">
      <StatCard label="مراجعات امروز" :value="operations.visits" icon="bi-people-fill" />
      <StatCard label="تکمیل‌شده امروز" :value="operations.completedVisits" icon="bi-check-lg" tone="cyan" />
      <StatCard label="اجناس کم‌موجود" :value="operations.lowStockProducts" icon="bi-box-seam-fill" tone="orange" />
      <StatCard v-if="financial" label="دریافت امروز" :value="money(financial.receipts)" icon="bi-cash-coin" />
      <StatCard v-if="financial" label="مصارف امروز" :value="money(financial.expenses)" icon="bi-wallet2" tone="red" />
      <StatCard v-if="financial" label="باقی حساب‌ها" :value="money(financial.outstandingNow)" icon="bi-receipt" tone="orange" />
    </div>

    <UiCard v-if="operations && operations.visitsByDepartment.length" title="وضعیت بخش‌ها امروز">
      <table class="xr-table"><tbody><tr v-for="row in operations.visitsByDepartment" :key="row.name"><td>{{ row.name }}</td><td>{{ row.count }} مراجعه</td></tr></tbody></table>
    </UiCard>
  </div>
</template>

<style scoped>
.home-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: var(--xr-gap);
}
</style>
