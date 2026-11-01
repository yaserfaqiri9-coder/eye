<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import PatientFormModal from './PatientFormModal.vue'
import { genderLabels, patientName, type Patient, type PatientListItem } from './types'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const q = ref(typeof route.query.q === 'string' ? route.query.q : '')
const rows = ref<PatientListItem[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 25
const loading = ref(true)
const error = ref('')
const creating = ref(false)

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))

let controller: AbortController | null = null
let debounce: ReturnType<typeof setTimeout> | undefined

async function load() {
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    if (q.value.trim()) params.set('q', q.value.trim())
    const result = await api<{ items: PatientListItem[]; total: number }>(`/patients?${params}`, { signal })
    rows.value = result.items
    total.value = result.total
  } catch (cause) {
    if (signal.aborted) return
    error.value = errorMessage(cause)
  }
  loading.value = false
}

watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(() => {
    page.value = 1
    load()
  }, 300)
})

watch(
  () => route.query.q,
  (value) => {
    if (typeof value === 'string' && value !== q.value) q.value = value
  },
)

function goTo(next: number) {
  page.value = next
  load()
}

function onSaved(patient: Patient) {
  creating.value = false
  router.push(`/patients/${patient.id}`)
}

onMounted(load)
onBeforeUnmount(() => {
  clearTimeout(debounce)
  controller?.abort()
})
</script>

<template>
  <UiCard title="مریضان">
    <template #actions>
      <button v-if="auth.can('patients.create')" type="button" class="btn btn-primary" @click="creating = true">مریض جدید</button>
    </template>

    <div class="row mb-3">
      <div class="col-md-6">
        <input v-model="q" type="search" class="form-control" placeholder="جستجو با نام، شماره پرونده یا شماره تماس" aria-label="جستجوی مریض" />
      </div>
    </div>

    <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
    <div v-if="loading && rows.length === 0" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">مریضی پیدا نشد.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr>
            <th>شماره پرونده</th>
            <th>نام</th>
            <th>نام پدر</th>
            <th>جنسیت</th>
            <th>سن</th>
            <th>تماس</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><RouterLink :to="`/patients/${row.id}`"><span class="ltr-num">{{ row.fileNumber }}</span></RouterLink></td>
            <td><RouterLink :to="`/patients/${row.id}`">{{ patientName(row) }}</RouterLink></td>
            <td>{{ row.fatherName ?? '—' }}</td>
            <td>{{ genderLabels[row.gender] }}</td>
            <td>{{ row.ageYears ?? '—' }}</td>
            <td><span class="ltr-num">{{ row.phone ?? '—' }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="pageCount > 1" class="d-flex align-items-center justify-content-center gap-2 mt-3">
      <button type="button" class="btn btn-soft btn-sm" :disabled="page <= 1" @click="goTo(page - 1)">قبلی</button>
      <span>{{ page }} از {{ pageCount }}</span>
      <button type="button" class="btn btn-soft btn-sm" :disabled="page >= pageCount" @click="goTo(page + 1)">بعدی</button>
    </div>
  </UiCard>

  <PatientFormModal v-if="creating" @close="creating = false" @saved="onSaved" />
</template>
