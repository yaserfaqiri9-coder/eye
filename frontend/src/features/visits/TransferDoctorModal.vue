<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { doctorOptions } from '@/api/lookups'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import type { VisitRow } from './types'

const props = defineProps<{ visit: VisitRow }>()
const emit = defineEmits<{ close: []; saved: [] }>()

const doctors = ref<ComboOption[]>([])
const form = reactive({ doctorStaffId: null as number | null, reason: '', saving: false, error: '' })

async function save() {
  if (!form.doctorStaffId) {
    form.error = 'داکتر را انتخاب کنید.'
    return
  }
  form.saving = true
  form.error = ''
  try {
    await api(`/visits/${props.visit.id}/transfer-doctor`, { method: 'POST', body: { doctorStaffId: form.doctorStaffId, reason: form.reason, version: props.visit.version } })
    emit('saved')
  } catch (cause) {
    form.error = errorMessage(cause)
  } finally {
    form.saving = false
  }
}

onMounted(async () => {
  try {
    doctors.value = (await doctorOptions()).filter((d) => d.value !== props.visit.doctorStaffId)
  } catch (cause) {
    form.error = errorMessage(cause)
  }
})
</script>

<template>
  <UiModal :title="`انتقال مسئولیت ${visit.patientName}`" @close="$emit('close')">
    <form @submit.prevent="save">
      <p>داکتر فعلی: {{ visit.doctorName ?? '—' }}</p>
      <div class="mb-3"><UiComboBox v-model="form.doctorStaffId" label="داکتر جدید" :options="doctors" required chip /></div>
      <div class="mb-3">
        <label class="form-label" for="t-reason">دلیل انتقال</label>
        <input id="t-reason" v-model="form.reason" class="form-control" minlength="3" maxlength="300" required />
      </div>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="form.saving">ثبت انتقال</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
