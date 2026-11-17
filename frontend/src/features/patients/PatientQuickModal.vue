<script setup lang="ts">
import { reactive, ref } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiModal from '@/components/UiModal.vue'
import { todayIso } from '@/utils/date'
import { genderLabels, patientName, type Patient, type PatientListItem } from './types'

/**
 * Registers a patient without leaving the reception desk. The same request and the same duplicate check
 * as the full patient form, so a person is never given two files from here either.
 */
const emit = defineEmits<{ close: []; chosen: [patientId: number] }>()

const form = reactive({
  firstName: '',
  lastName: '',
  fatherName: '',
  gender: 'Male',
  birthDate: null as string | null,
  estimatedAge: null as number | null,
  phone: '',
  address: '',
  occupation: '',
})

const saving = ref(false)
const error = ref('')
const matches = ref<PatientListItem[]>([])
const key = newIdempotencyKey()
// Confirming changes the request, so it needs its own key, but a retry of the confirmation must still register one patient.
const confirmKey = newIdempotencyKey()
const today = todayIso()

async function save(confirmNotDuplicate = false) {
  saving.value = true
  error.value = ''
  const body = { ...form, estimatedAge: form.birthDate ? null : form.estimatedAge || null, confirmNotDuplicate }
  try {
    const saved = await api<Patient>('/patients', { method: 'POST', body, idempotencyKey: confirmNotDuplicate ? confirmKey : key })
    emit('chosen', saved.id)
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'possible_duplicate') matches.value = (cause.problem?.matches as PatientListItem[]) ?? []
    else error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiModal title="ثبت مریض جدید" size="lg" @close="$emit('close')">
    <div v-if="matches.length" role="alert">
      <div class="alert alert-warning">مریض مشابه پیدا شد. اگر همان شخص است، همان پرونده را انتخاب کنید تا برای یک نفر دو پرونده ساخته نشود.</div>
      <ul class="list-unstyled quick-matches">
        <li v-for="match in matches" :key="match.id">
          <span><span class="ltr-num">{{ match.fileNumber }}</span> — {{ patientName(match) }}، پدر: {{ match.fatherName ?? '—' }}</span>
          <button type="button" class="btn btn-primary btn-sm" @click="$emit('chosen', match.id)">همین مریض است</button>
        </li>
      </ul>
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="xr-form-actions">
        <button type="button" class="btn btn-soft" :disabled="saving" @click="save(true)">شخص دیگری است، ثبت شود</button>
        <button type="button" class="btn btn-quiet" @click="matches = []">بازگشت به فورم</button>
      </div>
    </div>

    <form v-else @submit.prevent="save()">
      <div class="row g-3 mb-3">
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-first">نام</label>
          <input id="q-first" v-model="form.firstName" class="form-control" maxlength="100" required />
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-last">تخلص</label>
          <input id="q-last" v-model="form.lastName" class="form-control" maxlength="100" />
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-father">نام پدر</label>
          <input id="q-father" v-model="form.fatherName" class="form-control" maxlength="100" />
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-gender">جنسیت</label>
          <select id="q-gender" v-model="form.gender" class="form-select">
            <option v-for="(label, value) in genderLabels" :key="value" :value="value">{{ label }}</option>
          </select>
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-age">سن تخمینی</label>
          <input id="q-age" v-model.number="form.estimatedAge" class="form-control" type="number" min="0" max="130" :disabled="!!form.birthDate" />
        </div>
        <div class="col-md-4 col-sm-6">
          <ShamsiDateInput v-model="form.birthDate" label="یا تاریخ تولد" :max="today" />
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-phone">شماره تماس</label>
          <input id="q-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" inputmode="tel" />
        </div>
        <div class="col-md-4 col-sm-6">
          <label class="form-label" for="q-occupation">شغل</label>
          <input id="q-occupation" v-model="form.occupation" class="form-control" maxlength="100" />
        </div>
        <div class="col-md-4">
          <label class="form-label" for="q-address">آدرس</label>
          <input id="q-address" v-model="form.address" class="form-control" maxlength="300" />
        </div>
      </div>
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving">ثبت و ادامه پذیرش</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.quick-matches li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 0;
  border-bottom: 1px solid var(--xr-row-border);
}
</style>
