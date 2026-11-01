<script setup lang="ts">
import { reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiModal from '@/components/UiModal.vue'
import { genderLabels, patientName, type Patient, type PatientListItem } from './types'

const props = defineProps<{ patient?: Patient }>()
const emit = defineEmits<{ close: []; saved: [patient: Patient] }>()

const form = reactive({
  firstName: props.patient?.firstName ?? '',
  lastName: props.patient?.lastName ?? '',
  fatherName: props.patient?.fatherName ?? '',
  gender: props.patient?.gender ?? 'Male',
  birthDate: props.patient?.birthDate ?? (null as string | null),
  estimatedAge: (props.patient && !props.patient.birthDate ? props.patient.ageYears : null) as number | null,
  phone: props.patient?.phone ?? '',
  address: props.patient?.address ?? '',
})

const saving = ref(false)
const error = ref('')
const matches = ref<PatientListItem[]>([])
const key = newIdempotencyKey()

async function save(confirmNotDuplicate = false) {
  saving.value = true
  error.value = ''
  const body = {
    ...form,
    estimatedAge: form.birthDate ? null : form.estimatedAge || null,
    confirmNotDuplicate,
    version: props.patient?.version,
  }
  try {
    const saved = props.patient
      ? await api<Patient>(`/patients/${props.patient.id}`, { method: 'PUT', body })
      : await api<Patient>('/patients', { method: 'POST', body, idempotencyKey: confirmNotDuplicate ? newIdempotencyKey() : key })
    emit('saved', saved)
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'possible_duplicate') {
      matches.value = (cause.problem?.matches as PatientListItem[]) ?? []
    } else {
      error.value = errorMessage(cause)
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiModal :title="patient ? 'ویرایش مریض' : 'مریض جدید'" @close="$emit('close')">
    <div v-if="matches.length">
      <p>مریض مشابه پیدا شد. اگر همان شخص است، از پرونده موجود استفاده کنید.</p>
      <ul class="list-unstyled">
        <li v-for="match in matches" :key="match.id" class="mb-2">
          <RouterLink :to="`/patients/${match.id}`">
            <span class="ltr-num">{{ match.fileNumber }}</span> — {{ patientName(match) }}، پدر: {{ match.fatherName ?? '—' }}
          </RouterLink>
        </li>
      </ul>
      <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
      <button type="button" class="btn btn-soft" :disabled="saving" @click="save(true)">شخص دیگری است، ثبت شود</button>
    </div>
    <form v-else @submit.prevent="save()">
      <div class="row g-3">
        <div class="col-sm-6">
          <label class="form-label" for="p-first">نام</label>
          <input id="p-first" v-model="form.firstName" class="form-control" maxlength="100" required />
        </div>
        <div class="col-sm-6">
          <label class="form-label" for="p-last">تخلص</label>
          <input id="p-last" v-model="form.lastName" class="form-control" maxlength="100" />
        </div>
        <div class="col-sm-6">
          <label class="form-label" for="p-father">نام پدر</label>
          <input id="p-father" v-model="form.fatherName" class="form-control" maxlength="100" />
        </div>
        <div class="col-sm-6">
          <label class="form-label" for="p-gender">جنسیت</label>
          <select id="p-gender" v-model="form.gender" class="form-select">
            <option v-for="(label, value) in genderLabels" :key="value" :value="value">{{ label }}</option>
          </select>
        </div>
        <div class="col-sm-8">
          <ShamsiDateInput v-model="form.birthDate" label="تاریخ تولد (اختیاری)" />
        </div>
        <div class="col-sm-4">
          <label class="form-label" for="p-age">یا سن تخمینی</label>
          <input id="p-age" v-model.number="form.estimatedAge" class="form-control" type="number" min="0" max="130" :disabled="!!form.birthDate" />
        </div>
        <div class="col-sm-6">
          <label class="form-label" for="p-phone">شماره تماس</label>
          <input id="p-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" />
        </div>
        <div class="col-12">
          <label class="form-label" for="p-address">آدرس</label>
          <input id="p-address" v-model="form.address" class="form-control" maxlength="300" />
        </div>
      </div>
      <div v-if="error" class="alert alert-danger py-2 mt-3 mb-0" role="alert">{{ error }}</div>
      <button type="submit" class="btn btn-primary mt-3" :disabled="saving">ذخیره</button>
    </form>
  </UiModal>
</template>
