import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import { useAuthStore } from '@/stores/auth'
import { MissingClipError, sharedPlayer as player } from './clipPlayer'

export interface StationCall {
  id: number
  queueNumber: number
  destination: string
  clips: string[]
}

export interface StationHistoryRow {
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

export interface StationStatus {
  active: boolean
  otherStationOnline: boolean
  clipsTotal: number
  clipsMissing: number
  missingSample: string[]
  testClips: string[]
  history: StationHistoryRow[]
}

const POLL_MS = 5_000
const PAUSE_BETWEEN_CALLS_MS = 700
const VOLUME_KEY = 'alshifa.audio.volume'
const STATION_KEY = 'alshifa.audio.station'
const RESUME_KEY = 'alshifa.audio.resume'

function readStored(storage: () => Storage, key: string) {
  try {
    return storage().getItem(key)
  } catch {
    return null
  }
}

function writeStored(storage: () => Storage, key: string, value: string | null) {
  try {
    if (value === null) storage().removeItem(key)
    else storage().setItem(key, value)
  } catch {
    // Only a convenience: this computer is simply not remembered.
  }
}

const session = () => sessionStorage
const local = () => localStorage

/** Per tab and kept across a reload, so a reload is the same station while a second tab is not. */
function loadStationId() {
  const stored = readStored(session, STATION_KEY)
  if (stored) return stored
  const fresh = newIdempotencyKey()
  writeStored(session, STATION_KEY, fresh)
  return fresh
}

function loadVolume() {
  const stored = Number(readStored(local, VOLUME_KEY))
  return stored > 0 && stored <= 1 ? stored : 1
}

const stationId = loadStationId()
const activated = ref(false)
const needsClick = ref(false)
const waitingForClick = ref(false)
const busyElsewhere = ref(false)
const serverReachable = ref(true)
const status = ref<StationStatus | null>(null)
const nowPlaying = ref<StationCall | null>(null)
const volume = ref(loadVolume())
const error = ref('')
const testing = ref(false)

const reports: { id: number; played: boolean; reason: string | null }[] = []
let pumping = false
let activating = false

player.setVolume(volume.value)
watch(volume, (value) => {
  player.setVolume(value)
  writeStored(local, VOLUME_KEY, String(value))
})

async function loadStatus() {
  try {
    status.value = await api<StationStatus>(`/calls/station/status?stationId=${stationId}`)
    serverReachable.value = true
  } catch (cause) {
    serverReachable.value = false
    if (!activated.value) error.value = errorMessage(cause)
  }
}

function stop(message: string) {
  activated.value = false
  waitingForClick.value = false
  writeStored(local, RESUME_KEY, null)
  error.value = message
}

async function activate(takeOver = false, quiet = false) {
  if (activating) return
  activating = true
  if (!quiet) error.value = ''
  busyElsewhere.value = false
  try {
    // First, while still inside the click: browsers refuse sound without one.
    if (!(await player.unlock())) {
      if (!quiet) error.value = 'مرورگر اجازه پخش صدا را نداد.'
      return
    }
    await api('/calls/station/claim', { method: 'POST', body: { stationId, takeOver } })
    activated.value = true
    needsClick.value = false
    waitingForClick.value = false
    writeStored(local, RESUME_KEY, '1')
    await pump()
    await loadStatus()
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'station_busy') {
      // Another computer is the station now. Taking over stays a deliberate click on the station page.
      busyElsewhere.value = true
      waitingForClick.value = false
    }
    if (!quiet) error.value = errorMessage(cause)
  } finally {
    activating = false
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
      const call = await api<StationCall | undefined>('/calls/station/next', { method: 'POST', body: { stationId } })
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

/** Any click anywhere in the system counts as the gesture browsers ask for, so no one has to return to the station page. */
function onGesture() {
  if (waitingForClick.value) void activate(false, true)
  else if (needsClick.value) void restoreSound()
}

/**
 * Runs the station from the layout, so it belongs to the computer's session and not to one page. A computer that was
 * the station before takes the role back by itself after a reload or a new sign-in, at the first click if the browser requires one.
 */
export function useAudioStationHost() {
  const enabled = useAuthStore().can('calls.manage')
  let timer: ReturnType<typeof setInterval> | undefined

  useLiveRefresh(enabled ? ['PatientCallRequested'] : [], () => { void pump() })

  onMounted(() => {
    if (!enabled) return
    timer = setInterval(pump, POLL_MS)
    window.addEventListener('pointerdown', onGesture, true)
    window.addEventListener('keydown', onGesture, true)
    if (readStored(local, RESUME_KEY) === '1' && !activated.value) {
      waitingForClick.value = true
      void activate(false, true)
    }
  })

  onBeforeUnmount(() => {
    clearInterval(timer)
    window.removeEventListener('pointerdown', onGesture, true)
    window.removeEventListener('keydown', onGesture, true)
    activated.value = false
    needsClick.value = false
    waitingForClick.value = false
  })
}

export function useAudioStation() {
  return {
    activated,
    needsClick,
    waitingForClick,
    busyElsewhere,
    serverReachable,
    status,
    nowPlaying,
    volume,
    error,
    testing,
    packReady: computed(() => status.value !== null && status.value.clipsMissing === 0),
    packPending: computed(() => status.value !== null && status.value.clipsTotal > 0 && status.value.clipsMissing === status.value.clipsTotal),
    onAir: computed(() => activated.value && !needsClick.value && serverReachable.value),
    needsAttention: computed(() => waitingForClick.value || needsClick.value),
    activate,
    loadStatus,
    testSound,
    restoreSound,
  }
}
