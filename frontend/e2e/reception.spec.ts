import { collectFee, ensureTariffs, openDesk, pick } from './helpers'
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
  await expect(page.getByRole('img', { name: 'تصویر خوش‌آمدگویی' })).toBeVisible()
  await createUser(page, receptionUser, password, 'پذیرش')

  const context = await browser.newContext()
  const reception = await context.newPage()
  reception.on('pageerror', (e) => pageErrors.push(e.message))
  await login(reception, receptionUser, password)

  // Role lands on its own workspace.
  await expect(reception).toHaveURL(/\/reception$/)
  await ensureTariffs(page)
  await openDesk(page, reception)
  await expect(reception.getByRole('link', { name: 'کاربران' })).toHaveCount(0)

  const firstName = `احمد${stamp}`
  const phone = `07${String(stamp).slice(-8)}`

  async function fillPatient() {
    await reception.getByLabel('نام', { exact: true }).fill(firstName)
    await reception.getByLabel('نام پدر').fill('محمود')
    await reception.getByLabel('یا سن تخمینی').fill('40')
    await reception.getByLabel('شماره تماس').fill(phone)
    await reception.getByRole('button', { name: 'ذخیره' }).click()
  }

  await reception.getByRole('link', { name: 'مریضان' }).click()
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await fillPatient()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await expect(reception.getByText('P-', { exact: false }).first()).toBeVisible()

  // Same person again: the system warns before creating a second record.
  await reception.goto('/patients')
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await fillPatient()
  await expect(reception.getByText('مریض مشابه پیدا شد')).toBeVisible()
  reception.once('dialog', (dialog) => dialog.accept())
  await reception.getByRole('link', { name: 'بازگشت به مریضان' }).click()
  await expect(reception).toHaveURL(/\/patients$/)

  // Header search finds the patient.
  await reception.getByLabel('جستجوی مریض در هدر').fill(firstName)
  await reception.getByLabel('جستجوی مریض در هدر').press('Enter')
  await expect(reception).toHaveURL(/\/patients\?q=/)
  await expect(reception.getByRole('row', { name: new RegExp(firstName) })).toHaveCount(1)

  // New visit from the patient record continues in the one reception flow, with this patient already chosen.
  await reception.getByRole('link', { name: new RegExp(firstName) }).first().click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  const recordUrl = reception.url()
  await reception.getByRole('link', { name: 'مراجعه جدید' }).click()
  await expect(reception).toHaveURL(/\/reception\?patient=\d+$/)
  await expect(reception.getByRole('heading', { name: 'پذیرش مریض' })).toBeVisible()
  await reception.getByLabel('علت مراجعه').fill('تاری دید')
  await reception.getByRole('button', { name: 'ثبت مراجعه' }).click()

  // Registered but unpaid: no number anywhere yet, and the visit waits in the desk's open payments.
  const owing = reception.getByRole('row', { name: new RegExp(`فیس معاینه.*${firstName}`) })
  await expect(owing).toBeVisible()
  await collectFee(reception)
  await expect(owing).toHaveCount(0)
  const number = (await reception.locator('.admission-done__number').innerText()).trim()
  await reception.getByRole('button', { name: 'مریض بعدی' }).click()

  // The same patient cannot be given a second visit or fee while this one is open.
  await pick(reception, reception, 'مریض', firstName)
  await expect(reception.getByRole('alert').filter({ hasText: 'این مریض امروز مراجعه فعال دارد' })).toContainText(number)
  await reception.getByRole('button', { name: 'مریض دیگر' }).click()

  await reception.goto(recordUrl)
  await expect(reception.getByRole('row', { name: /تاری دید/ })).toBeVisible()

  // Dates display in the Afghan month names.
  const dateCell = await reception.getByRole('row', { name: /تاری دید/ }).getByRole('cell').nth(1).innerText()
  expect(dateCell).toMatch(/حمل|ثور|جوزا|سرطان|اسد|سنبله|میزان|عقرب|قوس|جدی|دلو|حوت/)

  // Visit appears in the queue with its number and payment, and status moves directly to completed without any lab step.
  await reception.goto('/reception')
  await expect(reception.getByRole('heading', { name: 'مراجعات امروز' })).toBeVisible()
  await expect(reception.getByRole('heading', { name: 'وقت‌های ملاقات امروز' })).toBeVisible()
  const row = reception.getByRole('row', { name: new RegExp(firstName) })
  await expect(row).toContainText(number)
  await expect(row).toContainText('پرداخت‌شده')
  await expect(row).toContainText('در انتظار')
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
