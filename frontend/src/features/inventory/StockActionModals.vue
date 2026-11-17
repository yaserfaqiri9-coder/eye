<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import UiModal from '@/components/UiModal.vue'
import { formatDate } from '@/utils/date'
import { quantity } from '@/utils/money'

interface BatchRow {
  id: number
  productName: string
  locationCode: string
  locationName: string
  lotNumber: string
  expiryDate: string | null
  quantity: number
}

/** Transfer and adjustment for a product row: the batch is picked inside the dialog when the product has more than one. */
const emit = defineEmits<{ done: [] }>()

const mode = ref<'transfer' | 'adjust' | null>(null)
const title = ref('')
const batches = ref<BatchRow[]>([])
const destinations = ref<{ code: string; name: string }[]>([])
const loading = ref(false)
const form = reactive({
  batchId: 0, to: '', quantity: 1, kind: 'Adjustment', direction: 'decrease' as 'decrease' | 'increase', reason: '', saving: false, error: '', key: '',
})

const batch = computed(() => batches.value.find((row) => row.id === form.batchId) ?? null)
const targets = computed(() => destinations.value.filter((row) => row.code !== batch.value?.locationCode))
const signed = computed(() => (form.kind === 'Consumption' || form.direction === 'decrease' ? -Math.abs(form.quantity) : Math.abs(form.quantity)))
const after = computed(() => (batch.value ? batch.value.quantity + signed.value : 0))

function batchLabel(row: BatchRow) {
  return `${row.locationName} — بچ ${row.lotNumber || '—'} — انقضا ${formatDate(row.expiryDate)} — ${quantity(row.quantity)}`
}

async function open(kind: 'transfer' | 'adjust', productId: number, productName: string, location?: string) {
  mode.value = kind
  title.value = `${kind === 'transfer' ? 'انتقال' : 'تعدیل'} ${productName}`
  batches.value = []
  loading.value = true
  Object.assign(form, { batchId: 0, to: '', quantity: kind === 'transfer' ? 1 : 0, kind: 'Adjustment', direction: 'decrease', reason: '', saving: false, error: '', key: newIdempotencyKey() })
  try {
    const params = new URLSearchParams({ productId: String(productId) })
    if (location) params.set('location', location)
    const [rows, places] = await Promise.all([
      api<BatchRow[]>(`/inventory/batches?${params}`),
      destinations.value.length ? Promise.resolve(destinations.value) : api<{ code: string; name: string }[]>('/inventory/locations'),
    ])
    batches.value = rows
    destinations.value = places
    form.batchId = rows[0]?.id ?? 0
    form.to = targets.value[0]?.code ?? ''
    if (!rows.length) form.error = 'این جنس موجودی ندارد.'
  } catch (cause) {
    form.error = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function pickBatch() {
  if (!targets.value.some((row) => row.code === form.to)) form.to = targets.value[0]?.code ?? ''
}

async function save() {
  form.saving = true
  form.error = ''
  try {
    if (mode.value === 'transfer') {
      await api('/inventory/transfers', { method: 'POST', idempotencyKey: form.key, body: { batchId: form.batchId, toLocationCode: form.to, quantity: form.quantity } })
    } else {
      await api('/inventory/adjustments', { method: 'POST', idempotencyKey: form.key, body: { batchId: form.batchId, quantity: signed.value, kind: form.kind, reason: form.reason } })
    }
    mode.value = null
    emit('done')
  } catch (cause) {
    form.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) form.key = newIdempotencyKey()
  } finally {
    form.saving = false
  }
}

defineExpose({ open })
</script>

<template>
  <UiModal v-if="mode" :title="title" @close="mode = null">
    <p v-if="loading" class="xr-state" role="status">در حال بارگذاری...</p>
    <form v-else @submit.prevent="save">
      <template v-if="batch">
        <div v-if="batches.length > 1" class="mb-3">
          <label class="form-label" for="sa-batch">بچ</label>
          <select id="sa-batch" v-model.number="form.batchId" class="form-select" @change="pickBatch">
            <option v-for="row in batches" :key="row.id" :value="row.id">{{ batchLabel(row) }}</option>
          </select>
        </div>
        <p v-else>{{ batchLabel(batch) }}</p>

        <template v-if="mode === 'transfer'">
          <div class="mb-3">
            <label class="form-label" for="sa-to">مقصد</label>
            <select id="sa-to" v-model="form.to" class="form-select" required>
              <option v-for="row in targets" :key="row.code" :value="row.code">{{ row.name }}</option>
            </select>
          </div>
          <div class="mb-3">
            <label class="form-label" for="sa-qty">مقدار</label>
            <input id="sa-qty" v-model.number="form.quantity" class="form-control" dir="ltr" type="number" min="0.001" step="any" :max="batch.quantity" required />
          </div>
        </template>

        <template v-else>
          <div class="mb-3">
            <label class="form-label" for="sa-kind">نوع</label>
            <select id="sa-kind" v-model="form.kind" class="form-select"><option value="Adjustment">تعدیل شمارش</option><option value="Consumption">مصرف بخش</option></select>
          </div>
          <div class="row g-3 mb-3">
            <div v-if="form.kind === 'Adjustment'" class="col-6">
              <label class="form-label" for="sa-dir">جهت</label>
              <select id="sa-dir" v-model="form.direction" class="form-select"><option value="decrease">کاهش موجودی</option><option value="increase">افزایش موجودی</option></select>
            </div>
            <div :class="form.kind === 'Adjustment' ? 'col-6' : 'col-12'">
              <label class="form-label" for="sa-qty">مقدار</label>
              <input id="sa-qty" v-model.number="form.quantity" class="form-control" dir="ltr" type="number" min="0.001" step="any" required />
            </div>
          </div>
          <p class="form-hint mb-3" :class="{ 'form-warning': after < 0 }">موجودی پس از ثبت: {{ quantity(after) }}</p>
          <div class="mb-3"><label class="form-label" for="sa-reason">دلیل</label><input id="sa-reason" v-model="form.reason" class="form-control" minlength="3" maxlength="300" required /></div>
        </template>
      </template>

      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="xr-form-actions">
        <button v-if="batch" type="submit" class="btn btn-primary" :disabled="form.saving">{{ mode === 'transfer' ? 'ثبت انتقال' : 'ثبت تعدیل' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
