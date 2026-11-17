import { expect, test, type Page } from '@playwright/test'

const BASE = '/audio/recorded-c98dd13ed170/WAV/'
const pair = (n: number, destination: string) => [`${BASE}numbers/number_${String(n).padStart(3, '0')}.wav`, `${BASE}destinations/destination_${destination}.wav`]

async function isolated(page: Page) {
  // No test request reaches the running hospital API, including auth and background hub traffic.
  await page.route(url => url.pathname.startsWith('/api/'), route => route.fulfill({ status: 401, json: { message: 'isolated audio test' } }))
  await page.route('**/hubs/**', route => route.abort())
  await page.addInitScript(() => {
    const output: { start: number; end?: number; duration: number; rate: number }[] = []
    ;(window as any).__heard = output
    const start = AudioBufferSourceNode.prototype.start
    AudioBufferSourceNode.prototype.start = function (...args: Parameters<typeof start>) {
      const row = { start: this.context.currentTime, duration: this.buffer!.duration, rate: this.buffer!.sampleRate }
      output.push(row)
      this.addEventListener('ended', () => { (row as any).end = this.context.currentTime })
      return start.apply(this, args)
    }
  })
  await page.goto('/login')
  await page.evaluate(async () => {
    const m = await import('/src/features/calls/clipPlayer.ts')
    ;(window as any).__module = m
    ;(window as any).__player = m.createClipPlayer()
  })
  // An actual click unlocks the real AudioContext.
  await page.getByRole('heading').first().evaluate(el => el.addEventListener('click', () => { void (window as any).__player.unlock() }))
  await page.getByRole('heading').first().click()
  await expect.poll(() => page.evaluate(() => (window as any).__player.isRunning())).toBe(true)
}

for (const [n, dest] of [[1, 'doctor'], [25, 'refraction'], [50, 'pharmacy'], [1, 'imaging'], [25, 'optical'], [50, 'initial_exam']] as const) {
  test(`real recording ${n} to ${dest} plays as one unmodified 48k buffer`, async ({ page }) => {
    await isolated(page)
    const result = await page.evaluate(async ({ clips, id }) => {
      const m = (window as any).__module
      const ctx = new OfflineAudioContext(1, 48000, 48000)
      const buffers = await Promise.all(clips.map(async path => ctx.decodeAudioData(await (await fetch(path)).arrayBuffer())))
      const joined = m.joinBuffers(ctx, buffers)
      const gap = Math.round(48000 * m.JOIN_GAP_MS / 1000)
      let exact = true
      for (let i = 0; i < buffers[0].length; i++) if (joined.getChannelData(0)[i] !== buffers[0].getChannelData(0)[i]) exact = false
      const offset = buffers[0].length + gap
      for (let i = 0; i < buffers[1].length; i++) if (joined.getChannelData(0)[offset+i] !== buffers[1].getChannelData(0)[i]) exact = false
      const silence = joined.getChannelData(0).slice(buffers[0].length, offset).every(x => x === 0)
      await (window as any).__player.play(clips, id)
      return { exact, silence, duration: joined.duration, expected: buffers[0].duration + buffers[1].duration + gap/48000, heard: (window as any).__heard }
    }, { clips: pair(n, dest), id: `test-${n}-${dest}` })
    expect(result.exact).toBe(true)
    expect(result.silence).toBe(true)
    expect(result.duration).toBeCloseTo(result.expected, 6)
    expect(result.heard).toHaveLength(1)
    expect(result.heard[0].rate).toBe(48000)
  })
}

test('rapid queue snapshots destinations; duplicate event plays once, intentional recall plays again', async ({ page }) => {
  await isolated(page)
  const result = await page.evaluate(async ({ first, second, third }) => {
    const player = (window as any).__player
    const a = player.play(first, 1001)
    const duplicate = player.play(first, 1001)
    const b = player.play(second, 1002)
    second[1] = third[1] // A later page selection cannot mutate the queued destination.
    const c = player.play(third, 1003)
    await Promise.all([a, duplicate, b, c])
    await player.play(first, 1001) // Re-delivery after completion remains silent.
    await player.play(first, 1004) // Recall: new event ID, same recorded words.
    return (window as any).__heard
  }, { first: pair(12, 'doctor'), second: pair(12, 'refraction'), third: pair(3, 'pharmacy') })
  expect(result).toHaveLength(4)
  for (let i=1;i<result.length;i++) expect(result[i].start).toBeGreaterThanOrEqual(result[i-1].end - 0.03)
  const expectedDuration = await page.evaluate(async clips => {
    const ctx = new OfflineAudioContext(1,48000,48000)
    const buffers = await Promise.all(clips.map(async p => ctx.decodeAudioData(await (await fetch(p)).arrayBuffer())))
    return buffers[0].duration+buffers[1].duration+0.03
  }, pair(12,'refraction'))
  expect(result[1].duration).toBeCloseTo(expectedDuration,5)
})

test('missing file plays no partial phrase, retry recovers and a failure cannot poison the queue', async ({ page }) => {
  await isolated(page)
  const missing=pair(25,'surgery')[1]!
  await page.route(`**${missing}`, route=>route.fulfill({status:404,body:'missing'}))
  const error=await page.evaluate(async clips=>{
    try { await (window as any).__player.play(clips,2001); return '' } catch(e) {return String(e)}
  },pair(25,'surgery'))
  expect(error).toContain('بارگذاری نشد')
  expect(await page.evaluate(()=>(window as any).__heard.length)).toBe(0)
  await page.unroute(`**${missing}`)
  await page.evaluate(async clips=>(window as any).__player.play(clips,2001),pair(25,'surgery'))
  expect(await page.evaluate(()=>(window as any).__heard.length)).toBe(1)
  const unsupported=await page.evaluate(async()=>{try{await(window as any).__player.play([],2002);return''}catch(e){return String(e)}})
  expect(unsupported).toContain('شماره یا مقصد')
  await page.evaluate(async clips=>(window as any).__player.play(clips,2003),pair(1,'cashier'))
  expect(await page.evaluate(()=>(window as any).__heard.length)).toBe(2)
})

test('all 59 announcement clips prepare locally and a fresh player decodes cached audio with network fully offline',async({page,context})=>{
  await isolated(page)
  await page.evaluate(()=>(window as any).__player.prepare())
  await context.setOffline(true)
  try {
    const result=await page.evaluate(async clips=>{
      const m=(window as any).__module
      const player=m.createClipPlayer()
      await player.unlock()
      await player.prepare()
      await player.play(clips,3001)
      return (window as any).__heard
    },pair(50,'imaging'))
    expect(result).toHaveLength(1)
  } finally {await context.setOffline(false)}
})

test('completed event deduplication survives a player recreation in the same tab',async({page})=>{
  await isolated(page)
  await page.evaluate(async clips=>{
    await(window as any).__player.play(clips,4001)
    const player=(window as any).__module.createClipPlayer();await player.unlock();await player.play(clips,4001)
  },pair(25,'reception'))
  expect(await page.evaluate(()=>(window as any).__heard.length)).toBe(1)
})
