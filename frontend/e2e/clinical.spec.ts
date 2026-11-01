import { expect, test, type Browser, type Page } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
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

async function staffPage(browser: Browser, userName: string, errors: string[]) {
  const context = await browser.newContext()
  const page = await context.newPage()
  page.on('pageerror', (e) => errors.push(e.message))
  await login(page, userName, password)
  await expect(page).not.toHaveURL(/\/login/)
  return page
}

test('full clinical journey crosses departments without re-entering data', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  const stamp = Date.now()

  await login(page, adminUser, adminPassword)
  await expect(page.getByText('خوش آمدید')).toBeVisible()
  await createUser(page, `rec_${stamp}`, 'پذیرش')
  await createUser(page, `tri_${stamp}`, 'معاینه ابتدایی')
  await createUser(page, `doc_${stamp}`, 'داکتر چشم')
  await createUser(page, `lab_${stamp}`, 'آزمایشگاه')

  const testName = `OCT-${stamp}`
  const created = await page.evaluate(async (name) => {
    const token = decodeURIComponent(document.cookie.match(/XSRF-TOKEN=([^;]+)/)![1])
    const r = await fetch('/api/diagnostic-tests', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token }, body: JSON.stringify({ name, isActive: true }) })
    return r.status
  }, testName)
  expect(created).toBe(201)

  // Reception registers the patient and sends them to pre-exam.
  const reception = await staffPage(browser, `rec_${stamp}`, errors)
  const firstName = `کریم${stamp}`
  await reception.goto('/patients')
  await reception.getByRole('button', { name: 'مریض جدید' }).click()
  const pd = reception.getByRole('dialog')
  await pd.getByLabel('نام', { exact: true }).fill(firstName)
  await pd.getByLabel('یا سن تخمینی').fill('55')
  await pd.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await reception.getByRole('button', { name: 'مراجعه جدید' }).click()
  const vd = reception.getByRole('dialog')
  await vd.getByLabel('بخش').selectOption({ label: 'معاینه ابتدایی' })
  await vd.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await expect(vd).toBeHidden()

  // Triage lands on their queue, records values, sends to doctor.
  const triage = await staffPage(browser, `tri_${stamp}`, errors)
  await expect(triage).toHaveURL(/\/triage$/)
  await triage.getByRole('row', { name: new RegExp(firstName) }).getByRole('button', { name: 'ثبت معاینه' }).click()
  const td = triage.getByRole('dialog')
  await td.getByLabel(/VA OD/).fill('6/9')
  await td.getByLabel(/IOP OS/).fill('17')
  await td.getByRole('button', { name: 'ذخیره و ارسال به داکتر' }).click()
  await expect(td).toBeHidden()

  // Doctor sees it with the triage values, without retyping anything.
  const doctor = await staffPage(browser, `doc_${stamp}`, errors)
  await expect(doctor).toHaveURL(/\/doctor$/)
  const docRow = doctor.getByRole('row', { name: new RegExp(firstName) })
  await expect(docRow).toContainText('آماده برای داکتر')
  await docRow.getByRole('link', { name: 'باز کردن پرونده' }).click()
  await expect(doctor.getByText('6/9 / —')).toBeVisible()
  await expect(doctor.getByText('— / 17')).toBeVisible()

  await doctor.getByLabel(/یافته‌های چشم راست/).fill('یافته نمونه')
  await doctor.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(doctor.getByRole('status')).toContainText('ذخیره شد')

  // Lab order travels to the lab, and the result comes back to the chart.
  await doctor.getByRole('tab', { name: 'آزمایش' }).click()
  await doctor.getByLabel('آزمایش', { exact: true }).selectOption({ label: testName })
  await doctor.getByRole('button', { name: 'درخواست' }).click()
  await expect(doctor.getByRole('row', { name: new RegExp(testName) })).toContainText('درخواست‌شده')

  const lab = await staffPage(browser, `lab_${stamp}`, errors)
  await expect(lab).toHaveURL(/\/lab$/)
  const labRow = lab.getByRole('row', { name: new RegExp(testName) })
  await labRow.getByRole('button', { name: 'ثبت نتیجه' }).click()
  await lab.getByRole('dialog').getByLabel('نتیجه').fill('نتیجه نمونه آزمایش')
  await lab.getByRole('dialog').getByRole('button', { name: 'ثبت نتیجه' }).click()
  await expect(labRow).toContainText('نتیجه آماده')

  await doctor.reload()
  await doctor.getByRole('tab', { name: 'آزمایش' }).click()
  await expect(doctor.getByRole('row', { name: new RegExp(testName) })).toContainText('نتیجه نمونه آزمایش')

  // Finalise: direct edit closes, amendment stays available.
  await doctor.getByRole('tab', { name: 'معاینه' }).click()
  await doctor.getByRole('button', { name: 'نهایی‌سازی و تکمیل مراجعه' }).click()
  await expect(doctor.getByRole('status')).toContainText('نهایی')
  await expect(doctor.getByLabel(/یافته‌های چشم راست/)).toBeDisabled()
  await expect(doctor.getByRole('button', { name: 'ثبت اصلاحیه' })).toBeVisible()

  // Reception still has no way into the clinical record.
  await reception.goto(doctor.url().replace(/^https?:\/\/[^/]+/, ''))
  await expect(reception).not.toHaveURL(/\/chart\//)

  expect(errors).toEqual([])
})
