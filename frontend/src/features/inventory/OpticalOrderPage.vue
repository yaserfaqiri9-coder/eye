<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import UiComboBox from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import type { OpticalRx } from '@/features/clinical/types'
import { parseOpticalNotes } from '@/features/clinical/examRecord'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { money } from '@/utils/money'
import { productSearch, type ProductOption } from './productSearch'

const route = useRoute()
const router = useRouter()

const rx = ref<OpticalRx | null>(null)
const form = reactive({
  description: '',
  items: [{ productId: null as number | null, quantity: 1 }],
  custom: [] as { description: string; amount: number | null }[],
})
const { dirty, markSaved, applying } = useUnsavedGuard(form)
const known = reactive(new Map<number, ProductOption>())
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let key = newIdempotencyKey()

const search = productSearch(['Frame', 'Lens'], (rows) => rows.forEach((row) => known.set(row.id, row)))

const lens = computed(() => parseOpticalNotes(rx.value?.notes).lens)

const total = computed(
  () =>
    form.items.reduce((sum, item) => sum + (known.get(item.productId ?? 0)?.salePrice ?? 0) * (item.quantity || 0), 0) +
    form.custom.reduce((sum, line) => sum + (line.amount || 0), 0),
)

async function loadPrescription() {
  const id = Number(route.query.rx)
  if (!id) return
  loading.value = true
  try {
    const rows = await api<OpticalRx[]>('/optical-prescriptions')
    const found = rows.find((row) => row.id === id)
    if (!found) throw new ApiError(404, 'not_found', 'این نسخه پیدا نشد. از فهرست نسخه‌ها دوباره باز کنید.')
    rx.value = found
    if (lens.value.length) applying(() => (form.description = lens.value.join('، ')))
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function submit() {
  const items = form.items.filter((item) => item.productId)
  const customLines = form.custom.filter((line) => line.description.trim() && line.amount)
  if (items.length === 0 && customLines.length === 0) {
    error.value = 'حداقل یک جنس یا یک قلم با مبلغ وارد کنید.'
    return
  }
  saving.value = true
  error.value = ''
  try {
    await api('/optical/orders', {
      method: 'POST',
      idempotencyKey: key,
      body: { patientId: rx.value?.patientId ?? null, opticalPrescriptionId: rx.value?.id ?? null, description: form.description, items, customLines },
    })
    markSaved()
    await router.push({ name: 'optical' })
  } catch (cause) {
    error.value = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) key = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}

onMounted(loadPrescription)
</script>

<template>
  <UiFormPage :title="route.query.rx ? `سفارش عینک برای ${rx?.patientName ?? ''}` : 'سفارش مستقل عینک'" :back-to="{ name: 'optical' }" back-label="بازگشت به بخش عینک" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <template v-if="rx" #summary>
      <section class="xr-card xr-card__body">
        <div class="xr-table-wrap">
          <table class="xr-clinical-table" aria-label="نسخه عینک">
            <thead><tr><th>چشم</th><th>SPH</th><th>CYL</th><th>AXIS</th><th>ADD</th><th>PD</th></tr></thead>
            <tbody>
              <tr><th scope="row"><span class="xr-eye xr-eye--od">OD</span></th><td class="ltr-num">{{ rx.sphereOd ?? '—' }}</td><td class="ltr-num">{{ rx.cylinderOd ?? '—' }}</td><td class="ltr-num">{{ rx.axisOd ?? '—' }}</td><td class="ltr-num">{{ rx.addOd ?? '—' }}</td><td class="ltr-num">{{ rx.pd ?? '—' }}</td></tr>
              <tr><th scope="row"><span class="xr-eye xr-eye--os">OS</span></th><td class="ltr-num">{{ rx.sphereOs ?? '—' }}</td><td class="ltr-num">{{ rx.cylinderOs ?? '—' }}</td><td class="ltr-num">{{ rx.axisOs ?? '—' }}</td><td class="ltr-num">{{ rx.addOs ?? '—' }}</td><td></td></tr>
            </tbody>
          </table>
        </div>
      </section>
    </template>

    <fieldset :disabled="loading">
      <div class="mb-4">
        <label class="form-label" for="oo-desc">شرح سفارش</label>
        <input id="oo-desc" v-model="form.description" class="form-control" maxlength="500" />
      </div>

      <div class="xr-group">
        <h2 class="xr-group__title">فریم و عدسی از گدام</h2>
        <table class="xr-lines">
          <thead><tr><th>جنس</th><th class="line-qty">تعداد</th><th class="line-money">مبلغ</th><th></th></tr></thead>
          <tbody>
            <tr v-for="(item, i) in form.items" :key="i">
              <td><UiComboBox v-model="item.productId" :label="`جنس ردیف ${i + 1}`" hide-label :search="search" chip placeholder="نام فریم یا عدسی را تایپ کنید" /></td>
              <td class="line-qty"><input v-model.number="item.quantity" class="form-control" type="number" min="1" step="1" :aria-label="`تعداد ردیف ${i + 1}`" /></td>
              <td class="xr-lines__money">{{ money((known.get(item.productId ?? 0)?.salePrice ?? 0) * (item.quantity || 0)) }}</td>
              <td><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف ردیف ${i + 1}`" title="حذف ردیف" :disabled="form.items.length === 1" @click="form.items.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></td>
            </tr>
          </tbody>
        </table>
        <button type="button" class="btn btn-soft btn-sm mt-2" @click="form.items.push({ productId: null, quantity: 1 })">افزودن جنس</button>
      </div>

      <div class="xr-group mb-0">
        <h2 class="xr-group__title">قلم‌های دیگر (مثلاً ساخت عدسی)</h2>
        <p v-if="form.custom.length === 0" class="form-hint">قلمی اضافه نشده است.</p>
        <div v-for="(line, i) in form.custom" :key="i" class="row g-2 mb-2">
          <div class="col"><input v-model="line.description" class="form-control" maxlength="200" :aria-label="`شرح قلم ${i + 1}`" placeholder="شرح" /></div>
          <div class="col-3"><input v-model.number="line.amount" class="form-control" type="number" min="0" step="0.01" :aria-label="`مبلغ قلم ${i + 1}`" placeholder="مبلغ" /></div>
          <div class="col-auto"><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف قلم ${i + 1}`" title="حذف قلم" @click="form.custom.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></div>
        </div>
        <button type="button" class="btn btn-soft btn-sm" @click="form.custom.push({ description: '', amount: null })">افزودن قلم</button>
      </div>
      <p class="xr-lines__total mb-0">مجموع: <strong>{{ money(total) }}</strong></p>
    </fieldset>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading">ثبت سفارش</button>
      <RouterLink :to="{ name: 'optical' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>

<style scoped>
.line-qty,
.line-money {
  width: 130px;
}
</style>
