<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { visitStatusLabels, visitStatusTones, type VisitRow } from '@/features/visits/types'
import { quantity } from '@/utils/money'
import type { PreExam } from './types'

const rows = ref<VisitRow[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    rows.value = await api<VisitRow[]>('/clinical/worklist?kind=triage')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const blank = { chiefComplaint: '', history: '', visualAcuityOd: '', visualAcuityOs: '', iopOd: '', iopOs: '', measurements: '', notes: '' }
const form = reactive({ visit: null as VisitRow | null, version: null as number | null, saving: false, error: '', ...blank })

async function open(visit: VisitRow) {
  Object.assign(form, { visit, version: null, saving: false, error: '', ...blank })
  try {
    const existing = await api<PreExam | undefined>(`/pre-examinations/${visit.id}`)
    if (existing) {
      Object.assign(form, {
        version: existing.version,
        chiefComplaint: existing.chiefComplaint ?? '',
        history: existing.history ?? '',
        visualAcuityOd: existing.visualAcuityOd ?? '',
        visualAcuityOs: existing.visualAcuityOs ?? '',
        iopOd: existing.iopOd ?? '',
        iopOs: existing.iopOs ?? '',
        measurements: existing.measurements ?? '',
        notes: existing.notes ?? '',
      })
    }
  } catch (cause) {
    form.error = errorMessage(cause)
  }
}

async function save(complete: boolean) {
  form.saving = true
  form.error = ''
  try {
    const saved = await api<PreExam>(`/pre-examinations/${form.visit!.id}`, {
      method: 'PUT',
      body: {
        chiefComplaint: form.chiefComplaint,
        history: form.history,
        visualAcuityOd: form.visualAcuityOd,
        visualAcuityOs: form.visualAcuityOs,
        iopOd: form.iopOd,
        iopOs: form.iopOs,
        measurements: form.measurements,
        notes: form.notes,
        version: form.version,
      },
    })
    form.version = saved.version
    if (complete) {
      await api(`/pre-examinations/${form.visit!.id}/complete`, { method: 'POST' })
    }
    form.visit = null
    await load()
  } catch (cause) {
    form.error = errorMessage(cause)
  } finally {
    form.saving = false
  }
}

useLiveRefresh(['VisitCreated', 'VisitStatusChanged', 'PreExamCompleted'], load)
onMounted(load)
</script>

<template>
  <UiCard title="صف معاینه ابتدایی">
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">مریضی در صف نیست.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نوبت</th><th>مریض</th><th>شماره پرونده</th><th>علت</th><th>وضعیت</th><th>عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td>{{ quantity(row.queueNumber) }}</td>
            <td>{{ row.patientName }}</td>
            <td><span class="ltr-num">{{ row.patientFileNumber }}</span></td>
            <td>{{ row.reason ?? '—' }}</td>
            <td><span class="xr-badge" :class="`xr-badge--${visitStatusTones[row.status]}`">{{ visitStatusLabels[row.status] }}</span></td>
            <td><button type="button" class="btn btn-soft btn-sm" @click="open(row)">ثبت معاینه</button></td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>

  <UiModal v-if="form.visit" :title="`معاینه ابتدایی: ${form.visit.patientName}`" @close="form.visit = null">
    <form @submit.prevent="save(false)">
      <div class="mb-3">
        <label class="form-label" for="pe-complaint">شکایت اصلی</label>
        <input id="pe-complaint" v-model="form.chiefComplaint" class="form-control" maxlength="500" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="pe-history">سابقه</label>
        <input id="pe-history" v-model="form.history" class="form-control" maxlength="1000" />
      </div>
      <div class="row g-3 mb-3">
        <div class="col-6">
          <label class="form-label" for="pe-va-od">دید چشم راست (VA OD)</label>
          <input id="pe-va-od" v-model="form.visualAcuityOd" class="form-control" dir="ltr" maxlength="30" />
        </div>
        <div class="col-6">
          <label class="form-label" for="pe-va-os">دید چشم چپ (VA OS)</label>
          <input id="pe-va-os" v-model="form.visualAcuityOs" class="form-control" dir="ltr" maxlength="30" />
        </div>
        <div class="col-6">
          <label class="form-label" for="pe-iop-od">فشار چشم راست (IOP OD)</label>
          <input id="pe-iop-od" v-model="form.iopOd" class="form-control" dir="ltr" maxlength="30" />
        </div>
        <div class="col-6">
          <label class="form-label" for="pe-iop-os">فشار چشم چپ (IOP OS)</label>
          <input id="pe-iop-os" v-model="form.iopOs" class="form-control" dir="ltr" maxlength="30" />
        </div>
      </div>
      <div class="mb-3">
        <label class="form-label" for="pe-measure">اندازه‌گیری‌های دیگر</label>
        <input id="pe-measure" v-model="form.measurements" class="form-control" maxlength="1000" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="pe-notes">یادداشت</label>
        <textarea id="pe-notes" v-model="form.notes" class="form-control" rows="2" maxlength="2000"></textarea>
      </div>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="d-flex gap-2">
        <button type="submit" class="btn btn-soft" :disabled="form.saving">ذخیره</button>
        <button type="button" class="btn btn-primary" :disabled="form.saving" @click="save(true)">ذخیره و ارسال به داکتر</button>
      </div>
    </form>
  </UiModal>
</template>
