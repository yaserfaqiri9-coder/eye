<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { liveConnected, useLiveRefresh } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import { formatTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import { createClipPlayer, MissingClipError } from './clipPlayer'

interface NextCall {
  id: number
  queueNumber: number
  destination: string
  clips: string[]
}

interface HistoryRow {
  id: number
  visitNumber: string | null
  queueNumber: number
  destination: string
  isRecall: boolean
  status: string
  outcome: string | null
  failureReason: string | null
  requestedAt: string
  finishedAt: string | null
  requestedBy: string | null
}

interface Status {
  active: boolean
  otherStationOnline: boolean
  clipsTotal: number
  clipsMissing: number
  missingSample: string[]
  testClips: string[]
  history: HistoryRow[]
}

const POLL_MS = 5_000
const STATUS_MS = 15_000
const PAUSE_BETWEEN_CALLS_MS = 700
const VOLUME_KEY = 'alshifa.audio.volume'
const STATION_KEY = 'alshifa.audio.station'

const statusLabels: Record<string, { label: string; tone: string }> = {
  Queued: { label: 'در نوبت', tone: 'orange' },
  Playing: { label: 'در حال پخش', tone: 'cyan' },
  Played: { label: 'پخش شد', tone: 'green' },
  Failed: { label: 'پخش نشد', tone: 'red' },
  Cancelled: { label: 'لغو شد', tone: 'orange' },
}

/** Per tab and kept across a reload, so a reload is the same station while a second tab is not. */
function loadStationId() {
  try {
    const stored = sessionStorage.getItem(STATION_KEY)
    if (stored) return stored
    const fresh = newIdempotencyKey()
    sessionStorage.setItem(STATION_KEY, fresh)
    return fresh
  } catch {
    return newIdempotencyKey()
  }
}

function loadVolume() {
  try {
    const stored = Number(localStorage.getItem(VOLUME_KEY))
    return stored > 0 && stored <= 1 ? stored : 1
  } catch {
    return 1
  }
}

const player = createClipPlayer()
const stationId = loadStationId()
const activated = ref(false)
const needsClick = ref(false)
const busyElsewhere = ref(false)
const serverReachable = ref(true)
const status = ref<Status | null>(null)
const nowPlaying = ref<NextCall | null>(null)
const volume = ref(loadVolume())
const error = ref('')
const testing = ref(false)

const reports: { id: number; played: boolean; reason: string | null }[] = []
let pumping = false
let pollTimer: ReturnType<typeof setInterval> | undefined
let statusTimer: ReturnType<typeof setInterval> | undefined

const packReady = computed(() => status.value !== null && status.value.clipsMissing === 0)
const packPending = computed(() => status.value !== null && status.value.clipsTotal > 0 && status.value.clipsMissing === status.value.clipsTotal)
const onAir = computed(() => activated.value && !needsClick.value && serverReachable.value)

player.setVolume(volume.value)
watch(volume, (value) => {
  player.setVolume(value)
  try {
    localStorage.setItem(VOLUME_KEY, String(value))
  } catch {
    // Only a convenience: the slider simply starts at full volume next time.
  }
})

async function loadStatus() {
  try {
    status.value = await api<Status>(`/calls/station/status?stationId=${stationId}`)
    serverReachable.value = true
  } catch (cause) {
    serverReachable.value = false
    if (!activated.value) error.value = errorMessage(cause)
  }
}

function stop(message: string) {
  activated.value = false
  error.value = message
}

async function activate(takeOver = false) {
  error.value = ''
  busyElsewhere.value = false
  try {
    // First, while still inside the click: browsers refuse sound without one.
    if (!(await player.unlock())) {
      error.value = 'مرورگر اجازه پخش صدا را نداد.'
      return
    }
    await api('/calls/station/claim', { method: 'POST', body: { stationId, takeOver } })
    activated.value = true
    needsClick.value = false
    await pump()
    await loadStatus()
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'station_busy') busyElsewhere.value = true
    error.value = errorMessage(cause)
  }
}

