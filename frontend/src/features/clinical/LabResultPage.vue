<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
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
const form = reactive({ text: '', reason: '' })
const { dirty, markSaved, applying } = useUnsavedGuard(form)
const loading = ref(true)
const saving = ref(false)
const error = ref('')

/** A released result is corrected, not re-entered: the old text and the reason stay on the record. */
const amending = computed(() => order.value?.status === 'ResultReady')

async function load() {
  try {
    const loaded = await api<DiagnosticOrder>(`/diagnostic-orders/${props.id}`)
    if (loaded.status === 'Cancelled') throw new ApiError(409, 'invalid_transition', 'این درخواست لغو شده است.')
    order.value = loaded
    if (loaded.status === 'ResultReady') applying(() => (form.text = loaded.resultText ?? ''))
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function submit() {
  saving.value = true
  error.value = ''
  const row = order.value!
  try {
    if (amending.value) {
      await api(`/diagnostic-orders/${row.id}/result`, { method: 'PUT', body: { resultText: form.text, reason: form.reason, version: row.version } })
    } else {
      await api(`/diagnostic-orders/${row.id}/result`, { method: 'POST', body: { resultText: form.text, version: row.version } })
    }
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
  <UiFormPage :title="order ? `${amending ? 'اصلاح نتیجه' : 'ثبت نتیجه'}: ${order.testName}` : 'ثبت نتیجه آزمایش'" :back-to="{ name: 'lab' }" back-label="بازگشت به آزمایشگاه" :dirty="dirty" :saving="saving" :error="error" @submit="submit">
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
      <div v-if="amending" class="alert alert-warning" role="status">این نتیجه قبلاً ثبت شده است. متن قبلی و دلیل اصلاح در پرونده می‌ماند و به داکتر اطلاع داده می‌شود.</div>
      <label class="form-label" for="r-text">نتیجه</label>
      <textarea id="r-text" v-model="form.text" class="form-control" rows="10" maxlength="4000" required></textarea>
      <div v-if="amending" class="mt-3">
        <label class="form-label" for="r-reason">دلیل اصلاح</label>
        <input id="r-reason" v-model="form.reason" class="form-control" minlength="3" maxlength="500" required />
      </div>
    </fieldset>
    <template v-if="order">
      <hr />
      <AttachmentPanel owner-type="DiagnosticOrder" :owner-id="order.id" :can-upload="auth.can('diagnostics.edit')" />
    </template>

    <template #actions>
      <button type="submit" class="btn btn-primary" :disabled="saving || loading || !order">{{ amending ? 'ثبت اصلاحیه' : 'ثبت نتیجه' }}</button>
      <RouterLink :to="{ name: 'lab' }" class="btn btn-soft-danger">انصراف</RouterLink>
    </template>
  </UiFormPage>
</template>
