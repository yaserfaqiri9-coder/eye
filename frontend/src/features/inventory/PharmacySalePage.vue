<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import UiComboBox from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import type { Prescription } from '@/features/clinical/types'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { useAuthStore } from '@/stores/auth'
import { money, quantity } from '@/utils/money'
import { productSearch, type ProductOption } from './productSearch'

interface SaleLine {
  productId: number | null
  quantity: number
  prescriptionItemId: number | null
  label: string
}

interface SaleDone {
  id: number
  saleNumber: string
  invoiceNumber: string
}

const route = useRoute()
const auth = useAuthStore()

const rx = ref<Prescription | null>(null)
const form = reactive({ lines: [] as SaleLine[] })
const { dirty, markSaved, applying } = useUnsavedGuard(form)
const known = reactive(new Map<number, ProductOption>())
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const done = ref<SaleDone | null>(null)
let key = newIdempotencyKey()

const search = productSearch(['Drug'], (rows) => rows.forEach((row) => known.set(row.id, row)))

const blankLine = (): SaleLine => ({ productId: null, quantity: 1, prescriptionItemId: null, label: '' })

function reset() {
  applying(() => (form.lines = [blankLine()]))
  done.value = null
  error.value = ''
  key = newIdempotencyKey()
}

const lineTotal = (line: SaleLine) => (known.get(line.productId ?? 0)?.salePrice ?? 0) * (line.quantity || 0)
const total = computed(() => form.lines.reduce((sum, line) => sum + lineTotal(line), 0))

async function loadPrescription() {
  const id = Number(route.query.rx)
  if (!id) return
  loading.value = true
  try {
    const number = typeof route.query.n === 'string' ? `?q=${encodeURIComponent(route.query.n)}` : ''
    const rows = await api<Prescription[]>(`/prescriptions${number}`)
    const found = rows.find((row) => row.id === id)
    if (!found) throw new ApiError(404, 'not_found', 'این نسخه پیدا نشد. از فهرست نسخه‌ها دوباره باز کنید.')
    rx.value = found
    applying(() => {
      form.lines = found.items
        .filter((i) => i.dispensedQuantity < i.quantity)
        .map((i) => ({ productId: null, quantity: i.quantity - i.dispensedQuantity, prescriptionItemId: i.id, label: i.drugName }))
    })
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function submit() {
  saving.value = true
  error.value = ''
  try {
    done.value = await api<SaleDone>('/pharmacy/sales', {
      method: 'POST',
      idempotencyKey: key,
      body: {
        patientId: rx.value?.patientId ?? null,
        prescriptionId: rx.value?.id ?? null,
        items: form.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, prescriptionItemId: l.prescriptionItemId })),
      },
    })
    markSaved()
  } catch (cause) {
    error.value = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) key = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  reset()
  loadPrescription()
})
</script>

<template>
  <UiFormPage
    :title="route.query.rx ? `تحویل نسخه ${rx?.rxNumber ?? ''}` : 'فروش مستقل دواخانه'"
    :back-to="{ name: 'pharmacy' }"
    back-label="بازگشت به دواخانه"
    :dirty="dirty"
    :saving="saving"
    :error="error"
    :done="!!done"
    @submit="submit"
  >
    <template v-if="rx" #summary>
      <section class="xr-card xr-card__body">
        <dl class="xr-facts">
          <div><dt>مریض</dt><dd>{{ rx.patientName }}</dd></div>
          <div><dt>شماره پرونده</dt><dd><span class="ltr-num">{{ rx.patientFileNumber }}</span></dd></div>
          <div><dt>توضیح نسخه</dt><dd>{{ rx.notes ?? '—' }}</dd></div>
        </dl>
      </section>
    </template>

    <div v-if="done" class="alert alert-success mb-0" role="status">
      فروش <span class="ltr-num">{{ done.saleNumber }}</span> ثبت شد. فاکتور <span class="ltr-num">{{ done.invoiceNumber }}</span> در صندوق قابل پرداخت است.
      <div class="xr-form-actions mt-2">
        <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${done.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ رسید</RouterLink>
        <button v-if="!rx" type="button" class="btn btn-soft btn-sm" @click="reset">فروش جدید</button>
      </div>
    </div>

    <fieldset v-else :disabled="loading">
      <div class="xr-table-wrap xr-lines-wrap">
        <table class="xr-lines">
          <thead>
            <tr><th>جنس</th><th class="line-qty">مقدار</th><th class="line-money">قیمت واحد</th><th class="line-money">مبلغ</th><th v-if="!rx"></th></tr>
          </thead>
          <tbody>
            <tr v-for="(line, i) in form.lines" :key="i">
              <td>
                <UiComboBox v-model="line.productId" :label="line.label ? `جنس برای: ${line.label}` : 'جنس'" :search="search" :hide-label="!line.label" required chip placeholder="نام یا کد جنس را تایپ کنید" />
              </td>
              <td class="line-qty" :class="{ 'line-qty--with-label': line.label }">
                <input v-model.number="line.quantity" class="form-control" type="number" min="0.001" step="any" required :aria-label="`مقدار ردیف ${i + 1}`" />
                <small v-if="known.get(line.productId ?? 0)" class="form-hint d-block">موجودی: {{ quantity(known.get(line.productId ?? 0)!.onHand) }}</small>
              </td>
              <td class="xr-lines__money">{{ money(known.get(line.productId ?? 0)?.salePrice ?? 0) }}</td>
              <td class="xr-lines__money">{{ money(lineTotal(line)) }}</td>
              <td v-if="!rx"><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف ردیف ${i + 1}`" title="حذف ردیف" :disabled="form.lines.length === 1" @click="form.lines.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></td>
            </tr>
          </tbody>
        </table>
      </div>
      <button v-if="!rx" type="button" class="btn btn-soft btn-sm mt-2" @click="form.lines.push(blankLine())">افزودن قلم</button>
      <p class="xr-lines__total mb-0">مجموع: <strong>{{ money(total) }}</strong></p>
    </fieldset>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading">ثبت فروش</button>
      <RouterLink :to="{ name: 'pharmacy' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>

<style scoped>
.line-qty {
  width: 130px;
}

.line-money {
  width: 130px;
}
</style>
