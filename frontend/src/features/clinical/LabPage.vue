<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { patientName, type PatientListItem } from '@/features/patients/types'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { diagnosticStatusLabels, diagnosticStatusTones, type DiagnosticOrder } from './types'

const auth = useAuthStore()
const rows = ref<DiagnosticOrder[]>([])
const status = ref('')
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    const params = new URLSearchParams({ pageSize: '100' })
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

const result = reactive({ order: null as DiagnosticOrder | null, text: '', saving: false, error: '' })

function openResult(row: DiagnosticOrder) {
  Object.assign(result, { order: row, text: '', saving: false, error: '' })
}

async function saveResult() {
  result.saving = true
  result.error = ''
  try {
    await api(`/diagnostic-orders/${result.order!.id}/result`, { method: 'POST', body: { resultText: result.text, version: result.order!.version } })
    result.order = null
    await load()
  } catch (cause) {
    result.error = errorMessage(cause)
  } finally {
    result.saving = false
  }
}

const tests = ref<{ id: number; name: string; isActive: boolean }[]>([])
const fresh = reactive({ open: false, search: '', results: [] as PatientListItem[], patient: null as PatientListItem | null, testId: null as number | null, note: '', saving: false, error: '', key: '' })

async function openNew() {
  Object.assign(fresh, { open: true, search: '', results: [], patient: null, testId: null, note: '', saving: false, error: '', key: newIdempotencyKey() })
  try {
    tests.value = (await api<typeof tests.value>('/diagnostic-tests')).filter((t) => t.isActive)
  } catch (cause) {
    fresh.error = errorMessage(cause)
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(
  () => fresh.search,
  (value) => {
    clearTimeout(debounce)
    if (!value.trim()) {
      fresh.results = []
      return
    }
    debounce = setTimeout(async () => {
      try {
        fresh.results = (await api<{ items: PatientListItem[] }>(`/patients?pageSize=8&q=${encodeURIComponent(value.trim())}`)).items
      } catch (cause) {
        fresh.error = errorMessage(cause)
      }
    }, 300)
  },
)

async function saveNew() {
  if (!fresh.patient) {
    fresh.error = 'مریض را انتخاب کنید.'
    return
  }
  fresh.saving = true
  fresh.error = ''
  try {
    await api('/diagnostic-orders', { method: 'POST', idempotencyKey: fresh.key, body: { patientId: fresh.patient.id, testId: fresh.testId, note: fresh.note } })
    fresh.open = false
    await load()
  } catch (cause) {
    fresh.error = errorMessage(cause)
    if (cause instanceof ApiError && cause.status !== 0) fresh.key = newIdempotencyKey()
  } finally {
    fresh.saving = false
  }
}

useLiveRefresh(['DiagnosticOrderCreated', 'DiagnosticResultReady'], load)
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
    <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
    <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">درخواستی نیست.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>شماره</th><th>مریض</th><th>شماره پرونده</th><th>آزمایش</th><th>توضیح</th><th>زمان درخواست</th><th>وضعیت</th><th>عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><span class="ltr-num">{{ row.orderNumber }}</span></td>
            <td>{{ row.patientName }}</td>
            <td><span class="ltr-num">{{ row.patientFileNumber }}</span></td>
            <td>{{ row.testName }}</td>
            <td>{{ row.note ?? '—' }}</td>
            <td>{{ formatDateTime(row.createdAt) }}</td>
            <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[row.status]}`">{{ diagnosticStatusLabels[row.status] }}</span></td>
            <td class="text-nowrap">
              <button v-if="row.status === 'Requested' && auth.can('diagnostics.edit')" type="button" class="btn btn-soft btn-sm" @click="start(row)">شروع</button>
              <button v-if="['Requested', 'InProgress'].includes(row.status) && auth.can('diagnostics.approve')" type="button" class="btn btn-soft btn-sm ms-2" @click="openResult(row)">ثبت نتیجه</button>
              <RouterLink v-if="row.status === 'ResultReady' && auth.can('diagnostics.print')" :to="`/print/lab/${row.id}`" target="_blank" class="btn btn-soft btn-sm">چاپ جواب</RouterLink>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>

  <UiModal v-if="result.order" :title="`نتیجه ${result.order.testName}: ${result.order.patientName}`" @close="result.order = null">
    <form @submit.prevent="saveResult">
      <label class="form-label" for="r-text">نتیجه</label>
      <textarea id="r-text" v-model="result.text" class="form-control mb-3" rows="4" maxlength="4000" required></textarea>
      <div v-if="result.error" class="alert alert-danger py-2" role="alert">{{ result.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="result.saving">ثبت نتیجه</button>
    </form>
    <hr />
    <AttachmentPanel owner-type="DiagnosticOrder" :owner-id="result.order.id" :can-upload="auth.can('diagnostics.edit')" />
  </UiModal>

  <UiModal v-if="fresh.open" title="درخواست مستقل آزمایش" @close="fresh.open = false">
    <form @submit.prevent="saveNew">
      <div class="mb-3">
        <template v-if="fresh.patient">
          <div class="form-label">مریض</div>
          <div class="d-flex align-items-center justify-content-between">
            <strong>{{ fresh.patient.fileNumber }} — {{ patientName(fresh.patient) }}</strong>
            <button type="button" class="btn btn-soft btn-sm" @click="fresh.patient = null">تغییر</button>
          </div>
        </template>
        <template v-else>
          <label class="form-label" for="l-search">جستجوی مریض</label>
          <input id="l-search" v-model="fresh.search" type="search" class="form-control" autocomplete="off" />
          <div v-if="fresh.results.length" class="list-group mt-1">
            <button v-for="r in fresh.results" :key="r.id" type="button" class="list-group-item list-group-item-action" @click="fresh.patient = r; fresh.results = []; fresh.search = ''">
              <span class="ltr-num">{{ r.fileNumber }}</span> — {{ patientName(r) }}
            </button>
          </div>
        </template>
      </div>
      <div class="mb-3">
        <label class="form-label" for="l-test">آزمایش</label>
        <select id="l-test" v-model.number="fresh.testId" class="form-select" required>
          <option :value="null" disabled>انتخاب کنید</option>
          <option v-for="t in tests" :key="t.id" :value="t.id">{{ t.name }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="l-note">توضیح</label>
        <input id="l-note" v-model="fresh.note" class="form-control" maxlength="500" />
      </div>
      <div v-if="fresh.error" class="alert alert-danger py-2" role="alert">{{ fresh.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="fresh.saving">ثبت درخواست</button>
    </form>
  </UiModal>
</template>
