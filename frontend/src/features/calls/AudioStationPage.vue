<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue'
import { liveConnected } from '@/api/realtime'
import UiCard from '@/components/UiCard.vue'
import { formatTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import { useAudioStation } from './audioStation'

defineOptions({ name: 'AudioStationPage' })

const STATUS_MS = 15_000

const statusLabels: Record<string, { label: string; tone: string }> = {
  Queued: { label: 'در نوبت', tone: 'orange' },
  Playing: { label: 'در حال پخش', tone: 'cyan' },
  Played: { label: 'پخش شد', tone: 'green' },
  Failed: { label: 'پخش نشد', tone: 'red' },
  Cancelled: { label: 'لغو شد', tone: 'orange' },
}

const { activated, needsClick, busyElsewhere, serverReachable, status, nowPlaying, volume, error, testing, packReady, packPending, onAir, activate, loadStatus, testSound, restoreSound } = useAudioStation()

let statusTimer: ReturnType<typeof setInterval> | undefined

onMounted(() => {
  loadStatus()
  statusTimer = setInterval(loadStatus, STATUS_MS)
})

onBeforeUnmount(() => clearInterval(statusTimer))
</script>

<template>
  <div class="d-grid gap-4">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="needsClick" class="alert alert-warning mb-0" role="alert">
      مرورگر صدا را متوقف کرده است و تا فعال‌شدن دوباره، فراخوانی پخش نمی‌شود.
      <button type="button" class="btn btn-primary btn-sm ms-2" @click="restoreSound">فعال‌سازی دوباره صدا</button>
    </div>
    <div v-if="packPending" class="alert alert-warning mb-0" role="status">
      فایل‌های صوتی روی سرور نصب نشده‌اند و تا آن زمان هیچ فراخوانی پخش نمی‌شود. روی لپ‌تاپ میزبان <span class="ltr-num">Import-AlShifa-Voice.exe</span> را اجرا کنید.
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
      <p class="form-hint mt-3 mb-0">کافی است یک بار فعال شود: رفتن به صفحه‌های دیگر پخش را قطع نمی‌کند و پس از تازه‌کردن صفحه یا ورود دوباره، این رایانه با اولین کلیک خودش دوباره مرکز پخش می‌شود. اگر مرکز پخش فعال نباشد، فراخوان از رایانه‌ای پخش می‌شود که دکمه را زده است.</p>
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
