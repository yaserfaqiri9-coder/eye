<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiComboBox from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { money } from '@/utils/money'
import { productSearch } from './productSearch'

const locationLabels: Record<string, string> = { central: 'گدام مرکزی', pharmacy: 'دواخانه', optical: 'بخش عینک' }

const router = useRouter()

const blankLine = () => ({ productId: null as number | null, lot: '', expiry: null as string | null, quantity: 1, unitCost: 0 })

const suppliers = ref<{ value: number; label: string }[]>([])
const suppliersLoading = ref(true)
const form = reactive({ supplierId: null as number | null, location: 'central', lines: [blankLine()] })
const { dirty, markSaved } = useUnsavedGuard(form)
const saving = ref(false)
const error = ref('')
let key = newIdempotencyKey()

const search = productSearch([], () => {}, false)
const total = computed(() => form.lines.reduce((sum, line) => sum + (line.quantity || 0) * (line.unitCost || 0), 0))

async function submit() {
  if (!form.supplierId) { error.value = 'تأمین‌کننده را از فهرست انتخاب کنید.'; return }
  saving.value = true
  error.value = ''
  try {
    await api('/inventory/purchases', {
      method: 'POST',
      idempotencyKey: key,
      body: {
        supplierId: form.supplierId,
        locationCode: form.location,
        items: form.lines.map((l) => ({ productId: l.productId, lotNumber: l.lot, expiryDate: l.expiry, quantity: l.quantity, unitCost: l.unitCost })),
      },
    })
    markSaved()
    await router.push({ name: 'inventory' })
  } catch (cause) {
    error.value = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) key = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}
onMounted(async () => {
  try { suppliers.value = (await api<{ id: number; name: string }[]>('/suppliers?activeOnly=true')).map(s => ({ value: s.id, label: s.name })) }
  catch (e) { error.value = errorMessage(e) } finally { suppliersLoading.value = false }
})
</script>

<template>
  <UiFormPage title="ثبت خرید" :back-to="{ name: 'inventory' }" back-label="بازگشت به گدام" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <div class="row g-3 mb-4">
      <div class="col-md-6">
        <UiComboBox v-model="form.supplierId" label="تأمین‌کننده" :options="suppliers" :loading="suppliersLoading" required empty-text="تأمین‌کننده فعالی ثبت نشده است." />
        <RouterLink :to="{ name: 'suppliers' }" class="d-inline-block mt-2">مدیریت تأمین‌کنندگان</RouterLink>
      </div>
      <div class="col-md-3">
        <label class="form-label" for="pu-location">محل ورود</label>
        <select id="pu-location" v-model="form.location" class="form-select">
          <option v-for="(label, value) in locationLabels" :key="value" :value="value">{{ label }}</option>
        </select>
      </div>
    </div>

    <div class="xr-table-wrap">
      <table class="xr-lines purchase">
        <thead>
          <tr><th>جنس</th><th>شماره بچ/لات</th><th>تاریخ انقضا</th><th>مقدار</th><th>قیمت خرید واحد</th><th>مبلغ</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="(line, i) in form.lines" :key="i">
            <td class="purchase__product"><UiComboBox v-model="line.productId" :label="`جنس ردیف ${i + 1}`" hide-label :search="search" required chip placeholder="نام یا کد جنس را تایپ کنید" /></td>
            <td><input v-model="line.lot" class="form-control" dir="ltr" maxlength="50" :aria-label="`شماره بچ ردیف ${i + 1}`" /></td>
            <td class="purchase__date"><ShamsiDateInput v-model="line.expiry" :label="`تاریخ انقضا ردیف ${i + 1}`" class="purchase__date-field" /></td>
            <td><input v-model.number="line.quantity" class="form-control" type="number" min="0.001" step="any" required :aria-label="`مقدار ردیف ${i + 1}`" /></td>
            <td><input v-model.number="line.unitCost" class="form-control" type="number" min="0" step="0.01" required :aria-label="`قیمت خرید واحد ردیف ${i + 1}`" /></td>
            <td class="xr-lines__money">{{ money((line.quantity || 0) * (line.unitCost || 0)) }}</td>
            <td><button type="button" class="btn btn-soft-danger btn-icon" :aria-label="`حذف ردیف ${i + 1}`" title="حذف ردیف" :disabled="form.lines.length === 1" @click="form.lines.splice(i, 1)"><i class="ri-close-line" aria-hidden="true"></i></button></td>
          </tr>
        </tbody>
      </table>
    </div>
    <button type="button" class="btn btn-soft btn-sm mt-2" @click="form.lines.push(blankLine())">افزودن قلم</button>
    <p class="xr-lines__total mb-0">مجموع خرید: <strong>{{ money(total) }}</strong></p>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving">ثبت خرید</button>
      <RouterLink :to="{ name: 'inventory' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>

<style scoped>
.purchase {
  min-width: 860px;
}

.purchase__product {
  width: 32%;
}

.purchase__date {
  width: 170px;
}

/* In a table the column title already names the date field. */
.purchase__date-field :deep(.form-label) {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
}
</style>
