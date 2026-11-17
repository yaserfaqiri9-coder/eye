import { onBeforeUnmount, onMounted, ref } from 'vue'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

/** The payload is the event's identifiers as JSON text, never patient data. */
type Listener = (eventType: string, payload?: string) => void

const RECONNECTED = '*'
const RESTART_DELAY_MS = 5_000
const SAFETY_REFRESH_MS = 60_000

const listeners = new Set<Listener>()
let connection: HubConnection | null = null
let restartTimer: ReturnType<typeof setTimeout> | undefined

export const liveConnected = ref(false)

function notify(type: string, payload?: string) {
  for (const listener of listeners) listener(type, payload)
}

async function start() {
  if (!connection) return
  try {
    await connection.start()
    liveConnected.value = true
    // Anything missed while disconnected is read back from the database.
    notify(RECONNECTED)
  } catch {
    liveConnected.value = false
    restartTimer = setTimeout(start, RESTART_DELAY_MS)
  }
}

export function connectRealtime() {
  if (connection) return
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/events')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build()

  connection.on('event', (event: { type: string; payload?: string }) => notify(event.type, event.payload))
  connection.onreconnecting(() => (liveConnected.value = false))
  connection.onreconnected(() => {
    liveConnected.value = true
    notify(RECONNECTED)
  })
  connection.onclose(() => {
    liveConnected.value = false
    if (connection) restartTimer = setTimeout(start, RESTART_DELAY_MS)
  })
  start()
}

export function disconnectRealtime() {
  clearTimeout(restartTimer)
  const current = connection
  connection = null
  liveConnected.value = false
  if (current && current.state !== HubConnectionState.Disconnected) current.stop()
}

/**
 * Reloads from the API when one of the events arrives; events only signal, the API stays the source of truth.
 * accept narrows to the events a page cares about by their identifiers (one visit, say).
 * refreshMs 0 turns off the timed safety refresh, for a read that is costly or audited each time.
 */
export function useLiveRefresh(eventTypes: string[], load: () => unknown, refreshMs = SAFETY_REFRESH_MS, accept?: (type: string, ids: Record<string, unknown>) => boolean) {
  let debounce: ReturnType<typeof setTimeout> | undefined
  let timer: ReturnType<typeof setInterval> | undefined
  let running = false
  let again = false

  // One load at a time: a burst of events during a load causes one more load after it, never an overlapping one
  // whose older answer could land last and show stale rows.
  async function run() {
    if (running) {
      again = true
      return
    }
    running = true
    try {
      await load()
    } catch {
      // The page shows its own error.
    } finally {
      running = false
    }
    if (again) {
      again = false
      run()
    }
  }

  const listener: Listener = (type, payload) => {
    if (type !== RECONNECTED && (!eventTypes.includes(type) || (accept && !accept(type, parseIds(payload))))) return
    clearTimeout(debounce)
    debounce = setTimeout(run, 200)
  }

  // A hidden tab skips the safety refresh and catches up as soon as it is shown again.
  const onVisible = () => {
    if (!document.hidden) run()
  }

  onMounted(() => {
    listeners.add(listener)
    if (refreshMs <= 0) return
    // Safety net: a lost event can delay a screen but never leave it stale for long.
    timer = setInterval(() => {
      if (!document.hidden) run()
    }, refreshMs)
    document.addEventListener('visibilitychange', onVisible)
  })

  onBeforeUnmount(() => {
    listeners.delete(listener)
    clearTimeout(debounce)
    clearInterval(timer)
    document.removeEventListener('visibilitychange', onVisible)
  })
}

function parseIds(payload?: string): Record<string, unknown> {
  try {
    const parsed: unknown = payload ? JSON.parse(payload) : null
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}
