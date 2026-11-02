<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, kabulToUtc } from '@/utils/date'
import { eyeLabels, surgeryStatusLabels, type SurgeryDetail, type SurgeryRow } from './types'

const auth = useAuthStore()
const rows = ref<SurgeryRow[]>([])
const status = ref('')
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    const params = new URLSearchParams({ pageSize: '100' })
    if (status.value) params.set('status', status.value)
    rows.value = (await api<{ items: SurgeryRow[] }>(`/surgeries?${params}`)).items
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function changeStatus(row: SurgeryRow, next: string) {
  error.value = ''
  try {
    await api(`/surgeries/${row.id}/status`, { method: 'POST', body: { status: next, version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

function canMove(next: string) {
  return next === 'Cancelled' ? auth.can('surgery.cancel') : auth.can('surgery.approve')
}

const form = reactive({ row: null as SurgeryRow | null, scheduledAt: null as string | null, date: null as string | null, time: '08:00', preOpChecks: '', consentRecorded: false, operationReport: '', postOpStatus: '', saving: false, error: '' })

async function openEdit(row: SurgeryRow) {
  Object.assign(form, { row, scheduledAt: null, date: null, time: '08:00', preOpChecks: '', consentRecorded: false, operationReport: '', postOpStatus: '', saving: false, error: '' })
  try {
    const detail = await api<SurgeryDetail>(`/surgeries/${row.id}`)
    Object.assign(form, {
      scheduledAt: detail.case.scheduledAt,
      preOpChecks: detail.preOpChecks ?? '',
      consentRecorded: detail.consentRecorded,
      operationReport: detail.operationReport ?? '',
      postOpStatus: detail.postOpStatus ?? '',
    })
  } catch (cause) {
    form.error = errorMessage(cause)
  }
}

async function save() {
  form.saving = true
  form.error = ''
  try {
    await api(`/surgeries/${form.row!.id}`, {
      method: 'PUT',
      body: {
        scheduledAt: form.date ? kabulToUtc(form.date, form.time) : form.scheduledAt,
        preOpChecks: form.preOpChecks,
        consentRecorded: form.consentRecorded,
        operationReport: form.operationReport,
        postOpStatus: form.postOpStatus,
        version: form.row!.version,
      },
    })
    form.row = null
    await load()
  } catch (cause) {
    form.error = errorMessage(cause)
  } finally {
    form.saving = false
  }
}

useLiveRefresh(['SurgeryScheduled'], load)
onMounted(load)
watch(status, load)
</script>

<template>
  <UiCard title="عملیات">
    <template #actions>
      <select v-model="status" class="form-select" aria-label="فیلتر وضعیت">
        <option value="">همه وضعیت‌ها</option>
        <option v-for="(label, value) in surgeryStatusLabels" :key="value" :value="value">{{ label }}</option>
      </select>
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">پرونده عملیاتی نیست.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>شماره</th><th>مریض</th><th>نوع عملیات</th><th>چشم</th><th>تاریخ</th><th>وضعیت</th><th v-if="auth.can('surgery.edit')">عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><span class="ltr-num">{{ row.caseNumber }}</span></td>
            <td>{{ row.patientName }}</td>
            <td>{{ row.surgeryType }}</td>
            <td>{{ eyeLabels[row.eye] }}</td>
            <td>{{ formatDateTime(row.scheduledAt) }}</td>
            <td>{{ surgeryStatusLabels[row.status] }}</td>
            <td v-if="auth.can('surgery.edit')" class="d-flex gap-2">
              <button type="button" class="btn btn-soft btn-sm text-nowrap" @click="openEdit(row)">جزئیات</button>
              <select
                v-if="row.nextStatuses.some(canMove)"
                class="form-select form-select-sm"
                :aria-label="`تغییر وضعیت ${row.caseNumber}`"
                @change="changeStatus(row, ($event.target as HTMLSelectElement).value)"
              >
                <option value="" selected disabled>تغییر وضعیت</option>
                <option v-for="next in row.nextStatuses.filter(canMove)" :key="next" :value="next">{{ surgeryStatusLabels[next] }}</option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>

  <UiModal v-if="form.row" :title="`عملیات ${form.row.caseNumber}: ${form.row.patientName}`" @close="form.row = null">
    <form @submit.prevent="save">
      <p v-if="form.scheduledAt" class="small">تاریخ فعلی: {{ formatDateTime(form.scheduledAt) }}</p>
      <div class="row g-3 mb-3">
        <div class="col-8"><ShamsiDateInput v-model="form.date" label="تاریخ عملیات" /></div>
        <div class="col-4">
          <label class="form-label" for="sg-time">ساعت</label>
          <input id="sg-time" v-model="form.time" class="form-control" type="time" dir="ltr" />
        </div>
      </div>
      <div class="mb-3">
        <label class="form-label" for="sg-preop">بررسی‌های پیش از عملیات</label>
        <textarea id="sg-preop" v-model="form.preOpChecks" class="form-control" rows="2" maxlength="2000"></textarea>
      </div>
      <div class="mb-3">
        <label class="form-check-label"><input v-model="form.consentRecorded" class="form-check-input ms-2" type="checkbox" />رضایت‌نامه ثبت شده است</label>
      </div>
      <div class="mb-3">
        <label class="form-label" for="sg-report">گزارش عملیات</label>
        <textarea id="sg-report" v-model="form.operationReport" class="form-control" rows="3" maxlength="4000"></textarea>
      </div>
      <div class="mb-3">
        <label class="form-label" for="sg-post">وضعیت پس از عملیات</label>
        <textarea id="sg-post" v-model="form.postOpStatus" class="form-control" rows="2" maxlength="2000"></textarea>
      </div>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="form.saving">ذخیره</button>
    </form>
    <hr />
    <AttachmentPanel owner-type="SurgeryCase" :owner-id="form.row.id" :can-upload="auth.can('surgery.edit')" />
  </UiModal>
</template>
