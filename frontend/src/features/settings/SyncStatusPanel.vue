<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { faDigits, formatDateTime } from '@/utils/date'

interface SyncStatus {
  mode: 'local' | 'mirror'
  enabled: boolean
  lastSuccessAt: string | null
  lastAttemptAt: string | null
  lastError: string | null
  pending: number | null
  intervalMinutes: number
  connected: boolean | null
}

const auth = useAuthStore()
const status = ref<SyncStatus | null>(null)
const error = ref('')
const notice = ref('')
let timer: ReturnType<typeof setTimeout> | undefined

async function load() {
  try {
    status.value = await api<SyncStatus>('/sync/status')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    clearTimeout(timer)
    timer = setTimeout(load, 30_000)
  }
}

async function runNow() {
  notice.value = ''
  try {
    await api('/sync/run', { method: 'POST' })
    notice.value = 'همگام‌سازی شروع شد.'
    setTimeout(load, 5000)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <section class="mb-4">
    <h3 class="h6 mb-2">همگام‌سازی با نسخه آنلاین</h3>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
    <template v-if="status && status.mode === 'local'">
      <div v-if="!status.enabled" class="alert alert-secondary" role="status">
        همگام‌سازی آنلاین روی این سرور تنظیم نشده است. کار شفاخانه به آن وابسته نیست.
      </div>
      <template v-else>
        <p class="form-hint">
          تغییرات هر {{ faDigits(status.intervalMinutes) }} دقیقه یک‌طرفه از سرور شفاخانه به نسخه آنلاین فرستاده می‌شود.
          اگر اینترنت قطع باشد، تغییرات نگه داشته می‌شوند و پس از وصل‌شدن خودکار فرستاده می‌شوند.
        </p>
        <div v-if="status.lastError" class="alert alert-warning" role="status">
          آخرین تلاش ناموفق بود: <span dir="auto">{{ status.lastError }}</span>
        </div>
        <div class="row g-3 mb-3">
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین همگام‌سازی موفق</div><strong>{{ formatDateTime(status.lastSuccessAt) }}</strong></div>
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین تلاش</div><strong>{{ formatDateTime(status.lastAttemptAt) }}</strong></div>
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">تغییرات در انتظار</div><strong>{{ faDigits(status.pending ?? 0) }}</strong></div>
          <div class="col-sm-6 col-lg-3">
            <div class="xr-meta">ارتباط</div>
            <strong :class="status.connected ? 'text-success' : 'text-danger'">{{ status.connected === null ? '—' : status.connected ? 'وصل' : 'قطع' }}</strong>
          </div>
        </div>
        <button v-if="auth.can('settings.edit')" type="button" class="btn btn-outline-primary" @click="runNow">همگام‌سازی همین حالا</button>
      </template>
    </template>
  </section>
</template>
