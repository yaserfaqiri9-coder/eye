<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useAuthStore } from '@/stores/auth'
import { money } from '@/utils/money'
import type { ServiceRow } from './types'

const router = useRouter()
const auth = useAuthStore()

const blankLine = () => ({ serviceId: null as number | null, quantity: 1 })
const form = reactive({ patientId: null as number | null, lines: [blankLine()] })
const { dirty, markSaved } = useUnsavedGuard(form)

const services = ref<ServiceRow[]>([])
const loading = ref(true)
const saving = ref(false)
const error = ref('')
let key = newIdempotencyKey()

const options = computed<ComboOption[]>(() => services.value.map((s) => ({ value: s.id, label: s.name, hint: money(s.currentPrice) })))
const priceOf = (id: number | null) => services.value.find((s) => s.id === id)?.currentPrice ?? 0
const total = computed(() => form.lines.reduce((sum, line) => sum + priceOf(line.serviceId) * (line.quantity || 0), 0))

async function submit() {
  saving.value = true
  error.value = ''
  try {
    await api('/invoices', {
      method: 'POST',
      idempotencyKey: key,
      body: { patientId: form.patientId, items: form.lines.map((l) => ({ serviceId: l.serviceId, quantity: l.quantity })) },
    })
    markSaved()
    await router.push({ name: 'cashier' })
  } catch (cause) {
    error.value = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) key = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    // Prices decide the amount charged, so they are read fresh when the form opens.
    services.value = (await api<ServiceRow[]>('/services')).filter((s) => s.isActive && s.currentPrice !== null)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <UiFormPage title="فاکتور مستقل" :back-to="{ name: 'cashier' }" back-label="بازگشت به صندوق" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <div v-if="auth.can('patients.view')" class="row mb-4">
      <div class="col-md-6"><PatientPicker v-model="form.patientId" label="مریض (اختیاری)" /></div>
    </div>

    <table class="xr-lines">
      <thead><tr><th>خدمت</th><th class="line-qty">تعداد</th><th class="line-money">قیمت واحد</th><th class="line-money">مبلغ</th><th></th></tr></thead>
      <tbody>
        <tr v-for="(line, i) in form.lines" :key="i">
          <td><UiComboBox v-model="line.serviceId" :label="`خدمت ردیف ${i + 1}`" hide-label :options="options" :loading="loading" required chip /></td>
          <td class="line-qty"><input v-model.number="line.quantity" class="form-control" type="number" min="1" step="1" required :aria-label="`تعداد ردیف ${i + 1}`" /></td>
          <td class="xr-lines__money">{{ money(priceOf(line.serviceId)) }}</td>
          <td class="xr-lines__money">{{ money(priceOf(line.serviceId) * (line.quantity || 0)) }}</td>
          <td><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف ردیف ${i + 1}`" title="حذف ردیف" :disabled="form.lines.length === 1" @click="form.lines.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></td>
        </tr>
      </tbody>
    </table>
    <button type="button" class="btn btn-soft btn-sm mt-2" @click="form.lines.push(blankLine())">افزودن قلم</button>
    <p class="xr-lines__total mb-0">مجموع: <strong>{{ money(total) }}</strong></p>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading">صدور فاکتور</button>
      <RouterLink :to="{ name: 'cashier' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>

<style scoped>
.line-qty,
.line-money {
  width: 130px;
}
</style>
