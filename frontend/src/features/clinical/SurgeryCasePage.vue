<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, kabulToUtc } from '@/utils/date'
import { eyeLabels, surgeryStatusLabels, type Chart, type SurgeryDetail } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ id: string }>()
const router = useRouter()
const auth = useAuthStore()

const detail = ref<SurgeryDetail | null>(null)
const chart = ref<Chart | null>(null)
const form = reactive({ date: null as string | null, time: '08:00', preOpChecks: '', consentRecorded: false, operationReport: '', postOpStatus: '' })
const { dirty, markSaved, applying } = useUnsavedGuard(form)
const loading = ref(true)
const saving = ref(false)
const error = ref('')

const diagnoses = computed(() => chart.value?.consultation?.diagnoses.map((d) => (d.eye === 'None' ? d.text : `${d.text} — ${eyeLabels[d.eye]}`)).join('؛ ') ?? '')

async function load() {
  try {
    const loaded = await api<SurgeryDetail>(`/surgeries/${props.id}`)
    detail.value = loaded
    applying(() =>
      Object.assign(form, {
        preOpChecks: loaded.preOpChecks ?? '',
        consentRecorded: loaded.consentRecorded,
        operationReport: loaded.operationReport ?? '',
        postOpStatus: loaded.postOpStatus ?? '',
      }),
    )
    // The chart is context only: it may be closed to this user, and the case stays usable without it.
    if (auth.can('consultation.view')) chart.value = await api<Chart>(`/consultations/${loaded.case.visitId}/chart`).catch(() => null)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function submit() {
  saving.value = true
  error.value = ''
  const row = detail.value!.case
  try {
    await api(`/surgeries/${row.id}`, {
      method: 'PUT',
      body: {
        scheduledAt: form.date ? kabulToUtc(form.date, form.time) : row.scheduledAt,
        preOpChecks: form.preOpChecks,
        consentRecorded: form.consentRecorded,
        operationReport: form.operationReport,
        postOpStatus: form.postOpStatus,
        version: row.version,
      },
    })
    markSaved()
    await router.push({ name: 'surgery' })
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <UiFormPage :title="detail ? `عملیات ${detail.case.caseNumber}` : 'پرونده عملیات'" :back-to="{ name: 'surgery' }" back-label="بازگشت به عملیات" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <template #summary>
      <section class="xr-card xr-card__body">
        <dl class="xr-facts">
          <div><dt>مریض</dt><dd>{{ detail?.case.patientName ?? '...' }}</dd></div>
          <div><dt>شماره پرونده</dt><dd><span class="ltr-num">{{ detail?.case.patientFileNumber ?? '...' }}</span></dd></div>
          <div><dt>نوع عملیات</dt><dd>{{ detail?.case.surgeryType ?? '...' }}</dd></div>
          <div><dt>چشم</dt><dd>{{ detail ? eyeLabels[detail.case.eye] : '...' }}</dd></div>
          <div><dt>وضعیت</dt><dd>{{ detail ? surgeryStatusLabels[detail.case.status] : '...' }}</dd></div>
          <div><dt>تاریخ فعلی</dt><dd>{{ formatDateTime(detail?.case.scheduledAt) }}</dd></div>
          <div v-if="diagnoses"><dt>تشخیص</dt><dd>{{ diagnoses }}</dd></div>
          <div v-if="chart?.preExam?.history"><dt>سوابق و هشدارها</dt><dd class="text-danger">{{ chart.preExam.history }}</dd></div>
        </dl>
        <div v-if="detail && auth.can('surgery.print')" class="mt-3">
          <RouterLink :to="`/print/ot-card/${detail.case.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ کارت عملیات‌خانه</RouterLink>
        </div>
      </section>
    </template>

    <fieldset :disabled="loading || !detail">
      <div class="row g-3 mb-3">
        <div class="col-md-4 col-sm-8"><ShamsiDateInput v-model="form.date" label="تاریخ عملیات" /></div>
        <div class="col-md-2 col-sm-4">
          <label class="form-label" for="sg-time">ساعت</label>
          <input id="sg-time" v-model="form.time" class="form-control" type="time" dir="ltr" />
        </div>
      </div>
      <div class="mb-3">
        <label class="form-label" for="sg-preop">بررسی‌های پیش از عملیات</label>
        <textarea id="sg-preop" v-model="form.preOpChecks" class="form-control" rows="3" maxlength="2000"></textarea>
      </div>
      <div class="mb-3">
        <label class="form-check-label"><input v-model="form.consentRecorded" class="form-check-input ms-2" type="checkbox" />رضایت‌نامه ثبت شده است</label>
      </div>
      <div class="mb-3">
        <label class="form-label" for="sg-report">گزارش عملیات</label>
        <textarea id="sg-report" v-model="form.operationReport" class="form-control" rows="5" maxlength="4000"></textarea>
      </div>
      <div>
        <label class="form-label" for="sg-post">وضعیت پس از عملیات</label>
        <textarea id="sg-post" v-model="form.postOpStatus" class="form-control" rows="3" maxlength="2000"></textarea>
      </div>
    </fieldset>
    <template v-if="detail">
      <hr />
      <AttachmentPanel owner-type="SurgeryCase" :owner-id="detail.case.id" :can-upload="auth.can('surgery.edit')" />
    </template>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading || !detail">ذخیره</button>
      <RouterLink :to="{ name: 'surgery' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>