/** A result that cannot be delivered is kept and retried. Nothing new is taken until it arrives, so a played call is never reported lost. */
async function flushReports() {
  while (reports.length) {
    const report = reports[0]
    try {
      await api(`/calls/${report.id}/result`, { method: 'POST', body: { stationId, played: report.played, reason: report.reason } })
    } catch (cause) {
      const retry = cause instanceof ApiError && (cause.status === 0 || cause.status >= 500 || cause.code === 'concurrency_conflict' || cause.code === 'busy')
      if (retry) throw cause
      if (cause instanceof ApiError && cause.code === 'station_replaced') throw cause
    }
    reports.shift()
  }
}

/** Takes one call at a time. It runs on the live signal and on a timer, and the timer doubles as the heartbeat. */
async function pump() {
  if (!activated.value || pumping) return
  pumping = true
  try {
    if (!(await player.isRunning())) {
      // Taking calls now would lose them silently, so the server is allowed to see this station go quiet.
      needsClick.value = true
      return
    }
    needsClick.value = false
    await flushReports()

    while (activated.value) {
      const call = await api<NextCall | undefined>('/calls/station/next', { method: 'POST', body: { stationId } })
      serverReachable.value = true
      if (!call) return

      nowPlaying.value = call
      let reason: string | null = null
      try {
        await player.play(call.clips)
      } catch (cause) {
        reason = cause instanceof MissingClipError ? cause.message : 'پخش صدا در این رایانه ناموفق بود.'
        error.value = reason
      }
      reports.push({ id: call.id, played: reason === null, reason })
      await flushReports()
      nowPlaying.value = null
      await loadStatus()
      await new Promise((resolve) => setTimeout(resolve, PAUSE_BETWEEN_CALLS_MS))
    }
  } catch (cause) {
    nowPlaying.value = null
    if (cause instanceof ApiError && cause.code === 'station_replaced') {
      stop(cause.message)
    } else if (cause instanceof ApiError && (cause.status === 0 || cause.status >= 500)) {
      serverReachable.value = false
    } else {
      error.value = errorMessage(cause)
    }
  } finally {
    pumping = false
  }
}

async function testSound() {
  if (!status.value) return
  testing.value = true
  error.value = ''
  try {
    if (!(await player.unlock())) throw new Error('blocked')
    needsClick.value = false
    await player.play(status.value.testClips)
  } catch (cause) {
    error.value = cause instanceof MissingClipError ? cause.message : 'پخش آزمایشی ناموفق بود.'
  } finally {
    testing.value = false
  }
}

async function restoreSound() {
  if (await player.unlock()) {
    needsClick.value = false
    await pump()
  }
}

useLiveRefresh(['PatientCallRequested'], pump)

onMounted(() => {
  loadStatus()
  pollTimer = setInterval(pump, POLL_MS)
  statusTimer = setInterval(loadStatus, STATUS_MS)
})

