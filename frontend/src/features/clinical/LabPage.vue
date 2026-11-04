<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { testOptions } from '@/api/lookups'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import CallControls from '@/features/calls/CallControls.vue'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientCell from '@/features/patients/PatientCell.vue'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { diagnosticStatusLabels, diagnosticStatusTones, type DiagnosticOrder } from './types'

const auth = useAuthStore()
const imagingCalls = useCallDesk('Imaging')
const labCalls = useCallDesk('Lab')
const rows = ref<DiagnosticOrder[]>([])
const status = ref('')
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    // Refraction has its own queue and form.
    const params = new URLSearchParams({ pageSize: '100', excludeRefraction: 'true' })
    if (status.value) params.set('status', status.value)
    rows.value = (await api<{ items: DiagnosticOrder[] }>(`/diagnostic-orders?${params}`)).items
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function start(row: DiagnosticOrder) {
  error.value = ''
  try {
    await api(`/diagnostic-orders/${row.id}/start`, { method: 'POST', body: { version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

const tests = ref<ComboOption[]>([])
const fresh = reactive({ open: false, patientId: null as number | null, testId: null as number | null, note: '', saving: false, error: '', key: '' })

async function openNew() {
  Object.assign(fresh, { open: true, patientId: null, testId: null, note: '', saving: false, error: '', key: newIdempotencyKey() })
  try {
    tests.value = await testOptions()
  } catch (cause) {
    fresh.error = errorMessage(cause)
  }
}

async function saveNew() {
  if (!fresh.patientId) {
    fresh.error = 'مریض را انتخاب کنید.'
    return
  }
  fresh.saving = true
  fresh.error = ''
  try {
    await api('/diagnostic-orders', { method: 'POST', idempotencyKey: fresh.key, body: { patientId: fresh.patientId, testId: fresh.testId, note: fresh.note } })
    fresh.open = false
    await load()
  } catch (cause) {
    fresh.error = errorMessage(cause)
    if (cause instanceof ApiError && cause.status !== 0) fresh.key = newIdempotencyKey()
  } finally {
    fresh.saving = false
  }
}

useLiveRefresh(['DiagnosticOrderCreated', 'DiagnosticResultReady', 'PaymentReceived'], load)
onMounted(load)
watch(status, load)
</script>

<template>
  <UiCard title="درخواست‌های آزمایش">
    <template #actions>
      <div class="d-flex gap-2">
        <select v-model="status" class="form-select" aria-label="فیلتر وضعیت">
          <option value="">همه وضعیت‌ها</option>
          <option v-for="(label, value) in diagnosticStatusLabels" :key="value" :value="value">{{ label }}</option>
        </select>
        <button v-if="auth.can('diagnostics.create')" type="button" class="btn btn-primary text-nowrap" @click="openNew">درخواست مستقل</button>
      </div>
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <CallNotice :desks="[imagingCalls, labCalls]" />
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">درخواستی نیست.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>شماره</th><th>مریض</th><th>آزمایش</th><th>زمان درخواست</th><th>وضعیت</th><th>فراخوان</th><th>عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><span class="ltr-num">{{ row.orderNumber }}</span></td>
            <td><PatientCell :name="row.patientName" :file-number="row.patientFileNumber" /></td>
            <td>
              <div>{{ row.testName }}</div>
              <div v-if="row.note" class="xr-meta">{{ row.note }}</div>
            </td>
            <td>{{ formatDateTime(row.createdAt) }}</td>
            <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[row.status]}`">{{ diagnosticStatusLabels[row.status] }}</span></td>
            <td><CallControls v-if="['Requested', 'InProgress'].includes(row.status)" :desk="row.kind === 'Imaging' ? imagingCalls : labCalls" :visit-id="row.visitId" /></td>
            <td class="text-nowrap">
              <span v-if="row.status === 'Requested' && row.paid === false" class="xr-badge xr-badge--orange">در انتظار پرداخت</span>
              <template v-else>
                <button v-if="row.status === 'Requested' && auth.can('diagnostics.edit')" type="button" class="btn btn-soft btn-sm" @click="start(row)">شروع</button>
                <RouterLink v-if="['Requested', 'InProgress'].includes(row.status) && auth.can('diagnostics.approve')" :to="`/lab/${row.id}/result`" class="btn btn-soft btn-sm ms-2">ثبت نتیجه</RouterLink>
              </template>
              <RouterLink v-if="row.status === 'ResultReady' && auth.can('diagnostics.print')" :to="`/print/lab/${row.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ جواب</RouterLink>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>

  <UiModal v-if="fresh.open" title="درخواست مستقل آزمایش" @close="fresh.open = false">
    <form @submit.prevent="saveNew">
      <div class="mb-3"><PatientPicker v-model="fresh.patientId" required /></div>
      <div class="mb-3"><UiComboBox v-model="fresh.testId" label="آزمایش" :options="tests" required chip /></div>
      <div class="mb-3">
        <label class="form-label" for="l-note">توضیح</label>
        <input id="l-note" v-model="fresh.note" class="form-control" maxlength="500" />
      </div>
      <div v-if="fresh.error" class="alert alert-danger" role="alert">{{ fresh.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="fresh.saving">ثبت درخواست</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
