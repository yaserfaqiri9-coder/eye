import { mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { call, ensureTariffs, openDesk, paidToken } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
// The API's Storage:Path plus "voice".
const voiceDir = process.env.ALSHIFA_E2E_VOICE_DIR!
const password = 'E2e-Password-123'

/** Half a second of real, decodable MPEG audio. The test measures the player's timing, not how anything sounds. */
const silence = Buffer.concat(Array.from({ length: 20 }, () => Buffer.concat([Buffer.from([0xff, 0xfb, 0x90, 0x00]), Buffer.alloc(413)])))

interface Spied {
  started: number[]
  ended: number[]
  duration: number[]
}

/** Records every sound the page really sends to the audio output. */
async function spyOnAudio(page: Page) {
  await page.addInitScript(() => {
    const log = { started: [] as number[], ended: [] as number[], duration: [] as number[] }
    ;(window as unknown as { __audio: typeof log }).__audio = log
    const start = AudioBufferSourceNode.prototype.start
    AudioBufferSourceNode.prototype.start = function (...args: Parameters<typeof start>) {
      log.started.push(this.context.currentTime)
      log.duration.push(this.buffer!.duration)
      this.addEventListener('ended', () => log.ended.push(this.context.currentTime))
      return start.apply(this, args)
    }
  })
}

const audio = (page: Page) => page.evaluate(() => (window as unknown as { __audio: Spied }).__audio)

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function staff(browser: Browser, admin: Page, role: string, listen = false) {
  const userName = `${role.toLowerCase()}_${Date.now()}`
  await call(admin, 'POST', '/users', { userName, fullName: userName, password, roles: [role] })
  const context = await browser.newContext()
  const page = await context.newPage()
  if (listen) await spyOnAudio(page)
  await login(page, userName, password)
  return { page, context }
}

async function newVisit(reception: Page, departmentId: number) {
  const tokenId = await paidToken(reception, departmentId)
  const patient = await call<{ id: number }>(reception, 'POST', '/patients', { firstName: `فراخوان${crypto.randomUUID().slice(0, 8)}`, gender: 'Male', estimatedAge: 30 })
  return call<{ id: number; queueNumber: number }>(reception, 'POST', '/visits', { patientId: patient.id, departmentId, tokenId })
}

test('with no station online, a call plays on the computer that pressed the button', async ({ page, browser }) => {
  test.setTimeout(120_000)
  mkdirSync(voiceDir, { recursive: true })
  for (const clip of ['to-vision.mp3', 'again-vision.mp3']) writeFileSync(join(voiceDir, clip), silence)

  await login(page, adminUser, adminPassword)
  await ensureTariffs(page)
  const reception = await staff(browser, page, 'Receptionist')
  await openDesk(page, reception.page)
  const departments = await call<{ id: number; name: string }[]>(reception.page, 'GET', '/lookups/departments')
  const visit = await newVisit(reception.page, departments.find((d) => d.name === 'معاینه ابتدایی')!.id)
  writeFileSync(join(voiceDir, `number-${String(visit.queueNumber).padStart(4, '0')}.mp3`), silence)

  const nurse = await staff(browser, page, 'Triage', true)
  await nurse.page.goto('/triage')
  await expect(nurse.page.getByRole('status').filter({ hasText: 'فراخوان از بلندگوی همین رایانه پخش می‌شود' })).toBeVisible()

  const number = visit.queueNumber.toLocaleString('fa-AF', { useGrouping: false })
  await nurse.page.getByRole('button', { name: `فراخوان نوبت ${number}`, exact: true }).click()
  await expect.poll(async () => (await audio(nurse.page)).ended.length, { timeout: 30_000 }).toBe(2)
  await expect(nurse.page.getByRole('row').filter({ hasText: number }).getByText('اعلان شد')).toBeVisible({ timeout: 15_000 })

  await reception.context.close()
  await nurse.context.close()
})

test('calls play once, one at a time, survive a network outage without repeats, and report a missing file', async ({ page, browser }) => {
  test.setTimeout(180_000)
  mkdirSync(voiceDir, { recursive: true })
  for (const clip of ['to-vision.mp3', 'again-vision.mp3']) writeFileSync(join(voiceDir, clip), silence)

  await spyOnAudio(page)
  await login(page, adminUser, adminPassword)
  await ensureTariffs(page)

  const reception = await staff(browser, page, 'Receptionist')
  await openDesk(page, reception.page)
  const departments = await call<{ id: number; name: string }[]>(reception.page, 'GET', '/lookups/departments')
  const triageDepartment = departments.find((d) => d.name === 'معاینه ابتدایی')!.id
  const first = await newVisit(reception.page, triageDepartment)
  const second = await newVisit(reception.page, triageDepartment)
  const third = await newVisit(reception.page, triageDepartment)
  expect(new Set([first.queueNumber, second.queueNumber, third.queueNumber]).size).toBe(3)
  for (const visit of [first, second, third]) writeFileSync(join(voiceDir, `number-${String(visit.queueNumber).padStart(4, '0')}.mp3`), silence)

  const nurse = await staff(browser, page, 'Triage')
  await nurse.page.goto('/triage')

  // Without an active station the desk is told plainly, and nothing is queued for later.
  await page.goto('/audio-station')
  await expect(page.getByText('غیرفعال')).toBeVisible()
  const faNumber = (visit: { queueNumber: number }) => visit.queueNumber.toLocaleString('fa-AF', { useGrouping: false })
  const callButton = (visit: { queueNumber: number }, label = 'فراخوان') => nurse.page.getByRole('button', { name: `${label} نوبت ${faNumber(visit)}`, exact: true })
  const nurseRow = (visit: { queueNumber: number }) => nurse.page.getByRole('row').filter({ has: nurse.page.getByRole('group', { name: `فراخوان نوبت ${faNumber(visit)}`, exact: true }) })

  // The dedicated role reaches the station page and nothing about patients.
  const speaker = await staff(browser, page, 'AudioStation')
  await speaker.page.goto('/audio-station')
  await expect(speaker.page.getByRole('heading', { name: 'مرکز پخش صوت' })).toBeVisible()
  await expect(speaker.page.getByRole('link', { name: 'مریضان' })).toHaveCount(0)
  await expect(speaker.page.getByRole('alert').filter({ hasText: 'اجازه' })).toHaveCount(0)
  await speaker.context.close()

  // Another computer may still count as the active station, and taking over has to be a deliberate second click.
  await page.getByRole('button', { name: 'فعال‌سازی صدا در این رایانه' }).click()
  const active = page.getByText('فعال و آماده پخش')
  const takeOver = page.getByRole('button', { name: 'این رایانه مرکز پخش شود' })
  await expect(active.or(takeOver)).toBeVisible()
  if (await takeOver.isVisible()) await takeOver.click()
  await expect(active).toBeVisible()

  // Navigating inside the app must keep the unlocked audio context and its heartbeat alive.
  await page.getByRole('link', { name: 'صفحه اصلی', exact: true }).click()
  await expect(page).not.toHaveURL(/audio-station/)
  await page.getByRole('link', { name: 'مرکز پخش صوت', exact: true }).click()
  await expect(active).toBeVisible()
  await expect(page.getByRole('button', { name: 'فعال‌سازی صدا در این رایانه', exact: true })).toHaveCount(0)
  // Desks opened before activation must clear the offline notice without a reload or a patient event.
  await expect(nurse.page.getByRole('status').filter({ hasText: 'مرکز پخش صوت فعال نیست' })).toHaveCount(0, { timeout: 20_000 })

  // Two desks pressing at nearly the same moment: each is heard whole, one after the other.
  const colleague = await staff(browser, page, 'Triage')
  await colleague.page.goto('/triage')
  const colleagueButton = colleague.page.getByRole('button', { name: `فراخوان نوبت ${faNumber(second)}`, exact: true })
  await Promise.all([callButton(first).click(), colleagueButton.click()])
  await expect.poll(async () => (await audio(page)).ended.length, { timeout: 30_000 }).toBe(4)
  const heard = await audio(page)
  for (let i = 1; i < heard.started.length; i++) {
    expect(heard.started[i]).toBeGreaterThanOrEqual(heard.ended[i - 1] - 0.05)
  }
  for (const visit of [first, second]) await expect(nurseRow(visit).getByText('اعلان شد')).toBeVisible({ timeout: 15_000 })

  // A reload is not a reason to repeat anything. This computer takes the station back, at the first click where the browser requires one, and the speaker stays silent.
  await page.reload()
  await page.getByRole('heading', { name: 'مرکز پخش صوت' }).click()
  await expect(page.getByText('فعال و آماده پخش')).toBeVisible()
  await expect(page.getByRole('button', { name: 'فعال‌سازی صدا در این رایانه', exact: true })).toHaveCount(0)
  await page.waitForTimeout(6_000)
  expect((await audio(page)).started).toHaveLength(0)

  // The network drops at the station. A call made meanwhile plays once when it returns, and nothing else does.
  await page.context().setOffline(true)
  await expect(page.getByText('قطع')).toBeVisible({ timeout: 15_000 })
  await callButton(third).click()
  await expect(nurseRow(third).getByText('در نوبت پخش')).toBeVisible()
  await page.waitForTimeout(3_000)
  expect((await audio(page)).started).toHaveLength(0)
  await page.context().setOffline(false)
  await expect.poll(async () => (await audio(page)).ended.length, { timeout: 30_000 }).toBe(2)
  await page.waitForTimeout(6_000)
  expect((await audio(page)).started).toHaveLength(2)

  // Recall is a person's decision and uses its own announcement.
  await callButton(first, 'فراخوان مجدد').click()
  await expect.poll(async () => (await audio(page)).ended.length, { timeout: 30_000 }).toBe(4)

  // A missing file stops the call at the desk, with the file named, and nothing goes out half-said.
  rmSync(join(voiceDir, 'again-vision.mp3'))
  await callButton(first, 'فراخوان مجدد').click()
  await expect(nurse.page.getByRole('alert').filter({ hasText: 'فایل صوتی این اعلان موجود نیست' })).toBeVisible()
  expect((await audio(page)).started).toHaveLength(4)

  // Attendance is recorded on the row and does not move the patient.
  await nurse.page.getByRole('button', { name: `گزینه‌های بیشتر نوبت ${faNumber(first)}`, exact: true }).click()
  await nurse.page.getByRole('menuitem', { name: `نوبت ${faNumber(first)} حاضر شد` }).click()
  await expect(nurseRow(first).getByText('حاضر شد', { exact: true })).toBeVisible()
  await expect(nurseRow(first).getByText('ثبت‌شده')).toBeVisible()

  await reception.context.close()
  await nurse.context.close()
  await colleague.context.close()
})
