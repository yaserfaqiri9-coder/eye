import { pick } from './helpers'
import { expect, test, type Browser, type Page } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function createUser(admin: Page, userName: string, roleLabel: string) {
  await admin.goto('/users')
  await admin.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = admin.getByRole('dialog')
  await dialog.getByLabel('نام کاربری').fill(userName)
  await dialog.getByLabel('نام کامل').fill(`کارمند ${roleLabel}`)
  await dialog.getByLabel(/رمز عبور/).fill(password)
  await dialog.getByLabel(roleLabel, { exact: true }).check()
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()
}

async function staffPage(browser: Browser, userName: string) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await login(page, userName, password)
  return { page, context }
}

async function registerVisit(reception: Page, firstName: string) {
  await reception.goto('/patients')
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await reception.getByLabel('نام', { exact: true }).fill(firstName)
  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await reception.getByRole('button', { name: 'مراجعه جدید' }).click()
  const vd = reception.getByRole('dialog')
  await pick(reception, vd, 'بخش', 'معاینه ابتدایی')
  await vd.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await expect(vd).toBeHidden()
}

test('a visit appears live in triage, and reconnecting after an outage recovers what was missed', async ({ page, browser }) => {
  test.setTimeout(180_000)
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await createUser(page, `rlr_${stamp}`, 'پذیرش')
  await createUser(page, `rlt_${stamp}`, 'معاینه ابتدایی')

  const { page: reception } = await staffPage(browser, `rlr_${stamp}`)
  const { page: triage, context: triageContext } = await staffPage(browser, `rlt_${stamp}`)
  await expect(triage).toHaveURL(/\/triage$/)
  await expect(triage.getByText('اتصال زنده قطع است')).toBeHidden()

  // Far quicker than the 60 second safety refresh, so only the live event can explain this.
  const first = `زنده${stamp}`
  await registerVisit(reception, first)
  await expect(triage.getByRole('row', { name: new RegExp(first) })).toBeVisible({ timeout: 8_000 })

  // Network drop: the user is told, and the queue catches up by itself after recovery.
  await triageContext.setOffline(true)
  await expect(triage.getByText('اتصال زنده قطع است')).toBeVisible({ timeout: 45_000 })

  const missed = `قطع${stamp}`
  await registerVisit(reception, missed)
  await expect(triage.getByRole('row', { name: new RegExp(missed) })).toHaveCount(0)

  await triageContext.setOffline(false)
  await expect(triage.getByRole('row', { name: new RegExp(missed) })).toBeVisible({ timeout: 45_000 })

  // The row can arrive first through the safety refresh, while the socket is still in reconnect backoff,
  // so the warning is allowed to outlast it. It must still clear by itself without a page reload.
  await expect(triage.getByText('اتصال زنده قطع است')).toBeHidden({ timeout: 45_000 })

  // Back on the live channel: a new visit arrives well before the next safety refresh could explain it.
  const after = `وصل${stamp}`
  await registerVisit(reception, after)
  await expect(triage.getByRole('row', { name: new RegExp(after) })).toBeVisible({ timeout: 8_000 })
})
