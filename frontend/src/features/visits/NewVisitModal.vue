<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage, newIdempotencyKey } from '@/api/http'
import UiModal from '@/components/UiModal.vue'
import { patientName, type PatientListItem } from '@/features/patients/types'
import { money } from '@/utils/money'
import type { LookupItem, VisitRow } from './types'

const props = defineProps<{ patient?: { id: number; label: string } }>()
const emit = defineEmits<{ close: []; saved: [visit: VisitRow] }>()

const departments = ref<LookupItem[]>([])
const doctors = ref<LookupItem[]>([])
const services = ref<{ id: number; name: string; isActive: boolean; currentPrice: number | null }[]>([])
const form = reactive({ departmentId: null as number | null, doctorStaffId: null as number | null, serviceId: null as number | null, reason: '' })
const selected = ref(props.patient ?? null)
const search = ref('')
const results = ref<PatientListItem[]>([])
const saving = ref(false)
const error = ref('')
const key = newIdempotencyKey()

let debounce: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(debounce)
  if (!value.trim()) {
    results.value = []
    return
  }
  debounce = setTimeout(async () => {
    try {
      const page = await api<{ items: PatientListItem[] }>(`/patients?pageSize=8&q=${encodeURIComponent(value.trim())}`)
      results.value = page.items
    } catch (cause) {
      error.value = errorMessage(cause)
    }
  }, 300)
})

function pick(patient: PatientListItem) {
  selected.value = { id: patient.id, label: `${patient.fileNumber} — ${patientName(patient)}` }
  results.value = []
  search.value = ''
}

async function save() {
  if (!selected.value) {
    error.value = 'مریض را انتخاب کنید.'
    return
  }
  saving.value = true
  error.value = ''
  try {
    const visit = await api<VisitRow>('/visits', {
      method: 'POST',
      idempotencyKey: key,
      body: { patientId: selected.value.id, departmentId: form.departmentId, doctorStaffId: form.doctorStaffId, serviceId: form.serviceId, reason: form.reason },
    })
    emit('saved', visit)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const [loadedDepartments, loadedDoctors, loadedServices] = await Promise.all([
      api<LookupItem[]>('/lookups/departments'),
      api<LookupItem[]>('/lookups/doctors'),
      api<typeof services.value>('/services'),
    ])
    departments.value = loadedDepartments
    doctors.value = loadedDoctors
    services.value = loadedServices.filter((s) => s.isActive && s.currentPrice !== null)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
})
</script>

<template>
  <UiModal title="مراجعه جدید" @close="$emit('close')">
    <form @submit.prevent="save">
      <div class="mb-3">
        <template v-if="selected">
          <div class="form-label">مریض</div>
          <div class="d-flex align-items-center justify-content-between">
            <strong>{{ selected.label }}</strong>
            <button v-if="!patient" type="button" class="btn btn-soft btn-sm" @click="selected = null">تغییر</button>
          </div>
        </template>
        <template v-else>
          <label class="form-label" for="v-search">جستجوی مریض</label>
          <input id="v-search" v-model="search" type="search" class="form-control" placeholder="نام، شماره پرونده یا تماس" autocomplete="off" />
          <div v-if="results.length" class="list-group mt-1">
            <button v-for="row in results" :key="row.id" type="button" class="list-group-item list-group-item-action" @click="pick(row)">
              <span class="ltr-num">{{ row.fileNumber }}</span> — {{ patientName(row) }}
            </button>
          </div>
        </template>
      </div>
      <div class="mb-3">
        <label class="form-label" for="v-dept">بخش</label>
        <select id="v-dept" v-model.number="form.departmentId" class="form-select" required>
          <option :value="null" disabled>انتخاب کنید</option>
          <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="v-doctor">داکتر (اختیاری)</label>
        <select id="v-doctor" v-model.number="form.doctorStaffId" class="form-select">
          <option :value="null">بدون انتخاب</option>
          <option v-for="d in doctors" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="v-service">خدمت قابل پرداخت (اختیاری)</label>
        <select id="v-service" v-model.number="form.serviceId" class="form-select">
          <option :value="null">بدون هزینه</option>
          <option v-for="s in services" :key="s.id" :value="s.id">{{ s.name }} — {{ money(s.currentPrice) }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="v-reason">علت مراجعه</label>
        <input id="v-reason" v-model="form.reason" class="form-control" maxlength="300" />
      </div>
      <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="saving">ثبت مراجعه</button>
    </form>
  </UiModal>
</template>
