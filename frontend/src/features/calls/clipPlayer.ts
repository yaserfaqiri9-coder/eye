/** Recorded assets are immutable and versioned together with the server's VoicePack.RecordedBase. */
export const RECORDED_VERSION = 'recorded-c98dd13ed170'
export const RECORDED_BASE = `/audio/${RECORDED_VERSION}/WAV/`
export const RECORDED_DESTINATIONS = ['doctor', 'refraction', 'imaging', 'pharmacy', 'optical', 'initial_exam', 'reception', 'cashier', 'surgery'] as const
export const recordedClips = [
  ...Array.from({ length: 50 }, (_, i) => `${RECORDED_BASE}numbers/number_${String(i + 1).padStart(3, '0')}.wav`),
  ...RECORDED_DESTINATIONS.map(key => `${RECORDED_BASE}destinations/destination_${key}.wav`),
]
const CACHE_NAME = `alshifa-queue-${RECORDED_VERSION}`
const END_GRACE_MS = 3_000
const RESUME_WAIT_MS = 300
// Original edges contain ~45 ms leading + ~65 ms trailing silence. Add only 30 ms;
// preserve every supplied PCM sample, with no trimming, fades or per-clip gain.
export const JOIN_GAP_MS = 30

export class MissingClipError extends Error {
  constructor(public readonly clip: string) {
    super(clip ? `فایل صوتی ${clip} بارگذاری نشد؛ اتصال محلی را بررسی و دوباره تلاش کنید.` : 'برای این شماره یا مقصد صدای ضبط‌شده موجود نیست؛ شماره‌های بسته ۱ تا ۵۰ است. فراخوان ثبت شده و شماره یا مقصد جایگزین نشده است.')
  }
}

/** Copy unchanged PCM into a single buffer: one source.start for the entire announcement. */
export function joinBuffers(context: BaseAudioContext, buffers: AudioBuffer[], gapMs = JOIN_GAP_MS) {
  if (!buffers.length) throw new MissingClipError('')
  const rate = buffers[0]!.sampleRate
  const channels = buffers[0]!.numberOfChannels
  if (buffers.some(b => b.sampleRate !== rate || b.numberOfChannels !== channels)) throw new Error('قالب فایل‌های صوتی یکسان نیست؛ بسته را بررسی کنید.')
  const gap = Math.round(rate * gapMs / 1000)
  const length = buffers.reduce((total, b) => total + b.length, 0) + gap * (buffers.length - 1)
  const joined = context.createBuffer(channels, length, rate)
  let offset = 0
  buffers.forEach((buffer, index) => {
    for (let channel = 0; channel < channels; channel++) joined.getChannelData(channel).set(buffer.getChannelData(channel), offset)
    offset += buffer.length + (index + 1 < buffers.length ? gap : 0)
  })
  return joined
}

