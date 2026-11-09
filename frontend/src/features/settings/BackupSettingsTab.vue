<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { faDigits, formatDateTime } from '@/utils/date'

interface BackupFile {
  name: string
  sizeBytes: number
  createdAt: string
}

interface BackupStatus {
  configured: boolean
  lastSuccessAt: string | null
  lastRunAt: string | null
  runningSince: string | null
  lastError: string | null
  needsAttention: boolean
  destination: string | null
  optionalDestination: string | null
  lastSizeBytes: number | null
  lastDurationSeconds: number | null
  backups: BackupFile[]
}

// Fast while a backup is running or was just requested, so the result shows up without reloading the page.
const ACTIVE_POLL_MS = 10_000
const IDLE_POLL_MS = 60_000

const auth = useAuthStore()
const status = ref<BackupStatus | null>(null)
const loading = ref(true)
const requesting = ref(false)
const requestedAt = ref<number | null>(null)
const error = ref('')
const notice = ref('')
let timer: ReturnType<typeof setTimeout> | undefined

// The scheduled task needs a few seconds to start, so a fresh request counts as running until the status file says otherwise.
const waiting = computed(() => requestedAt.value !== null && Date.now() - requestedAt.value < 3 * 60_000)
const running = computed(() => !!status.value?.runningSince || waiting.value)

function size(bytes: number | null) {
  if (bytes === null) return '—'
  if (bytes < 1024 * 1024) return `${faDigits((bytes / 1024).toFixed(0))} KB`
  if (bytes < 1024 * 1024 * 1024) return `${faDigits((bytes / 1024 / 1024).toFixed(1))} MB`
  return `${faDigits((bytes / 1024 / 1024 / 1024).toFixed(2))} GB`
}

function duration(seconds: number | null) {
  if (seconds === null) return '—'
  return seconds < 60 ? `${faDigits(seconds)} ثانیه` : `${faDigits(Math.round(seconds / 60))} دقیقه`
}

async function load() {
  try {
    const previous = status.value?.lastRunAt
    status.value = await api<BackupStatus>('/settings/backup-status')
    if (requestedAt.value !== null && status.value.lastRunAt !== previous && !status.value.runningSince) {
      requestedAt.value = null
      notice.value = status.value.lastError ? '' : 'پشتیبان‌گیری انجام شد.'
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
    clearTimeout(timer)
    timer = setTimeout(load, running.value ? ACTIVE_POLL_MS : IDLE_POLL_MS)
  }
}

async function runNow() {
  requesting.value = true
  error.value = ''
  notice.value = ''
  try {
    await api('/settings/backup/run', { method: 'POST' })
    requestedAt.value = Date.now()
    notice.value = 'پشتیبان‌گیری شروع شد. چند دقیقه طول می‌کشد؛ کار با سیستم در این مدت ادامه دارد.'
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    requesting.value = false
  }
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
  <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>

  <div v-else-if="status && !status.configured" class="alert alert-warning" role="status">
    پشتیبان‌گیری خودکار روی این کامپیوتر نصب نشده است. روی سرور شفاخانه، برنامه نصب این کار را خودکار تنظیم می‌کند.
  </div>

  <template v-else-if="status">
    <p class="form-hint">
      سیستم هر ۴ ساعت خودکار از کل دیتابیس، فایل‌های بارگذاری‌شده و کلیدهای ورود پشتیبان می‌گیرد. فایل پشتیبان رمزگذاری می‌شود و پس از نوشتن روی مقصد باز و بررسی می‌شود.
      همه پشتیبان‌های ۷ روز اخیر و پس از آن یکی در هر روز تا ۳۵ روز نگه داشته می‌شوند.
    </p>

    <div v-if="running" class="alert alert-info" role="status">
      پشتیبان‌گیری در حال اجراست<span v-if="status.runningSince"> (شروع: {{ formatDateTime(status.runningSince) }})</span>...
    </div>
    <div v-else-if="status.needsAttention" class="alert alert-danger" role="alert">
      پشتیبان‌گیری مشکل دارد.
      <span v-if="status.lastError" dir="auto"> خطا: {{ status.lastError }}</span>
      <span v-else> در ۲۴ ساعت اخیر پشتیبان موفق ثبت نشده است.</span>
    </div>
    <div v-else class="alert alert-success" role="status">پشتیبان‌گیری درست کار می‌کند.</div>

    <div class="row g-3 mb-3">
      <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین پشتیبان موفق</div><strong>{{ formatDateTime(status.lastSuccessAt) }}</strong></div>
      <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین اجرا</div><strong>{{ formatDateTime(status.lastRunAt) }}</strong></div>
      <div class="col-sm-6 col-lg-3"><div class="xr-meta">اندازه / مدت</div><strong>{{ size(status.lastSizeBytes) }} · {{ duration(status.lastDurationSeconds) }}</strong></div>
      <div class="col-sm-6 col-lg-3">
        <div class="xr-meta">مقصد</div>
        <strong dir="ltr">{{ status.destination ?? '—' }}</strong>
        <div v-if="status.optionalDestination" class="xr-meta" dir="ltr">{{ status.optionalDestination }}</div>
      </div>
    </div>

    <button v-if="auth.can('settings.edit')" type="button" class="btn btn-primary mb-3" :disabled="requesting || running" @click="runNow">
      پشتیبان‌گیری همین حالا
    </button>

    <div class="xr-table-wrap">
      <table class="xr-table">
        <thead><tr><th>تاریخ</th><th>فایل</th><th>اندازه</th></tr></thead>
        <tbody>
          <tr v-if="status.backups.length === 0"><td colspan="3" class="text-muted">هنوز پشتیبانی در مقصد ثبت نشده است.</td></tr>
          <tr v-for="file in status.backups" :key="file.name">
            <td>{{ formatDateTime(file.createdAt) }}</td>
            <td dir="ltr">{{ file.name }}</td>
            <td>{{ size(file.sizeBytes) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </template>
</template>
