<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useAuthStore } from '@/stores/auth'
import { money } from '@/utils/money'
import type { InvoiceDetail, ServiceRow } from './types'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const blankLine = () => ({ serviceId: null as number | null, quantity: 1 })
const form = reactive({ patientId: null as number | null, lines: [blankLine()], reason: '' })
const { dirty, markSaved, applying } = useUnsavedGuard(form)

/** Set when this form replaces a mistaken invoice instead of issuing a new one. */
const correcting = ref<InvoiceDetail | null>(null)
const correctId = computed(() => Number(route.query.correct) || null)

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
  const items = form.lines.map((l) => ({ serviceId: l.serviceId, quantity: l.quantity }))
  try {
    if (correcting.value) {
      await api(`/invoices/${correcting.value.invoice.id}/correct`, { method: 'POST', body: { patientId: form.patientId, items, reason: form.reason, version: correcting.value.invoice.version } })
    } else {
      await api('/invoices', { method: 'POST', idempotencyKey: key, body: { patientId: form.patientId, items } })
    }
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
    if (correctId.value) {
      const detail = await api<InvoiceDetail>(`/invoices/${correctId.value}`)
      if (!detail.correctable) throw new ApiError(409, 'invoice_not_standalone', 'این فاکتور از بخش دیگری صادر شده است. آن را از همان بخش اصلاح یا لغو کنید.')
      correcting.value = detail
      applying(() => {
        form.patientId = detail.invoice.patientId
        form.lines = detail.items.filter((i) => !i.isVoid).map((i) => ({ serviceId: i.serviceId, quantity: i.quantity }))
      })
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <UiFormPage :title="correctId ? `اصلاح فاکتور ${correcting?.invoice.invoiceNumber ?? ''}` : 'فاکتور مستقل'" :back-to="{ name: 'cashier' }" back-label="بازگشت به صندوق" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <div v-if="correcting" class="alert alert-warning" role="status">فاکتور فعلی با این دلیل باطل می‌شود و فاکتور تازه با قیمت‌های امروز صادر می‌شود. سابقه هر دو می‌ماند.</div>
    <div v-if="auth.can('patients.view')" class="row mb-4">
      <div class="col-md-6"><PatientPicker v-model="form.patientId" label="مریض (اختیاری)" :selected-label="correcting?.invoice.patientName" /></div>
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
    <div v-if="correcting" class="mt-3"><label class="form-label" for="inv-reason">دلیل اصلاح</label><input id="inv-reason" v-model="form.reason" class="form-control" minlength="3" maxlength="300" required /></div>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading || (!!correctId && !correcting)">{{ correctId ? 'باطل کردن و صدور فاکتور تازه' : 'صدور فاکتور' }}</button>
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