onBeforeUnmount(() => {
  activated.value = false
  clearInterval(pollTimer)
  clearInterval(statusTimer)
})
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="needsClick" class="alert alert-warning mb-0" role="alert">
      مرورگر صدا را متوقف کرده است و تا فعال‌شدن دوباره، فراخوانی پخش نمی‌شود.
      <button type="button" class="btn btn-primary btn-sm ms-2" @click="restoreSound">فعال‌سازی دوباره صدا</button>
    </div>
    <div v-if="packPending" class="alert alert-warning mb-0" role="status">
      فراخوان صوتی در انتظار فعال‌سازی است. بقیه سیستم عادی کار می‌کند. برای فعال‌سازی، روی لپ‌تاپ میزبان <span class="ltr-num">Import-AlShifa-Voice.exe</span> را اجرا کنید.
    </div>
    <div v-else-if="status && status.clipsMissing > 0" class="alert alert-danger mb-0" role="alert">
      {{ quantity(status.clipsMissing) }} فایل از {{ quantity(status.clipsTotal) }} فایل مجموعه صوتی موجود نیست. فراخوانی که به این فایل‌ها نیاز دارد انجام نمی‌شود.
      <span class="ltr-num d-block small mt-1">{{ status.missingSample.join(', ') }}</span>
    </div>

    <UiCard title="مرکز پخش صوت">
      <dl class="xr-facts mb-3">
        <div>
          <dt>وضعیت این رایانه</dt>
          <dd><span class="xr-badge" :class="onAir ? 'xr-badge--green' : 'xr-badge--orange'">{{ onAir ? 'فعال و آماده پخش' : 'غیرفعال' }}</span></dd>
        </div>
        <div>
          <dt>اتصال به سرور</dt>
          <dd><span class="xr-badge" :class="serverReachable ? 'xr-badge--green' : 'xr-badge--red'">{{ serverReachable ? 'متصل' : 'قطع' }}</span></dd>
        </div>
        <div>
          <dt>اعلان زنده</dt>
          <dd><span class="xr-badge" :class="liveConnected ? 'xr-badge--green' : 'xr-badge--orange'">{{ liveConnected ? 'متصل' : 'در حال اتصال' }}</span></dd>
        </div>
        <div>
          <dt>مجموعه صوتی</dt>
          <dd><span class="xr-badge" :class="packReady ? 'xr-badge--green' : packPending ? 'xr-badge--orange' : 'xr-badge--red'">{{ packReady ? 'کامل' : packPending ? 'در انتظار فعال‌سازی' : 'ناقص' }}</span></dd>
        </div>
      </dl>

      <p v-if="nowPlaying" class="station-now" role="status">نوبت {{ quantity(nowPlaying.queueNumber) }}، {{ nowPlaying.destination }}</p>

      <div class="row g-3 align-items-end">
        <div class="col-md-5">
          <label class="form-label" for="station-volume">بلندی صدا</label>
          <input id="station-volume" v-model.number="volume" class="form-range" type="range" min="0.1" max="1" step="0.05" />
        </div>
        <div class="col-md-7 d-flex flex-wrap gap-2">
          <button v-if="!activated" type="button" class="btn btn-primary" @click="activate(false)">فعال‌سازی صدا در این رایانه</button>
          <button v-if="busyElsewhere" type="button" class="btn btn-soft-danger" @click="activate(true)">این رایانه مرکز پخش شود</button>
          <button type="button" class="btn btn-soft" :disabled="testing || !status" @click="testSound">آزمایش صدا</button>
        </div>
      </div>
      <p class="form-hint mt-3 mb-0">این صفحه باید روی رایانه اسپیکر باز بماند. پس از بازکردن دوباره صفحه یا مرورگر، یک بار دیگر صدا را فعال کنید.</p>
    </UiCard>

    <UiCard title="فراخوان‌های امروز">
      <div v-if="!status || status.history.length === 0" class="xr-state">فراخوانی ثبت نشده است.</div>
      <div v-else class="xr-table-wrap">
        <table class="xr-table">
          <thead><tr><th>نوبت</th><th>مراجعه</th><th>مقصد</th><th>زمان</th><th>کاربر</th><th>وضعیت</th></tr></thead>
          <tbody>
            <tr v-for="row in status.history" :key="row.id">
              <td>{{ quantity(row.queueNumber) }}</td>
              <td><span class="ltr-num">{{ row.visitNumber ?? '—' }}</span></td>
              <td>{{ row.destination }}<span v-if="row.isRecall" class="text-muted small"> (مجدد)</span></td>
              <td><span class="ltr-num">{{ formatTime(row.requestedAt) }}</span></td>
              <td>{{ row.requestedBy ?? '—' }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${statusLabels[row.status].tone}`" :title="row.failureReason ?? undefined">{{ statusLabels[row.status].label }}</span></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>
  </div>
</template>

<style scoped>
.station-now {
  margin-bottom: 1rem;
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--xr-primary);
}
</style>
