<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useUnsavedGuard } from '@/features/clinical/useUnsavedGuard'
import { todayIso } from '@/utils/date'
import { genderLabels, patientName, type Patient, type PatientListItem } from './types'

const props = defineProps<{ id?: string }>()
const router = useRouter()
const route = useRoute()

const form = reactive({
  firstName: '',
  lastName: '',
  fatherName: '',
  gender: 'Male',
  birthDate: null as string | null,
  estimatedAge: null as number | null,
  phone: '',
  address: '',
})

const { dirty, markSaved, applying } = useUnsavedGuard(form)

const patient = ref<Patient | null>(null)
const loading = ref(!!props.id)
const saving = ref(false)
const error = ref('')
const matches = ref<PatientListItem[]>([])
const key = newIdempotencyKey()
// Confirming changes the request, so it needs its own key, but a retry of the confirmation must still register one patient.
const confirmKey = newIdempotencyKey()

const today = todayIso()
const backTo = computed(() => (props.id ? `/patients/${props.id}` : '/patients'))

async function load() {
  if (!props.id) return
  try {
    const existing = await api<Patient>(`/patients/${props.id}`)
    patient.value = existing
    applying(() =>
      Object.assign(form, {
        firstName: existing.firstName,
        lastName: existing.lastName ?? '',
        fatherName: existing.fatherName ?? '',
        gender: existing.gender,
        birthDate: existing.birthDate,
        estimatedAge: existing.birthDate ? null : existing.ageYears,
        phone: existing.phone ?? '',
        address: existing.address ?? '',
      }),
    )
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function save(confirmNotDuplicate = false) {
  saving.value = true
  error.value = ''
  const body = {
    ...form,
    estimatedAge: form.birthDate ? null : form.estimatedAge || null,
    confirmNotDuplicate,
    version: patient.value?.version,
  }
  try {
    const saved = patient.value
      ? await api<Patient>(`/patients/${patient.value.id}`, { method: 'PUT', body })
      : await api<Patient>('/patients', { method: 'POST', body, idempotencyKey: confirmNotDuplicate ? confirmKey : key })
    markSaved()
    if (route.query.token && !patient.value) {
      await useToken(saved.id, String(route.query.token))
      return
    }
    await router.push(`/patients/${saved.id}`)
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

/** The fee is already paid, so a refusal leaves the token on the reception list and the patient already registered. The desk must be told. */
async function useToken(patientId: number, tokenId: string) {
  try {
    const token = await api<{ id: number; departmentId: number; doctorStaffId: number | null }>(`/visit-tokens/${tokenId}`)
    await api('/visits', { method: 'POST', idempotencyKey: newIdempotencyKey(), body: { patientId, departmentId: token.departmentId, doctorStaffId: token.doctorStaffId, tokenId: token.id } })
  } catch (cause) {
    error.value = `مریض ثبت شد، ولی مراجعه ساخته نشد: ${errorMessage(cause)} مراجعه را از همان نوبت در صفحه پذیرش تکمیل کنید.`
    return
  }
  await router.push({ name: 'reception' })
}

onMounted(load)
</script>

<template>
  <UiFormPage
    :title="id ? 'ویرایش مریض' : 'ثبت مریض جدید'"
    :back-to="backTo"
    :back-label="id ? 'بازگشت به پرونده' : 'بازگشت به مریضان'"
    :dirty="dirty"
    :saving="saving"
    :error="error"
    :done="matches.length > 0"
    @submit="save()"
  >
    <div v-if="matches.length" role="alert">
      <div class="alert alert-warning">مریض مشابه پیدا شد. اگر همان شخص است، از پرونده موجود استفاده کنید تا برای یک نفر دو پرونده ساخته نشود.</div>
      <ul class="list-unstyled">
        <li v-for="match in matches" :key="match.id" class="mb-2">
          <RouterLink :to="`/patients/${match.id}`">
            <span class="ltr-num">{{ match.fileNumber }}</span> — {{ patientName(match) }}، پدر: {{ match.fatherName ?? '—' }}
          </RouterLink>
        </li>
      </ul>
      <div class="xr-form-actions">
        <button type="button" class="btn btn-soft" :disabled="saving" @click="save(true)">شخص دیگری است، ثبت شود</button>
        <button type="button" class="btn btn-soft" @click="matches = []">بازگشت به فورم</button>
      </div>
    </div>

    <fieldset v-else :disabled="loading">
      <div class="xr-group">
        <h2 class="xr-group__title">هویت</h2>
        <div class="row g-3">
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-first">نام</label>
            <input id="p-first" v-model="form.firstName" class="form-control" maxlength="100" required autofocus />
          </div>
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-last">تخلص</label>
            <input id="p-last" v-model="form.lastName" class="form-control" maxlength="100" />
          </div>
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-father">نام پدر</label>
            <input id="p-father" v-model="form.fatherName" class="form-control" maxlength="100" />
          </div>
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-gender">جنسیت</label>
            <select id="p-gender" v-model="form.gender" class="form-select">
              <option v-for="(label, value) in genderLabels" :key="value" :value="value">{{ label }}</option>
            </select>
          </div>
        </div>
      </div>

      <div class="xr-group">
        <h2 class="xr-group__title">سن</h2>
        <div class="row g-3">
          <div class="col-md-3 col-sm-6">
            <ShamsiDateInput v-model="form.birthDate" label="تاریخ تولد (اختیاری)" :max="today" />
          </div>
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-age">یا سن تخمینی</label>
            <input id="p-age" v-model.number="form.estimatedAge" class="form-control" type="number" min="0" max="130" :disabled="!!form.birthDate" />
          </div>
        </div>
      </div>

      <div class="xr-group mb-0">
        <h2 class="xr-group__title">تماس</h2>
        <div class="row g-3">
          <div class="col-md-3 col-sm-6">
            <label class="form-label" for="p-phone">شماره تماس</label>
            <input id="p-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" inputmode="tel" />
          </div>
          <div class="col-md-9">
            <label class="form-label" for="p-address">آدرس</label>
            <input id="p-address" v-model="form.address" class="form-control" maxlength="300" />
          </div>
        </div>
      </div>
    </fieldset>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading">ذخیره</button>
      <RouterLink :to="backTo" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>
