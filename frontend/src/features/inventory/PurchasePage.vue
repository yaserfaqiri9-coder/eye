<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiComboBox from '@/components/UiComboBox.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { money } from '@/utils/money'
import { productGroupLabels, stockUnits } from './drugForms'
import { productSearch } from './productSearch'

/** A product typed on the form that is not in the list yet; the server adds it with the purchase. */
interface NewProduct {
  name: string
  kind: string
  unit: string
  salePrice: number | null
}

const locationLabels: Record<string, string> = { central: 'گدام مرکزی', pharmacy: 'دواخانه', optical: 'بخش عینک' }

interface PurchaseDetail {
  purchase: { id: number; purchaseNumber: string; supplierId: number | null; supplierName: string | null; locationCode: string; isCancelled: boolean; version: number }
  items: { productId: number; productName: string; lotNumber: string; expiryDate: string | null; quantity: number; unitCost: number }[]
}

const route = useRoute()
const router = useRouter()

const blankLine = () => ({ productId: null as number | null, productName: null as string | null, newProduct: null as NewProduct | null, lot: '', expiry: null as string | null, quantity: 1, unitCost: null as number | null })

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

type Line = ReturnType<typeof blankLine>

const isOptical = (kind: string) => kind === 'Frame' || kind === 'Lens'

function addNewProduct(line: Line, name: string) {
  line.productId = null
  line.newProduct = { name, kind: form.location === 'optical' ? 'Frame' : 'Drug', unit: stockUnits[0], salePrice: null }
}

async function submit() {
  const isOpening = opening.value && !correctId
  if (!isOpening && !form.supplierId) { error.value = 'تأمین‌کننده را از فهرست انتخاب کنید.'; return }
  saving.value = true
  error.value = ''
  const items = form.lines.map((l) => ({
    productId: l.newProduct ? 0 : l.productId,
    newProduct: l.newProduct ? { name: l.newProduct.name, kind: l.newProduct.kind, unit: l.newProduct.unit, salePrice: l.newProduct.salePrice ?? 0 } : null,
    lotNumber: l.lot,
    expiryDate: l.expiry,
    quantity: l.quantity,
    unitCost: l.unitCost || 0,
  }))
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
    await router.push({ name: 'purchases' })
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
        form.lines = detail.items.map((i) => ({ productId: i.productId, productName: i.productName, newProduct: null, lot: i.lotNumber, expiry: i.expiryDate, quantity: i.quantity, unitCost: i.unitCost }))
      })
    }
  } catch (e) { error.value = errorMessage(e) } finally { suppliersLoading.value = false }
})
</script>

<template>
  <UiFormPage :title="correctId ? `اصلاح خرید ${correcting?.purchase.purchaseNumber ?? ''}` : opening ? 'ثبت موجودی اول دوره' : 'ثبت خرید'" :back-to="{ name: 'purchases' }" back-label="بازگشت به خریدها" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <div v-if="correcting" class="alert alert-warning" role="status">خرید فعلی با این دلیل لغو می‌شود و خرید تازه ثبت می‌شود. اگر بخشی از آن فروخته یا منتقل شده باشد، اصلاح انجام نمی‌شود.</div>
    <div v-if="!correctId" class="xr-tabs mb-3" role="radiogroup" aria-label="نوع ورود موجودی">
      <button type="button" role="radio" class="xr-tab" :class="{ 'xr-tab--active': !opening }" :aria-checked="!opening" @click="opening = false">خرید از تأمین‌کننده</button>
      <button type="button" role="radio" class="xr-tab" :class="{ 'xr-tab--active': opening }" :aria-checked="opening" @click="opening = true">موجودی اول دوره</button>
    </div>
    <p v-if="opening && !correctId" class="text-muted small">موجودی که پیش از شروع سیستم در گدام بوده است. تأمین‌کننده لازم نیست و بدهی ثبت نمی‌شود. قیمت خرید اختیاری است؛ اگر بدانید، ارزش موجودی درست حساب می‌شود.</p>
    <div class="row g-3 mb-4">
      <div v-if="opening && !correctId" class="col-md-6">
        <label class="form-label" for="pu-note">یادداشت (اختیاری)</label>
        <input id="pu-note" v-model="form.note" class="form-control" maxlength="200" placeholder="مثلاً شمارش 1 قوس" />
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
            <td class="purchase__product">
              <div v-if="line.newProduct" class="new-product" role="group" :aria-label="`جنس جدید ردیف ${i + 1}`">
                <div class="new-product__head">
                  <span class="xr-badge xr-badge--green">جنس جدید</span>
                  <input v-model.trim="line.newProduct.name" class="form-control form-control-sm" maxlength="200" required :aria-label="`نام جنس جدید ردیف ${i + 1}`" />
                  <button type="button" class="btn btn-quiet btn-icon btn-sm" title="انتخاب از فهرست" :aria-label="`لغو جنس جدید ردیف ${i + 1}`" @click="line.newProduct = null"><i class="ri-close-line" aria-hidden="true"></i></button>
                </div>
                <div class="new-product__fields">
                  <select v-model="line.newProduct.kind" class="form-select form-select-sm" :aria-label="`گروه جنس ردیف ${i + 1}`">
                    <option v-for="(label, value) in productGroupLabels" :key="value" :value="value">{{ label }}</option>
                  </select>
                  <select v-model="line.newProduct.unit" class="form-select form-select-sm" :aria-label="`واحد ردیف ${i + 1}`">
                    <option v-for="unit in stockUnits" :key="unit" :value="unit">{{ unit }}</option>
                  </select>
                  <!-- Optical goods are priced at the counter, so their sale price may stay empty; a drug without one would sell for nothing. -->
                  <input v-model.number="line.newProduct.salePrice" class="form-control form-control-sm" type="number" min="0" step="0.01" :required="!isOptical(line.newProduct.kind)" :placeholder="isOptical(line.newProduct.kind) ? 'قیمت فروش (اختیاری)' : 'قیمت فروش'" :aria-label="`قیمت فروش ردیف ${i + 1}`" />
                </div>
              </div>
              <UiComboBox v-else v-model="line.productId" :label="`جنس ردیف ${i + 1}`" hide-label :search="search" :selected-label="line.productName" required chip placeholder="نام یا کد جنس را تایپ کنید" create-label="افزودن جنس جدید" @create="addNewProduct(line, $event)" />
            </td>
            <td><input v-model="line.lot" class="form-control" dir="ltr" maxlength="50" :aria-label="`شماره بچ ردیف ${i + 1}`" /></td>
            <td class="purchase__date"><ShamsiDateInput v-model="line.expiry" :label="`تاریخ انقضا ردیف ${i + 1}`" class="purchase__date-field" /></td>
            <td><input v-model.number="line.quantity" class="form-control" type="number" min="0.001" step="any" required :aria-label="`مقدار ردیف ${i + 1}`" /></td>
            <td><input v-model.number="line.unitCost" class="form-control" type="number" min="0" step="0.01" placeholder="اختیاری" :aria-label="`قیمت خرید واحد ردیف ${i + 1}`" /></td>
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
      <RouterLink :to="{ name: 'purchases' }" class="btn btn-soft-danger">انصراف</RouterLink>
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

.new-product {
  display: grid;
  gap: 6px;
}

.new-product__head,
.new-product__fields {
  display: flex;
  align-items: center;
  gap: 6px;
}

.new-product__head .form-control {
  flex: 1;
}

.new-product__fields > * {
  flex: 1;
  min-width: 0;
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
