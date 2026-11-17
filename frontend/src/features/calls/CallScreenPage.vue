<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { connectRealtime, disconnectRealtime } from '@/api/realtime'
import { api } from '@/api/http'
import { useAudioStation, useAudioStationHost } from './audioStation'

defineOptions({ name: 'CallScreenPage' })
const route = useRoute()
useAudioStationHost()
const { activated, onAir, needsAttention, busyElsewhere, error, preparing, retryAudio, activate, restoreSound, loadStatus } = useAudioStation()
interface DisplayCall { id: number; queueNumber: number; destination: string; patientName: string | null }
const shown = ref<DisplayCall | null>(null)
const displayOffline = ref(false)
let displayLoading = false
let disposed = false
async function loadDisplay() {
  if (displayLoading) return
  displayLoading = true
  try {
    const call = await api<DisplayCall | undefined>('/calls/display')
    if (!disposed) {
      shown.value = call ?? null
      displayOffline.value = false
    }
  } catch {
    if (!disposed) {
      shown.value = null
      displayOffline.value = true
    }
  } finally { displayLoading = false }
}
const digits = (value: string | number) => String(value).replace(/\d/g, (digit) => '۰۱۲۳۴۵۶۷۸۹'[Number(digit)]!)
const ticket = computed(() => shown.value ? digits(shown.value.queueNumber) : '—')
const destination = computed(() => shown.value?.destination || '—')
const now = ref(new Date())
const time = computed(() => new Intl.DateTimeFormat('fa-AF', { timeZone: 'Asia/Kabul', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(now.value))
const date = computed(() => {
  const parts = new Intl.DateTimeFormat('fa-AF-u-ca-persian', { timeZone: 'Asia/Kabul', weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }).formatToParts(now.value)
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((entry) => entry.type === type)?.value || ''
  return `${part('weekday')}، ${part('day')} ${part('month')} ${part('year')}`
})
const scale = ref(1)
function resize() { scale.value = Math.min(window.innerWidth / 1671, window.innerHeight / 939.9375) }
let clockTimer: ReturnType<typeof setInterval> | undefined
let statusTimer: ReturnType<typeof setInterval> | undefined
let displayTimer: ReturnType<typeof setInterval> | undefined
async function start() {
  // Fullscreen requires the user gesture, before awaiting audio/network operations.
  if (!document.fullscreenElement) void document.documentElement.requestFullscreen?.().catch(() => {})
  if (busyElsewhere.value) await activate(true)
  else if (!activated.value) await activate(false)
  else await restoreSound()
}
onMounted(() => {
  resize()
  window.addEventListener('resize', resize)
  connectRealtime()
  void loadStatus()
  void loadDisplay()
  displayTimer = setInterval(loadDisplay, 1_000)
  // Only the dedicated Windows TV browser enables unattended audio. Normal browsers still ask for a click.
  // Authentication and station ownership checks remain in activate(); never take over another station automatically.
  if (route.query.tv === 'windows') void activate(false, true)
  statusTimer = setInterval(loadStatus, 10_000)
  clockTimer = setInterval(() => (now.value = new Date()), 1_000)
})
onBeforeUnmount(() => {
  disposed = true
  clearInterval(displayTimer)
  clearInterval(statusTimer)
  clearInterval(clockTimer)
  window.removeEventListener('resize', resize)
  disconnectRealtime()
})
</script>

<template>
  <main class="lcd" dir="rtl" aria-label="نمایش فراخوان مریضان">
    <div class="lcd__canvas" :style="{ transform: `translate(-50%, -50%) scale(${scale})` }">
      <!-- Only the photographic eye is clipped from the reference; all text and UI surfaces are HTML/CSS. -->
      <img class="lcd__eye" src="/images/lcd/reference-eye.png" alt="" aria-hidden="true" />
      <svg class="lcd__waves" viewBox="0 0 1671 940" fill="none" aria-hidden="true">
        <defs><linearGradient id="lcd-wave" x1="0" y1="850" x2="1550" y2="650" gradientUnits="userSpaceOnUse"><stop stop-color="#0061be"/><stop offset=".36" stop-color="#74b7fa"/><stop offset=".67" stop-color="#e6f2ff"/><stop offset="1" stop-color="#b1d8ff"/></linearGradient></defs>
        <path d="M0 922 C240 833 391 878 635 912 C1020 981 1465 759 1671 566 L1671 940 H0Z" fill="url(#lcd-wave)"/>
        <path d="M128 940 C356 831 486 915 714 926 C1110 955 1505 834 1671 702 V940Z" fill="#edf6ff" fill-opacity=".8"/>
      </svg>
      <header class="lcd__header">
        <img class="lcd__logo" src="/print/alshefa-official-logo.png" alt="لوگوی شفاخانه تخصصی چشم الشفا" />
        <span class="lcd__brand-divider" aria-hidden="true"></span>
        <div class="lcd__brand"><h1>شفاخانه تخصصی چشم الشفا</h1><p dir="ltr">AL-SHEFA SPECIALISTIC EYE HOSPITAL</p></div>
        <div class="lcd__datetime"><time class="lcd__time" dir="ltr">{{ time }}</time><time class="lcd__date">{{ date }}</time></div>
        <span class="lcd__date-divider" aria-hidden="true"></span>
        <svg class="lcd__calendar" viewBox="0 0 60 60" fill="none" stroke="currentColor" stroke-width="4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="7" y="12" width="46" height="41" rx="4"/><path d="M7 25H53M18 7V18M42 7V18"/><path d="M18 33H21M29 33H32M40 33H43M18 43H21M29 43H32" stroke-linecap="butt"/></svg>
      </header>
      <section class="lcd__call" aria-live="assertive" aria-atomic="true" aria-label="فراخوان جاری">
        <div class="lcd__number-panel">
          <div class="lcd__label">بیمار شماره</div>
          <div v-if="shown?.patientName" class="lcd__patient-name">{{ shown.patientName }}</div>
          <div class="lcd__number" :class="{ 'lcd__number--named': shown?.patientName }" dir="ltr" :style="{ fontSize: `${Math.min(shown?.patientName ? 310 : 440, ticket.length > 2 ? 650 / (ticket.length * .65) : 440)}px` }"><span class="lcd__digits">{{ ticket }}</span></div>
        </div>
        <div class="lcd__destination-bar">
          <span class="lcd__please">لطفاً به بخش</span>
          <span class="lcd__destination" :style="{ fontSize: `${destination.length > 9 ? Math.max(24, 640 / destination.length) : 80}px` }">{{ destination }}</span>
          <span class="lcd__proceed">مراجعه کنید.</span>
          <span class="lcd__icon-divider" aria-hidden="true"></span>
          <svg class="lcd__pharmacy" viewBox="0 0 110 112" fill="none" stroke="currentColor" stroke-width="6" stroke-linejoin="round" aria-hidden="true"><rect x="20" y="8" width="47" height="20" rx="4"/><path d="M24 29V38L13 49V91Q13 97 19 97H53M63 29V38L74 49V64M14 51H73M14 66H73M31 76H53M42 65V87"/><g transform="rotate(-36 79 87)"><rect x="55" y="73" width="49" height="28" rx="14"/><path d="M80 74V100"/></g></svg>
        </div>
      </section>
      <footer class="lcd__footer">ساخته شده توسط شرکت خدمات تکنالوژی نواتیک</footer>
    </div>
    <div v-if="displayOffline" class="lcd__offline" role="status">ارتباط نمایش با سرور قطع است؛ در حال اتصال دوباره…</div>
    <div v-if="error && onAir" class="lcd__audio-error" role="alert">
      {{ error }}
      <button type="button" :disabled="preparing" @click="retryAudio">تلاش مجدد برای بارگذاری صدا</button>
      <span>برای تکرار اعلان، از بخش مربوط فراخوان مجدد بزنید.</span>
    </div>
    <button v-if="!onAir || needsAttention" class="lcd__start" type="button" :disabled="preparing" @click="start">
      <strong>{{ busyElsewhere ? 'برای انتقال صدا به این تلویزیون کلیک کنید' : 'برای فعال‌سازی صدا در این تلویزیون کلیک کنید' }}</strong>
      <span v-if="preparing">آماده‌سازی صداهای محلی…</span>
      <span v-if="error">{{ error }}</span>
    </button>
  </main>
</template>

<style scoped>
.lcd__audio-error { position: fixed; inset-inline: 16px; bottom: 16px; z-index: 5; padding: 12px 18px; background: #fff1ed; color: #842b20; border: 1px solid #e4b0a5; border-radius: 10px; cursor: auto; }
.lcd__audio-error button { margin-inline: 12px; padding: 6px 12px; cursor: pointer; }
.lcd__audio-error span { display: block; font-size: 13px; margin-top: 6px; }
.lcd { position: fixed; inset: 0; overflow: hidden; background: #e8f3ff; color: #061c50; cursor: none; font-family: 'Vazirmatn', sans-serif; }
.lcd *, .lcd *::before, .lcd *::after { box-sizing: border-box; }
.lcd__canvas { position: absolute; left: 50%; top: 50%; width: 1671px; height: 939.9375px; overflow: hidden; transform-origin: center; background: radial-gradient(ellipse at 48% 43%, #fff 0%, #f8fbff 39%, #e4f2ff 77%, #d3e9ff 100%); }
.lcd__eye { position: absolute; inset: 0; width: 1671px; height: 941px; pointer-events: none; clip-path: polygon(0 22.7%, 5.4% 27.1%, 10.8% 34%, 15.6% 44.6%, 19.2% 58.4%, 20.9% 69.1%, 11.3% 71.2%, 11.3% 90.3%, 0 91.4%); mask-image: linear-gradient(to bottom, #000 81%, transparent 92%), linear-gradient(to right, #000 6%, #000b 11%, transparent 21%); mask-composite: intersect; }
.lcd__waves { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.lcd__logo { position: absolute; left: 53px; top: 20px; width: 201px; height: 201px; object-fit: contain; }
.lcd__brand-divider { position: absolute; left: 273px; top: 70px; height: 87px; border-left: 2px solid #627083; }
.lcd__brand { position: absolute; left: 305px; top: 47px; text-align: left; white-space: nowrap; }
.lcd__brand h1 { margin: 0; color: #061c50; font-size: 50px; font-weight: 900; line-height: 1.3; letter-spacing: -1.5px; }
.lcd__brand p { margin: 0 0 0 4px; font-family: Arial, sans-serif; font-weight: 700; font-size: 30px; line-height: 1.65; color: #646e7f; }
.lcd__datetime { position: absolute; top: 38px; left: 1267px; width: 250px; text-align: center; }
.lcd__time { display: block; font-size: 73px; font-weight: 900; line-height: 1.22; letter-spacing: -3px; border-bottom: 2px solid #8d98a8; }
.lcd__date { display: block; padding-top: 6px; font-size: 25px; font-weight: 800; color: #606a79; white-space: nowrap; }
.lcd__date-divider { position: absolute; left: 1530px; top: 70px; height: 87px; border-left: 2px solid #627083; }
.lcd__calendar { position: absolute; left: 1551px; top: 70px; width: 60px; height: 60px; }
.lcd__number-panel { position: absolute; left: 455px; top: 267px; width: 761px; height: 389px; border: 2px solid #fff; border-radius: 82px; background: linear-gradient(115deg, #ffffffc9, #ffffffec 49%, #f4f9ffd9); box-shadow: 0 17px 25px -8px #64aef288; }
.lcd__label { position: absolute; top: -57px; left: 130px; width: 496px; height: 99px; display: flex; align-items: center; justify-content: center; border-radius: 55px; border: 1px solid #ffffffd9; background: linear-gradient(#e2f0ff, #b4daff); box-shadow: 0 15px 25px #85bcf333; font-size: 65px; font-weight: 900; line-height: 1; }
.lcd__number { position: absolute; inset: 106px 15px 0; display: flex; align-items: center; justify-content: center; font-weight: 900; letter-spacing: -12px; }
.lcd__number--named { top: 135px; }
.lcd__patient-name { position: absolute; top: 48px; left: 24px; right: 24px; height: 60px; display: flex; align-items: center; justify-content: center; font-size: 38px; font-weight: 800; line-height: 1.3; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.lcd__digits { line-height: 1.2; background: linear-gradient(140deg, #04356b 20%, #041a40 80%); background-clip: text; -webkit-text-fill-color: transparent; }
.lcd__destination-bar { position: absolute; top: 679px; left: 199px; width: 1273px; height: 147px; border: 2px solid #fff; border-radius: 76px; background: linear-gradient(110deg, #c5e5ffcc, #f2f9ffdc 44%, #c3e1ffcc); box-shadow: 0 18px 24px -10px #4e9fe288; }
.lcd__please, .lcd__proceed { position: absolute; display: flex; align-items: center; justify-content: center; top: 8px; height: 122px; font-size: 54px; font-weight: 900; white-space: nowrap; letter-spacing: -1.5px; }
.lcd__please { right: 43px; width: 316px; }
.lcd__proceed { left: 225px; width: 285px; }
.lcd__destination { position: absolute; left: 528px; top: 12px; width: 364px; height: 119px; display: flex; align-items: center; justify-content: center; padding: 0 15px; border: 1px solid #b9dcff; border-radius: 65px; background: linear-gradient(155deg, #034797, #002053); color: #fff; font-weight: 900; white-space: normal; overflow-wrap: anywhere; text-align: center; line-height: 1.15; }
.lcd__icon-divider { position: absolute; left: 201px; top: 27px; height: 90px; border-left: 2px solid #315984; }
.lcd__pharmacy { position: absolute; left: 64px; top: 14px; width: 110px; height: 112px; color: #06366b; }
.lcd__footer { position: absolute; right: 49px; bottom: 24px; font-size: 27px; font-weight: 800; color: #566174; }
.lcd__offline { position: absolute; top: 0; left: 0; right: 0; padding: 10px; background: #8d261d; color: #fff; text-align: center; }
.lcd__start { position: absolute; left: 12px; bottom: 12px; max-width: 65%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; border: 0; border-radius: 8px; background: #061c50e8; color: #fff; padding: 10px 16px; cursor: pointer; font: inherit; font-size: clamp(12px, 1.1vw, 20px); text-align: center; }
.lcd__start span { font-size: .65em; }
.lcd__start:focus-visible { outline: 4px solid #fff; outline-offset: -8px; }
</style>