export function createClipPlayer() {
  let context: AudioContext | null = null
  let gain: GainNode | null = null
  let volume = 1
  let chain: Promise<unknown> = Promise.resolve()
  const cache = new Map<string, Promise<AudioBuffer>>()
  const events = new Map<string, Promise<void>>()
  const played = new Set<string>()
  const playedKey = `alshifa.audio.played.${RECORDED_VERSION}`
  try { for (const id of JSON.parse(sessionStorage.getItem(playedKey) ?? '[]') as string[]) played.add(id) } catch { /* Storage is optional. */ }

  async function resume(target: AudioContext) {
    await Promise.race([target.resume().catch(() => undefined), new Promise(done => setTimeout(done, RESUME_WAIT_MS))])
  }
  /** Call within the user gesture, before waiting for the server. */
  async function unlock() {
    if (!context || context.state === 'closed') {
      context = new AudioContext({ sampleRate: 48_000 })
      gain = context.createGain()
      gain.gain.value = volume
      gain.connect(context.destination)
    }
    await resume(context)
    return context.state === 'running'
  }
  async function isRunning() {
    if (!context) return false
    if (context.state !== 'running') await resume(context)
    return context.state === 'running'
  }
  function setVolume(value: number) {
    volume = Math.min(1, Math.max(0, value))
    if (gain) gain.gain.value = volume
  }

  async function rawClip(clip: string) {
    // Only bundled recorded WAVs can be requested. No external URL or old voice pack.
    if (!recordedClips.includes(clip)) throw new MissingClipError(clip)
    let persistent: Cache | null = null
    try {
      if ('caches' in globalThis) persistent = await caches.open(CACHE_NAME)
      const saved = await persistent?.match(clip)
      if (saved?.ok) return saved.arrayBuffer()
    } catch { /* Memory and the local server remain available without CacheStorage. */ }
    try {
      const response = await fetch(clip, { signal: AbortSignal.timeout(15_000) })
      if (!response.ok) throw new MissingClipError(clip)
      const data = await response.clone().arrayBuffer()
      if (persistent) { try { await persistent.put(clip, response) } catch { /* Quota is optional. */ } }
      return data
    } catch { throw new MissingClipError(clip) }
  }
  function load(clip: string) {
    let buffer = cache.get(clip)
    if (!buffer) {
      buffer = (async () => {
        if (!context) throw new Error('ابتدا صدا را فعال کنید.')
        try { return await context.decodeAudioData(await rawClip(clip)) }
        catch (cause) {
          if (cause instanceof MissingClipError) throw cause
          try { if ('caches' in globalThis) await (await caches.open(CACHE_NAME)).delete(clip) } catch { /* Optional cache. */ }
          throw new Error(`فایل صوتی ${clip} قابل خواندن نیست؛ بسته را دوباره آماده کنید.`)
        }
      })()
      cache.set(clip, buffer)
      void buffer.catch(() => cache.delete(clip))
    }
    return buffer
  }

  /** Preload every supported pair for playback without internet; errors remain visible and retryable. */
  async function prepare() {
    if (!context) throw new Error('ابتدا صدا را فعال کنید.')
    let index = 0
    // Limit concurrent downloads/decodes on the small computers driving the waiting-room TV.
    await Promise.all(Array.from({ length: 4 }, async () => {
      while (index < recordedClips.length) await load(recordedClips[index++]!)
    }))
    try {
      if ('caches' in globalThis) for (const key of await caches.keys()) {
        if (key.startsWith('alshifa-queue-') && key !== CACHE_NAME) await caches.delete(key)
      }
    } catch { /* Never clear unrelated application caches. */ }
  }

  function playBuffer(buffer: AudioBuffer) {
    return new Promise<void>((resolve, reject) => {
      if (context?.state !== 'running') { reject(new Error('صدا متوقف است؛ دکمه فعال‌کردن صدا را بزنید.')); return }
      const source = context.createBufferSource()
      source.buffer = buffer
      source.connect(gain!)
      let finished = false
      const finish = (error?: Error) => {
        if (finished) return
        finished = true
        clearTimeout(watchdog)
        source.disconnect()
        error ? reject(error) : resolve()
      }
      const watchdog = setTimeout(() => {
        // Stop the old source before the next queued announcement is allowed to start.
        source.onended = null
        try { source.stop() } catch { /* Already ended. */ }
        finish(new Error('پخش صدا کامل نشد؛ صدا را دوباره فعال و از بخش مربوط فراخوان مجدد بزنید.'))
      }, buffer.duration * 1000 + END_GRACE_MS)
      source.onended = () => finish()
      try { source.start() } catch { finish(new Error('مرورگر اجازه پخش نداد؛ صدا را دوباره فعال کنید.')) }
    })
  }

  /** Snapshot at enqueue time. Event IDs deduplicate deliveries; an intentional recall has its own server ID. */
  function play(clips: readonly string[], eventId?: number | string) {
    const snapshot = [...clips]
    const id = eventId === undefined ? undefined : String(eventId)
    if (id && played.has(id)) return Promise.resolve()
    if (id && events.has(id)) return events.get(id)!
    const run = chain.then(async () => {
      if (!snapshot.length) throw new MissingClipError('')
      if (!(await isRunning())) throw new Error('ابتدا صدا را فعال کنید.')
      const buffers = await Promise.all(snapshot.map(load))
      await playBuffer(joinBuffers(context!, buffers))
      if (id) {
        played.add(id)
        try { sessionStorage.setItem(playedKey, JSON.stringify([...played])) } catch { /* Deduplication stays in memory. */ }
      }
    })
    if (id) events.set(id, run)
    void run.finally(() => { if (id) events.delete(id) }).catch(() => undefined)
    chain = run.catch(() => undefined)
    return run
  }
  return { unlock, isRunning, setVolume, prepare, play }
}
export const sharedPlayer = createClipPlayer()
