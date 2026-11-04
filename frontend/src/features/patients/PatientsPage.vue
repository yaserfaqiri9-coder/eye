<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiPager from '@/components/UiPager.vue'
import { useAuthStore } from '@/stores/auth'
import { quantity } from '@/utils/money'
import PatientCell from './PatientCell.vue'
import { genderLabels, patientName, type PatientListItem } from './types'

const auth = useAuthStore()
const route = useRoute()

const q = ref(typeof route.query.q === 'string' ? route.query.q : '')
const rows = ref<PatientListItem[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 25
const loading = ref(true)
const error = ref('')

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

onMounted(load)
onBeforeUnmount(() => {
  clearTimeout(debounce)
  controller?.abort()
})
</script>

<template>
  <UiCard title="مریضان">
    <template #actions>
      <RouterLink v-if="auth.can('patients.create')" to="/patients/new" class="btn btn-primary">مریض جدید</RouterLink>
    </template>

    <div class="row mb-3 align-items-center gy-2">
      <div class="col-md-6">
        <input v-model="q" type="search" class="form-control" placeholder="جستجو با نام، شماره پرونده یا شماره تماس" aria-label="جستجوی مریض" />
      </div>
      <div v-if="total > 0" class="col-md-6 text-md-end xr-meta">{{ quantity(total) }} مریض</div>
    </div>

    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading && rows.length === 0" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">مریضی پیدا نشد.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table patients-table">
        <thead>
          <tr>
            <th>مریض</th>
            <th>نام پدر</th>
            <th>جنسیت و سن</th>
            <th>شماره تماس</th>
            <th aria-label="باز کردن پرونده"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td><PatientCell :name="patientName(row)" :file-number="row.fileNumber" :to="`/patients/${row.id}`" :gender="row.gender" avatar /></td>
            <td>{{ row.fatherName ?? '—' }}</td>
            <td class="text-nowrap">{{ genderLabels[row.gender] }}<template v-if="row.ageYears != null">، {{ quantity(row.ageYears) }} ساله</template></td>
            <td class="patients-table__phone"><span class="ltr-num">{{ row.phone ?? '—' }}</span></td>
            <td class="patients-table__open text-nowrap">مشاهده پرونده <i class="ri-arrow-left-s-line" aria-hidden="true"></i></td>
          </tr>
        </tbody>
      </table>
    </div>

    <UiPager :page="page" :page-count="pageCount" @go="goTo" />
  </UiCard>
</template>

<style scoped>
/* The whole row opens the record; the name stays the real link, so the keyboard reaches it too. */
.patients-table tbody tr {
  position: relative;
  cursor: pointer;
}

.patients-table :deep(a.xr-person__name)::after {
  content: '' / '';
  position: absolute;
  inset: 0;
}

/* Sits above the row's link so a number can still be selected and copied. */
.patients-table__phone span {
  position: relative;
  z-index: 1;
  cursor: text;
}

.patients-table__open {
  color: var(--xr-text-soft);
  font-size: 12.5px;
  text-align: end;
}

.patients-table tbody tr:hover .patients-table__open {
  color: var(--xr-tone-primary-text);
}
</style>
