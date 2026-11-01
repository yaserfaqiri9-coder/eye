<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import NewVisitModal from '@/features/visits/NewVisitModal.vue'
import { visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import PatientFormModal from './PatientFormModal.vue'
import { genderLabels, patientName, type Patient } from './types'

interface ChangeRow {
  field: string
  oldValue: string | null
  newValue: string | null
  changedAt: string
  changedBy: string | null
}

const fieldLabels: Record<string, string> = {
  FirstName: 'نام',
  LastName: 'تخلص',
  FatherName: 'نام پدر',
  Gender: 'جنسیت',
  BirthDate: 'تاریخ تولد',
  EstimatedBirthYear: 'سال تولد تخمینی',
  Phone: 'تماس',
  Address: 'آدرس',
}

const props = defineProps<{ id: string }>()
const auth = useAuthStore()

const patient = ref<Patient | null>(null)
const visits = ref<VisitRow[]>([])
const changes = ref<ChangeRow[]>([])
const error = ref('')
const editing = ref(false)
const addingVisit = ref(false)

async function load() {
  error.value = ''
  try {
    patient.value = await api<Patient>(`/patients/${props.id}`)
    if (auth.can('visits.view')) {
      visits.value = (await api<{ items: VisitRow[] }>(`/visits?patientId=${props.id}`)).items
    }
    if (auth.can('patients.edit')) {
      changes.value = await api<ChangeRow[]>(`/patients/${props.id}/changes`)
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

function onSaved() {
  editing.value = false
  load()
}

function onVisitSaved() {
  addingVisit.value = false
  load()
}

watch(() => props.id, load)
onMounted(load)
</script>

<template>
  <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
  <div v-if="patient" class="d-grid gap-4">
    <UiCard :title="patientName(patient)">
      <template #actions>
        <div class="d-flex gap-2">
          <button v-if="auth.can('visits.create')" type="button" class="btn btn-primary" @click="addingVisit = true">مراجعه جدید</button>
          <button v-if="auth.can('patients.edit')" type="button" class="btn btn-soft" @click="editing = true">ویرایش</button>
          <RouterLink v-if="auth.can('patients.print')" :to="`/print/patient/${patient.id}`" target="_blank" class="btn btn-soft">چاپ کارت</RouterLink>
        </div>
      </template>
      <dl class="row mb-0">
        <dt class="col-sm-2">شماره پرونده</dt>
        <dd class="col-sm-4"><span class="ltr-num">{{ patient.fileNumber }}</span></dd>
        <dt class="col-sm-2">نام پدر</dt>
        <dd class="col-sm-4">{{ patient.fatherName ?? '—' }}</dd>
        <dt class="col-sm-2">جنسیت</dt>
        <dd class="col-sm-4">{{ genderLabels[patient.gender] }}</dd>
        <dt class="col-sm-2">سن</dt>
        <dd class="col-sm-4">{{ patient.ageYears ?? '—' }}<template v-if="patient.birthDate"> (تولد: {{ formatDate(patient.birthDate) }})</template></dd>
        <dt class="col-sm-2">تماس</dt>
        <dd class="col-sm-4"><span class="ltr-num">{{ patient.phone ?? '—' }}</span></dd>
        <dt class="col-sm-2">آدرس</dt>
        <dd class="col-sm-4">{{ patient.address ?? '—' }}</dd>
      </dl>
    </UiCard>

    <UiCard v-if="auth.can('visits.view')" title="تاریخچه مراجعات">
      <div v-if="visits.length === 0" class="py-3 text-center text-muted">مراجعه‌ای ثبت نشده است.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>شماره مراجعه</th><th>تاریخ</th><th>بخش</th><th>داکتر</th><th>علت</th><th>وضعیت</th></tr>
          </thead>
          <tbody>
            <tr v-for="visit in visits" :key="visit.id">
              <td>
                <RouterLink v-if="auth.can('consultation.view')" :to="`/chart/${visit.id}`"><span class="ltr-num">{{ visit.visitNumber }}</span></RouterLink>
                <span v-else class="ltr-num">{{ visit.visitNumber }}</span>
              </td>
              <td>{{ formatDate(visit.visitDate) }}</td>
              <td>{{ visit.departmentName }}</td>
              <td>{{ visit.doctorName ?? '—' }}</td>
              <td>{{ visit.reason ?? '—' }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[visit.status]}`">{{ visitStatusLabels[visit.status] }}</span></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard v-if="auth.can('patients.edit') && changes.length" title="تاریخچه تغییرات">
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>فیلد</th><th>قبلی</th><th>جدید</th><th>کاربر</th><th>زمان</th></tr>
          </thead>
          <tbody>
            <tr v-for="(change, index) in changes" :key="index">
              <td>{{ fieldLabels[change.field] ?? change.field }}</td>
              <td>{{ change.oldValue ?? '—' }}</td>
              <td>{{ change.newValue ?? '—' }}</td>
              <td>{{ change.changedBy ?? '—' }}</td>
              <td>{{ formatDateTime(change.changedAt) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>

  <PatientFormModal v-if="editing && patient" :patient="patient" @close="editing = false" @saved="onSaved" />
  <NewVisitModal
    v-if="addingVisit && patient"
    :patient="{ id: patient.id, label: `${patient.fileNumber} — ${patientName(patient)}` }"
    @close="addingVisit = false"
    @saved="onVisitSaved"
  />
</template>
