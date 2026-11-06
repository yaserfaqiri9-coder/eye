<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, kabulToUtc } from '@/utils/date'
import SurgerySupplies from './SurgerySupplies.vue'
import { eyeLabels, surgeryStatusLabels, type Chart, type SurgeryDetail } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ id: string }>()
const router = useRouter()
const auth = useAuthStore()

const detail = ref<SurgeryDetail | null>(null)
const chart = ref<Chart | null>(null)
const form = reactive({ date: null as string | null, time: '08:00', surgeryType: '', eye: 'OD', preOpChecks: '', consentRecorded: false, operationReport: '', postOpStatus: '', amendReason: '' })
const { dirty, markSaved, applying } = useUnsavedGuard(form)
const loading = ref(true)
const saving = ref(false)
const error = ref('')

/** Which operation and which eye can change only until the case begins. */
const canChangeOperation = computed(() => ['Proposed', 'Scheduled'].includes(detail.value?.case.status ?? ''))
const isClosed = computed(() => detail.value?.case.status === 'Closed')
const isCancelled = computed(() => detail.value?.case.status === 'Cancelled')

const diagnoses = computed(() => chart.value?.consultation?.diagnoses.map((d) => (d.eye === 'None' ? d.text : `${d.text} — ${eyeLabels[d.eye]}`)).join('؛ ') ?? '')

async function load() {
  try {
    const loaded = await api<SurgeryDetail>(`/surgeries/${props.id}`)
    detail.value = loaded
    applying(() =>
      Object.assign(form, {
        surgeryType: loaded.case.surgeryType,
        eye: loaded.case.eye,
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
        surgeryType: canChangeOperation.value ? form.surgeryType : undefined,
        eye: canChangeOperation.value ? form.eye : undefined,
        amendReason: isClosed.value ? form.amendReason : undefined,
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

/** After supplies are recorded the case has a new version; the form keeps what the user typed. */
async function reload() {
  try {
    const loaded = await api<SurgeryDetail>(`/surgeries/${props.id}`)
    detail.value = loaded
  } catch (cause) {
    error.value = errorMessage(cause)
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
          <div v-if="detail?.packageName"><dt>پکیج</dt><dd>{{ detail.packageName }}<span v-if="!detail.suppliesRecordedAt && ['InProgress', 'Completed', 'FollowUp'].includes(detail.case.status)" class="xr-note xr-note--red ms-2">مواد مصرفی ثبت نشده</span></dd></div>
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

    <fieldset :disabled="loading || !detail || isCancelled">
      <div v-if="isCancelled" class="alert alert-primary" role="status">این پرونده لغو شده است و قابل ویرایش نیست.</div>
      <div v-if="isClosed" class="alert alert-warning" role="status">این پرونده بسته شده است. مقدار قبلی و دلیل اصلاح در پرونده می‌ماند.</div>
      <div v-if="canChangeOperation" class="row g-3 mb-3">
        <div class="col-md-6">
          <label class="form-label" for="sg-type">نوع عملیات</label>
          <input id="sg-type" v-model="form.surgeryType" class="form-control" maxlength="200" required />
        </div>
        <div class="col-md-3">
          <label class="form-label" for="sg-eye">چشم</label>
          <select id="sg-eye" v-model="form.eye" class="form-select">
            <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
          </select>
        </div>
      </div>
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
      <div v-if="isClosed" class="mt-3">
        <label class="form-label" for="sg-reason">دلیل اصلاح</label>
        <input id="sg-reason" v-model="form.amendReason" class="form-control" minlength="3" maxlength="500" required />
      </div>
    </fieldset>
    <template v-if="detail">
      <hr />
      <SurgerySupplies :key="detail.case.version" :detail="detail" @changed="reload" />
      <hr />
      <AttachmentPanel owner-type="SurgeryCase" :owner-id="detail.case.id" :can-upload="auth.can('surgery.edit')" />
    </template>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading || !detail || isCancelled">ذخیره</button>
      <RouterLink :to="{ name: 'surgery' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>
