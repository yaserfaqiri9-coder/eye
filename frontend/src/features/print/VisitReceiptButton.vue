<script setup lang="ts">
import { ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import type { Admission } from '@/features/visits/types'
import { prepareVisitReceipt } from './visitReceipt'

const props = defineProps<{ visitId: number; queueNumber: number | null }>()
const emit = defineEmits<{ issued: [row: Admission] }>()
const auth = useAuthStore()
const printing = ref(false)
const error = ref('')
const fallback = ref('')

async function print() {
  if (printing.value) return
  printing.value = true
  error.value = ''
  fallback.value = ''
  const receipt = prepareVisitReceipt(true)
  try {
    let number = props.queueNumber
    if (number === null) {
      const row = await api<Admission>(`/admissions/${props.visitId}/reserve-number`, { method: 'POST' })
      number = row.queueNumber
      emit('issued', row)
    }
    if (!receipt.open(props.visitId, number)) {
      fallback.value = `/print/visit/${props.visitId}?autoPrint=1`
      error.value = 'مرورگر پنجره چاپ را مسدود کرد؛ رسید را از لینک زیر باز کنید.'
    }
  } catch (cause) {
    receipt.close()
    error.value = errorMessage(cause)
  } finally {
    printing.value = false
  }
}
</script>

<template>
  <span v-if="auth.can('visits.print')" class="d-inline-flex flex-column align-items-start gap-1">
    <button type="button" class="btn btn-primary" :disabled="printing || (queueNumber === null && !auth.can('visitfees.create'))" @click="print">
      <i class="ri-printer-line" aria-hidden="true"></i> {{ printing ? 'آماده‌سازی چاپ…' : 'چاپ فوری رسید نوبت' }}
    </button>
    <span v-if="error" class="small text-danger text-wrap" role="alert">{{ error }}</span>
    <RouterLink v-if="fallback" :to="fallback" class="btn btn-soft btn-sm">باز کردن رسید در همین صفحه</RouterLink>
  </span>
</template>
