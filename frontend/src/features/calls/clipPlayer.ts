const CACHE_LIMIT = 120
const END_GRACE_MS = 3_000
const RESUME_WAIT_MS = 300

export class MissingClipError extends Error {
  constructor(public readonly clip: string) {
    super(`فایل صوتی ${clip} موجود نیست.`)
  }
}

/**
 * Plays the local voice pack through one audio context. Browsers allow sound only after a click, and once that context
 * runs, later announcements need no further click. Everything goes through one chain, so two clips can never overlap.
 */
export function createClipPlayer() {
  let context: AudioContext | null = null
  let gain: GainNode | null = null
  let volume = 1
  let chain: Promise<unknown> = Promise.resolve()
  const cache = new Map<string, AudioBuffer>()

  /** Must be called from a click. */
  async function unlock() {
    if (!context) {
      context = new AudioContext()
      gain = context.createGain()
      gain.gain.value = volume
      gain.connect(context.destination)
    }
    await resume(context)
    return context.state === 'running'
  }

  /** A browser still waiting for a click leaves resume() pending forever, which must not stop whoever is waiting on it. */
  async function resume(target: AudioContext) {
    await Promise.race([target.resume().catch(() => undefined), new Promise((done) => setTimeout(done, RESUME_WAIT_MS))])
  }

  /** False when the browser has suspended audio and only another click can bring it back. */
  async function isRunning() {
    if (!context) return false
    if (context.state !== 'running') await resume(context)
    return context.state === 'running'
  }

  function setVolume(value: number) {
    volume = value
    if (gain) gain.gain.value = value
  }

  async function load(clip: string) {
    const cached = cache.get(clip)
    if (cached) {
      // Reinserting keeps the most recently used clips at the end, away from eviction.
      cache.delete(clip)
      cache.set(clip, cached)
      return cached
    }

    const response = await fetch(`/api/calls/voice/${clip}`, { credentials: 'same-origin' })
    if (response.status === 404) throw new MissingClipError(clip)
    if (!response.ok) throw new Error(`voice clip ${clip}: ${response.status}`)
    const buffer = await context!.decodeAudioData(await response.arrayBuffer())

    cache.set(clip, buffer)
    if (cache.size > CACHE_LIMIT) cache.delete(cache.keys().next().value!)
    return buffer
  }

  function playBuffer(buffer: AudioBuffer) {
    return new Promise<void>((resolve, reject) => {
      const source = context!.createBufferSource()
      source.buffer = buffer
      source.connect(gain!)
      // A suspended context never fires "ended"; without this the chain would stop for good.
      const watchdog = setTimeout(() => reject(new Error('audio did not finish')), buffer.duration * 1000 + END_GRACE_MS)
      source.onended = () => {
        clearTimeout(watchdog)
        resolve()
      }
      source.start()
    })
  }

  /** All clips load before the first sound, so a missing file never leaves half an announcement in the air. */
  function play(clips: string[]) {
    const run = chain.then(async () => {
      const buffers = await Promise.all(clips.map(load))
      for (const buffer of buffers) await playBuffer(buffer)
    })
    chain = run.catch(() => undefined)
    return run
  }

  return { unlock, isRunning, setVolume, play }
}

/** One player per tab, so a desk's announcement and the station's can never overlap. */
export const sharedPlayer = createClipPlayer()
