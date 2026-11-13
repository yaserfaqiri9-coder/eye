<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiComboBox from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { money } from '@/utils/money'
import { productSearch } from './productSearch'

const locationLabels: Record<string, string> = { central: 'گدام مرکزی', pharmacy: 'دواخانه', optical: 'بخش عینک' }

interface PurchaseDetail {
  purchase: { id: number; purchaseNumber: string; supplierId: number | null; supplierName: string | null; locationCode: string; isCancelled: boolean; version: number }
  items: { productId: number; productName: string; lotNumber: string; expiryDate: string | null; quantity: number; unitCost: number }[]
}

const route = useRoute()
const router = useRouter()

const blankLine = () => ({ productId: null as number | null, productName: null as string | null, lot: '', expiry: null as string | null, quantity: 1, unitCost: 0 })

const suppliers = ref<{ value: number; label: string }[]>([])
const suppliersLoading = ref(true)
const form = reactive({ supplierId: null as number | null, location: 'central', lines: [blankLine()], reason: '', note: '' })
/** Opening stock is what is already on the shelf when the system starts: no supplier, nothing owed. */
const opening = ref(route.query.opening === '1')
const { dirty, markSaved, applying } = useUnsavedGuard(form)
/** Set when this form replaces a mistaken purchase instead of recording a new one. */
const correcting = ref<PurchaseDetail | null>(null)
const correctId = Number(route.query.correct) || null
const saving = ref(false)
const error = ref('')
let key = newIdempotencyKey()

const search = productSearch([], () => {}, false)
const total = computed(() => form.lines.reduce((sum, line) => sum + (line.quantity || 0) * (line.unitCost || 0), 0))

async function submit() {
  const isOpening = opening.value && !correctId
  if (!isOpening && !form.supplierId) { error.value = 'تأمین‌کننده را از فهرست انتخاب کنید.'; return }
  saving.value = true
  error.value = ''
  const items = form.lines.map((l) => ({ productId: l.productId, lotNumber: l.lot, expiryDate: l.expiry, quantity: l.quantity, unitCost: l.unitCost || 0 }))
  const body = { supplierId: form.supplierId, locationCode: form.location, items }
  try {
    if (isOpening) {
      await api('/inventory/opening-stock', { method: 'POST', idempotencyKey: key, body: { locationCode: form.location, items, note: form.note || null } })
    } else if (correcting.value) {
      await api(`/inventory/purchases/${correcting.value.purchase.id}/correct`, { method: 'POST', body: { ...body, reason: form.reason, version: correcting.value.purchase.version } })
    } else {
      await api('/inventory/purchases', { method: 'POST', idempotencyKey: key, body })
    }
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
  try {
    suppliers.value = (await api<{ id: number; name: string }[]>('/suppliers?activeOnly=true')).map(s => ({ value: s.id, label: s.name }))
    if (correctId) {
      const detail = await api<PurchaseDetail>(`/inventory/purchases/${correctId}`)
      if (detail.purchase.isCancelled) throw new ApiError(409, 'already_cancelled', 'این خرید قبلاً لغو شده است.')
      correcting.value = detail
      applying(() => {
        form.supplierId = detail.purchase.supplierId
        form.location = detail.purchase.locationCode
        form.lines = detail.items.map((i) => ({ productId: i.productId, productName: i.productName, lot: i.lotNumber, expiry: i.expiryDate, quantity: i.quantity, unitCost: i.unitCost }))
      })
    }
  } catch (e) { error.value = errorMessage(e) } finally { suppliersLoading.value = false }
})
</script>

<template>
  <UiFormPage :title="correctId ? `اصلاح خرید ${correcting?.purchase.purchaseNumber ?? ''}` : opening ? 'ثبت موجودی اول دوره' : 'ثبت خرید'" :back-to="{ name: 'inventory' }" back-label="بازگشت به گدام" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <div v-if="correcting" class="alert alert-warning" role="status">خرید فعلی با این دلیل لغو می‌شود و خرید تازه ثبت می‌شود. اگر بخشی از آن فروخته یا منتقل شده باشد، اصلاح انجام نمی‌شود.</div>
    <div v-if="!correctId" class="xr-tabs mb-3" role="radiogroup" aria-label="نوع ورود موجودی">
      <button type="button" role="radio" class="xr-tab" :class="{ 'xr-tab--active': !opening }" :aria-checked="!opening" @click="opening = false">خرید از تأمین‌کننده</button>
      <button type="button" role="radio" class="xr-tab" :class="{ 'xr-tab--active': opening }" :aria-checked="opening" @click="opening = true">موجودی اول دوره</button>
    </div>
    <p v-if="opening && !correctId" class="text-muted small">موجودی که پیش از شروع سیستم در گدام بوده است. تأمین‌کننده لازم نیست و بدهی ثبت نمی‌شود. قیمت خرید اختیاری است؛ اگر بدانید، ارزش موجودی درست حساب می‌شود.</p>
    <div class="row g-3 mb-4">
      <div v-if="opening && !correctId" class="col-md-6">
        <label class="form-label" for="pu-note">یادداشت (اختیاری)</label>
        <input id="pu-note" v-model="form.note" class="form-control" maxlength="200" placeholder="مثلاً شمارش ۱ قوس" />
      </div>
      <div v-else class="col-md-6">
        <UiComboBox v-model="form.supplierId" label="تأمین‌کننده" :options="suppliers" :selected-label="correcting?.purchase.supplierName" :loading="suppliersLoading" required empty-text="تأمین‌کننده فعالی ثبت نشده است." />
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
            <td class="purchase__product"><UiComboBox v-model="line.productId" :label="`جنس ردیف ${i + 1}`" hide-label :search="search" :selected-label="line.productName" required chip placeholder="نام یا کد جنس را تایپ کنید" /></td>
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
    <p class="xr-lines__total mb-0">{{ opening && !correctId ? 'ارزش موجودی' : 'مجموع خرید' }}: <strong>{{ money(total) }}</strong></p>
    <div v-if="correcting" class="mt-3"><label class="form-label" for="pu-reason">دلیل اصلاح</label><input id="pu-reason" v-model="form.reason" class="form-control" minlength="3" maxlength="300" required /></div>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || (!!correctId && !correcting)">{{ correctId ? 'لغو و ثبت خرید تازه' : opening ? 'ثبت موجودی اول دوره' : 'ثبت خرید' }}</button>
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
