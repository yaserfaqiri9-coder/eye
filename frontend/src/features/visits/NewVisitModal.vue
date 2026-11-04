<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { doctorOptions } from '@/api/lookups'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { quantity } from '@/utils/money'
import type { VisitRow, VisitToken } from './types'

/** Completes a paid token issued before the patient-first flow. New patients go through AdmissionFlow. */
const props = defineProps<{ token: VisitToken }>()
const emit = defineEmits<{ close: []; saved: [visit: VisitRow] }>()

const doctors = ref<ComboOption[]>([])
const form = reactive({ patientId: null as number | null, doctorStaffId: props.token.doctorStaffId, reason: '' })
const saving = ref(false)
const error = ref('')
const visitKey = ref(newIdempotencyKey())

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
      body: { patientId: form.patientId, departmentId: props.token.departmentId, doctorStaffId: form.doctorStaffId, reason: form.reason, tokenId: props.token.id },
    })
    emit('saved', visit)
  } catch (cause) {
    error.value = errorMessage(cause)
    // An unknown outcome keeps the key so a retry opens one visit.
    if (cause instanceof ApiError && !cause.outcomeUnknown) visitKey.value = newIdempotencyKey()
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    doctors.value = await doctorOptions()
  } catch (cause) {
    error.value = errorMessage(cause)
  }
})
</script>

<template>
  <UiModal :title="`نوبت ${quantity(token.queueNumber)}: ثبت مشخصات مریض`" @close="$emit('close')">
    <form @submit.prevent="save">
      <div class="alert alert-success" role="status">فیس این نوبت قبلاً دریافت شده است. {{ token.departmentName }}</div>
      <div class="mb-3">
        <PatientPicker v-model="form.patientId" required />
        <RouterLink :to="{ name: 'patient-new', query: { token: token.id } }" class="btn btn-soft btn-sm mt-2">مریض جدید</RouterLink>
      </div>
      <div class="mb-3"><UiComboBox v-model="form.doctorStaffId" label="داکتر (اختیاری)" :options="doctors" chip placeholder="بدون انتخاب" /></div>
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
