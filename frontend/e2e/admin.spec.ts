import { pick } from './helpers'
import { expect, test, type Page } from '@playwright/test'

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

test('a user changes their password from the header, and the admin can reset it and read the audit trail', async ({ page, browser }) => {
  test.setTimeout(90_000)
  const stamp = Date.now()
  const userName = `pwd_${stamp}`

  await login(page, adminUser, adminPassword)
  await createUser(page, userName, 'صندوق‌دار')

  const context = await browser.newContext()
  const staff = await context.newPage()
  await login(staff, userName, password)

  const changed = 'Changed-Password-456'
  await staff.getByRole('button', { name: 'تغییر رمز' }).click()
  const dialog = staff.getByRole('dialog')
  await dialog.getByLabel('رمز فعلی').fill(password)
  await dialog.getByLabel(/رمز جدید/).fill(changed)
  await dialog.getByRole('button', { name: 'تغییر رمز' }).click()
  await expect(dialog.getByRole('status')).toContainText('رمز شما تغییر کرد')
  await dialog.getByRole('button', { name: 'بستن' }).click()

  // Still signed in after changing it, and a state-changing call still passes the CSRF check.
  await staff.reload()
  await expect(staff).toHaveURL(/\/cashier$/)

  await page.goto('/users')
  await page.getByRole('searchbox', { name: 'جستجو', exact: true }).fill(userName)
  await page.getByRole('button', { name: 'جستجو', exact: true }).click()
  const row = page.getByRole('row', { name: new RegExp(userName) })
  await row.getByRole('button', { name: 'بازنشانی رمز' }).click()
  const resetDialog = page.getByRole('dialog')
  await resetDialog.getByLabel(/رمز جدید/).fill('Reset-By-Admin-789')
  await resetDialog.getByRole('button', { name: 'بازنشانی رمز' }).click()
  await expect(resetDialog).toBeHidden()

  await staff.reload()
  await expect(staff).toHaveURL(/\/login/)

  const fresh = await (await browser.newContext()).newPage()
  await login(fresh, userName, 'Reset-By-Admin-789')

  await page.getByRole('link', { name: 'ثبت فعالیت‌ها' }).click()
  await page.getByLabel('فیلتر فعالیت').selectOption({ label: 'بازنشانی رمز' })
  await expect(page.getByRole('row', { name: /بازنشانی رمز/ }).first()).toBeVisible()
})

test('lab uploads a result file, refuses a disguised one, and the doctor sees the empty queue dashboard', async ({ page, browser }) => {
  test.setTimeout(90_000)
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await createUser(page, `alb_${stamp}`, 'آزمایشگاه')
  await createUser(page, `arc_${stamp}`, 'پذیرش')
  await createUser(page, `adc_${stamp}`, 'داکتر چشم')

  await page.goto('/settings')
  await page.getByRole('tab', { name: 'فهرست آزمایش‌ها' }).click()
  await page.getByRole('button', { name: 'آزمایش جدید' }).click()
  const testName = `CT-${stamp}`
  await page.getByRole('dialog').getByLabel('نام آزمایش').fill(testName)
  await page.getByRole('dialog').getByRole('button', { name: 'ذخیره' }).click()
  await expect(page.getByRole('dialog')).toBeHidden()

  const reception = await (await browser.newContext()).newPage()
  await login(reception, `arc_${stamp}`, password)
  const firstName = `فایل${stamp}`
  await reception.goto('/patients')
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await reception.getByLabel('نام', { exact: true }).fill(firstName)
  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)

  const lab = await (await browser.newContext()).newPage()
  await login(lab, `alb_${stamp}`, password)
  await lab.getByRole('button', { name: 'درخواست مستقل' }).click()
  const order = lab.getByRole('dialog')
  await pick(lab, order, 'مریض', firstName)
  await pick(lab, order, 'آزمایش', testName)
  await order.getByRole('button', { name: 'ثبت درخواست' }).click()
  await expect(order).toBeHidden()

  await lab.getByRole('row', { name: new RegExp(testName) }).getByRole('link', { name: 'ثبت نتیجه' }).click()
  const result = lab.locator('form.xr-card')
  const input = result.getByLabel(/بارگذاری فایل/)

  await input.setInputFiles({ name: 'fake.pdf', mimeType: 'application/pdf', buffer: Buffer.from('<script>alert(1)</script>') })
  await expect(result.getByRole('alert')).toContainText('فقط فایل PDF')

  await input.setInputFiles({ name: 'report.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 test report') })
  await expect(result.getByRole('link', { name: 'report.pdf' })).toBeVisible()

  const doctor = await (await browser.newContext()).newPage()
  await login(doctor, `adc_${stamp}`, password)
  await expect(doctor.getByText('نتایج آماده')).toBeVisible()
  await expect(doctor.getByRole('alert')).toHaveCount(0)
})
