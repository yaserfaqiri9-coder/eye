import { expect, test, type Page } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!

async function login(page: Page, user: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(password)
  await page.getByRole('button', { name: 'ورود' }).click()
}

async function createUser(page: Page, userName: string, password: string, roleLabel: string) {
  await page.goto('/users')
  await page.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('نام کاربری').fill(userName)
  await dialog.getByLabel('نام کامل').fill(`کارمند ${roleLabel}`)
  await dialog.getByLabel(/رمز عبور/).fill(password)
  await dialog.getByLabel(roleLabel, { exact: true }).check()
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()
}

test('receptionist registers a patient, duplicate is caught, visit reaches the queue and completes', async ({ page, browser }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (e) => pageErrors.push(e.message))

  const stamp = Date.now()
  const password = 'E2e-Password-123'
  const receptionUser = `rec_${stamp}`

  await login(page, adminUser, adminPassword)
  await expect(page.getByText('خوش آمدید')).toBeVisible()
  await createUser(page, receptionUser, password, 'پذیرش')

  const context = await browser.newContext()
  const reception = await context.newPage()
  reception.on('pageerror', (e) => pageErrors.push(e.message))
  await login(reception, receptionUser, password)

  // Role lands on its own workspace.
  await expect(reception).toHaveURL(/\/reception$/)
  await expect(reception.getByRole('link', { name: 'کاربران' })).toHaveCount(0)

  const firstName = `احمد${stamp}`
  const phone = `07${String(stamp).slice(-8)}`

  async function fillPatient() {
    const dialog = reception.getByRole('dialog')
    await dialog.getByLabel('نام', { exact: true }).fill(firstName)
    await dialog.getByLabel('نام پدر').fill('محمود')
    await dialog.getByLabel('یا سن تخمینی').fill('40')
    await dialog.getByLabel('شماره تماس').fill(phone)
    await dialog.getByRole('button', { name: 'ذخیره' }).click()
  }

  await reception.getByRole('link', { name: 'مریضان' }).click()
  await reception.getByRole('button', { name: 'مریض جدید' }).click()
  await fillPatient()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await expect(reception.getByText('P-', { exact: false }).first()).toBeVisible()

  // Same person again: the system warns before creating a second record.
  await reception.goto('/patients')
  await reception.getByRole('button', { name: 'مریض جدید' }).click()
  await fillPatient()
  await expect(reception.getByText('مریض مشابه پیدا شد')).toBeVisible()
  await reception.getByRole('dialog').getByRole('button', { name: 'بستن' }).click()

  // Header search finds the patient.
  await reception.getByLabel('جستجوی مریض در هدر').fill(firstName)
  await reception.getByLabel('جستجوی مریض در هدر').press('Enter')
  await expect(reception).toHaveURL(/\/patients\?q=/)
  await expect(reception.getByRole('row', { name: new RegExp(firstName) })).toHaveCount(1)

  // New visit from the patient record.
  await reception.getByRole('link', { name: new RegExp(firstName) }).first().click()
  await reception.getByRole('button', { name: 'مراجعه جدید' }).click()
  const visitDialog = reception.getByRole('dialog')
  await visitDialog.getByLabel('بخش').selectOption({ label: 'معاینه ابتدایی' })
  await visitDialog.getByLabel('علت مراجعه').fill('تاری دید')
  await visitDialog.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await expect(visitDialog).toBeHidden()
  await expect(reception.getByRole('row', { name: /تاری دید/ })).toBeVisible()

  // Dates display in the Afghan month names.
  const dateCell = await reception.getByRole('row', { name: /تاری دید/ }).getByRole('cell').nth(1).innerText()
  expect(dateCell).toMatch(/حمل|ثور|جوزا|سرطان|اسد|سنبله|میزان|عقرب|قوس|جدی|دلو|حوت/)

  // Visit appears in the queue, and status moves directly to completed without any lab step.
  await reception.goto('/reception')
  const row = reception.getByRole('row', { name: new RegExp(firstName) })
  await expect(row).toContainText('ثبت‌شده')
  await row.getByRole('combobox').selectOption({ label: 'تکمیل' })
  await expect(row).toContainText('تکمیل')

  await context.close()
  expect(pageErrors).toEqual([])
})

test('Shamsi conversion matches the browser calendar', async ({ page }) => {
  await page.goto('/login')
  const result = await page.evaluate(async () => {
    const { shamsiToIso } = await import('/src/utils/date.ts')
    const format = new Intl.DateTimeFormat('en-u-ca-persian-nu-latn', { timeZone: 'UTC', year: 'numeric', month: 'numeric', day: 'numeric' })
    const failures: string[] = []
    for (const [y, m, d] of [[1405, 1, 1], [1405, 8, 11], [1403, 12, 30], [1404, 12, 29], [1360, 6, 31], [1450, 7, 1]]) {
      const iso = shamsiToIso(y, m, d)
      const parts = format.formatToParts(new Date(`${iso}T12:00:00Z`))
      const got = ['year', 'month', 'day'].map((t) => Number(parts.find((p) => p.type === t)!.value))
      if (got[0] !== y || got[1] !== m || got[2] !== d) failures.push(`${y}/${m}/${d} -> ${iso} -> ${got.join('/')}`)
    }
    return failures
  })
  expect(result).toEqual([])
})
