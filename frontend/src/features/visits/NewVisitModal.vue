<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { departmentOptions, doctorOptions } from '@/api/lookups'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { money, quantity } from '@/utils/money'
import type { VisitRow, VisitToken } from './types'

const props = defineProps<{ patient?: { id: number; label: string }; token?: VisitToken }>()
const emit = defineEmits<{ close: []; saved: [visit: VisitRow]; changed: [] }>()

const departments = ref<ComboOption[]>([])
const doctors = ref<ComboOption[]>([])
const services = ref<ComboOption[]>([])
const listsLoading = ref(true)
const fee = ref<number | null>(null)
const token = ref<VisitToken | null>(props.token ?? null)
const form = reactive({ patientId: props.patient?.id ?? null, departmentId: props.token?.departmentId ?? null as number | null, doctorStaffId: props.token?.doctorStaffId ?? null as number | null, serviceId: null as number | null, reason: '' })
const saving = ref(false)
const error = ref('')
const tokenKey = ref(newIdempotencyKey())
const payKey = ref(newIdempotencyKey())
const visitKey = ref(newIdempotencyKey())

const paid = computed(() => token.value?.paid === true)

function retryable(cause: unknown, key: typeof tokenKey) {
  // A definite refusal may be retried with new values; an unknown outcome keeps the key so a retry cannot double-charge.
  if (cause instanceof ApiError && !cause.outcomeUnknown) key.value = newIdempotencyKey()
}

/** Issues the token if there is none yet, then collects the fee in full. */
async function collect() {
  saving.value = true
  error.value = ''
  try {
    if (!token.value) {
      try {
        token.value = await api<VisitToken>('/visit-tokens', {
          method: 'POST',
          idempotencyKey: tokenKey.value,
          body: { departmentId: form.departmentId, doctorStaffId: form.doctorStaffId },
        })
        emit('changed')
      } catch (cause) {
        retryable(cause, tokenKey)
        throw cause
      }
    }
    try {
      await api('/reception-fees/pay', {
        method: 'POST',
        idempotencyKey: payKey.value,
        body: { invoiceId: token.value.invoiceId, amount: token.value.balance, method: 'Cash', invoiceVersion: token.value.invoiceVersion },
      })
      token.value = await api<VisitToken>(`/visit-tokens/${token.value.id}`)
      emit('changed')
    } catch (cause) {
      retryable(cause, payKey)
      if (cause instanceof ApiError && cause.code === 'invoice_changed') token.value = await api<VisitToken>(`/visit-tokens/${token.value.id}`)
      throw cause
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

async function save() {
  if (!form.patientId) {
    error.value = 'مریض را انتخاب کنید.'
    return
  }
  saving.value = true
  error.value = ''
  try {
    const visit = await api<VisitRow>('/visits', {
      method: 'POST',
      idempotencyKey: visitKey.value,
      body: { patientId: form.patientId, departmentId: token.value!.departmentId, doctorStaffId: form.doctorStaffId, serviceId: form.serviceId, reason: form.reason, tokenId: token.value!.id },
    })
    emit('saved', visit)
  } catch (cause) {
    error.value = errorMessage(cause)
    retryable(cause, visitKey)
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    // Prices are read fresh each time; departments and doctors come from the shared short-lived list.
    const [loadedDepartments, loadedDoctors, loadedServices, loadedFee] = await Promise.all([
      departmentOptions(),
      doctorOptions(),
      api<{ id: number; name: string; code: string; isActive: boolean; currentPrice: number | null }[]>('/services'),
      token.value ? Promise.resolve(null) : api<{ price: number }>('/visit-tokens/fee'),
    ])
    departments.value = loadedDepartments
    doctors.value = loadedDoctors
    services.value = loadedServices.filter((s) => s.isActive && s.currentPrice !== null).map((s) => ({ value: s.id, label: s.name, hint: money(s.currentPrice) }))
    fee.value = loadedFee?.price ?? null
    // Most patients start at Vision, so the desk does not have to choose each time.
    if (!form.departmentId) form.departmentId = loadedDepartments.find((d) => d.label === 'معاینه ابتدایی')?.value ?? null
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    listsLoading.value = false
  }
})
</script>

<template>
  <UiModal :title="paid ? `شماره صف ${quantity(token!.queueNumber)}: ثبت مشخصات مریض` : 'پذیرش حضوری و دریافت فیس ویزیت'" @close="$emit('close')">
    <form v-if="!paid" @submit.prevent="collect">
      <template v-if="!token">
        <div class="mb-3"><UiComboBox v-model="form.departmentId" label="بخش" :options="departments" :loading="listsLoading" required chip /></div>
        <div class="mb-3"><UiComboBox v-model="form.doctorStaffId" label="داکتر (اختیاری)" :options="doctors" :loading="listsLoading" chip placeholder="بدون انتخاب" /></div>
      </template>
      <p>فیس ویزیت: <strong>{{ money(token ? token.balance : fee) }}</strong></p>
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving || listsLoading">{{ token ? 'دریافت فیس' : 'ساخت شماره صف و دریافت فیس' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>

    <form v-else @submit.prevent="save">
      <div class="alert alert-success" role="status">فیس ویزیت دریافت شد. {{ token!.departmentName }}، نوبت {{ quantity(token!.queueNumber) }}</div>
      <div class="mb-3">
        <template v-if="patient">
          <div class="form-label">مریض</div>
          <strong>{{ patient.label }}</strong>
        </template>
        <template v-else>
          <PatientPicker v-model="form.patientId" required />
          <RouterLink :to="{ name: 'patient-new', query: { token: token!.id } }" class="btn btn-soft btn-sm mt-2">مریض جدید</RouterLink>
        </template>
      </div>
      <div class="mb-3"><UiComboBox v-model="form.doctorStaffId" label="داکتر (اختیاری)" :options="doctors" :loading="listsLoading" chip placeholder="بدون انتخاب" /></div>
      <div class="mb-3"><UiComboBox v-model="form.serviceId" label="خدمت اضافی قابل پرداخت (اختیاری)" :options="services" :loading="listsLoading" chip placeholder="بدون خدمت اضافی" /></div>
      <div class="mb-3">
        <label class="form-label" for="v-reason">علت مراجعه</label>
        <input id="v-reason" v-model="form.reason" class="form-control" maxlength="300" />
      </div>
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving">ثبت مراجعه</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>بعداً</button>
      </div>
    </form>
  </UiModal>
</template>
