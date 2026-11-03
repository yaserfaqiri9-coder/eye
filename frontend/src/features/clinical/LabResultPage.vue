<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, errorMessage } from '@/api/http'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import UiFormPage from '@/components/UiFormPage.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { diagnosticStatusLabels, type DiagnosticOrder } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ id: string }>()
const router = useRouter()
const auth = useAuthStore()

const order = ref<DiagnosticOrder | null>(null)
const form = reactive({ text: '' })
const { dirty, markSaved } = useUnsavedGuard(form)
const loading = ref(true)
const saving = ref(false)
const error = ref('')

async function load() {
  try {
    // Orders waiting for a result are recent, and the list is the only read this API offers for them.
    const statuses = ['Requested', 'InProgress']
    const pages = await Promise.all(statuses.map((status) => api<{ items: DiagnosticOrder[] }>(`/diagnostic-orders?status=${status}&pageSize=100`)))
    order.value = pages.flatMap((p) => p.items).find((row) => row.id === Number(props.id)) ?? null
    if (!order.value) throw new ApiError(404, 'not_found', 'این درخواست پیدا نشد یا نتیجه آن قبلاً ثبت شده است.')
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function submit() {
  saving.value = true
  error.value = ''
  try {
    await api(`/diagnostic-orders/${order.value!.id}/result`, { method: 'POST', body: { resultText: form.text, version: order.value!.version } })
    markSaved()
    await router.push({ name: 'lab' })
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <UiFormPage :title="order ? `ثبت نتیجه: ${order.testName}` : 'ثبت نتیجه آزمایش'" :back-to="{ name: 'lab' }" back-label="بازگشت به آزمایشگاه" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
    <template #summary>
      <section class="xr-card xr-card__body">
        <dl class="xr-facts">
          <div><dt>مریض</dt><dd>{{ order?.patientName ?? '...' }}</dd></div>
          <div><dt>شماره پرونده</dt><dd><span class="ltr-num">{{ order?.patientFileNumber ?? '...' }}</span></dd></div>
          <div><dt>شماره درخواست</dt><dd><span class="ltr-num">{{ order?.orderNumber ?? '...' }}</span></dd></div>
          <div><dt>زمان درخواست</dt><dd>{{ order ? formatDateTime(order.createdAt) : '...' }}</dd></div>
          <div><dt>وضعیت</dt><dd>{{ order ? diagnosticStatusLabels[order.status] : '...' }}</dd></div>
          <div><dt>توضیح داکتر</dt><dd>{{ order?.note ?? '—' }}</dd></div>
        </dl>
      </section>
    </template>

    <fieldset :disabled="loading || !order">
      <label class="form-label" for="r-text">نتیجه</label>
      <textarea id="r-text" v-model="form.text" class="form-control" rows="10" maxlength="4000" required></textarea>
    </fieldset>
    <template v-if="order">
      <hr />
      <AttachmentPanel owner-type="DiagnosticOrder" :owner-id="order.id" :can-upload="auth.can('diagnostics.edit')" />
    </template>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading || !order">ثبت نتیجه</button>
      <RouterLink :to="{ name: 'lab' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>
