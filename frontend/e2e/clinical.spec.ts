import { call, collectFee, ensureTariffs, openDesk, pick } from './helpers'
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
  test.setTimeout(180_000)
  const errors: string[] = []
  const stamp = Date.now()

  await login(page, adminUser, adminPassword)
  await expect(page.getByRole('img', { name: 'تصویر خوش‌آمدگویی' })).toBeVisible()
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
  await ensureTariffs(page)
  await openDesk(page, reception)
  const firstName = `کریم${stamp}`
  await reception.goto('/patients')
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await reception.getByLabel('نام', { exact: true }).fill(firstName)
  await reception.getByLabel('یا سن تخمینی').fill('55')
  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await reception.getByRole('button', { name: 'مراجعه جدید' }).click()
  const vd = reception.getByRole('dialog')
  await pick(reception, vd, 'بخش', 'معاینه ابتدایی')
  await collectFee(vd)
  await vd.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await expect(vd).toBeHidden()

  // Triage lands on their queue, records values, sends to doctor.
  const triage = await staffPage(browser, `tri_${stamp}`, errors)
  await expect(triage).toHaveURL(/\/triage$/)
  await triage.getByRole('row', { name: new RegExp(firstName) }).getByRole('link', { name: 'ثبت معاینه' }).click()
  await expect(triage).toHaveURL(/\/triage\/\d+$/)
  const visitId = triage.url().match(/(\d+)$/)![1]
  await triage.getByLabel(/VA OD/).fill('۶/۹')
  await expect(triage.getByText('تغییرات ذخیره نشده')).toBeVisible()

  // Leaving with unsaved values asks first; declining keeps the form and what was typed.
  triage.once('dialog', (dialog) => dialog.dismiss())
  await triage.getByRole('link', { name: 'بینایی‌سنجی' }).click()
  await expect(triage).toHaveURL(/\/triage\/\d+$/)
  await expect(triage.getByLabel(/VA OD/)).toHaveValue('6/9')

  await triage.getByLabel(/PH OD/).fill('6/6')
  await triage.getByLabel(/IOP OS/).fill('17')
  await triage.getByLabel('سوابق ضروری').fill('حساسیت به پنسلین')
  await triage.getByRole('button', { name: 'ذخیره و ارسال به داکتر' }).click()
  await expect(triage).toHaveURL(/\/triage$/)

  // Vision reached the doctor, who asks for refraction; reception collects its own fee before it can start.
  const doctor = await staffPage(browser, `doc_${stamp}`, errors)
  await expect(doctor).toHaveURL(/\/doctor$/)
  const docRow = doctor.getByRole('row', { name: new RegExp(firstName) }).filter({ hasText: 'آماده برای داکتر' })
  await expect(docRow).toBeVisible()
  const visitRow = await call<{ patientId: number; patientFileNumber: string }>(doctor, 'GET', `/visits/${visitId}`)
  const tests = await call<{ id: number; kind: string }[]>(doctor, 'GET', '/diagnostic-tests')
  await call(doctor, 'POST', '/diagnostic-orders', { patientId: visitRow.patientId, visitId: Number(visitId), testId: tests.find((t) => t.kind === 'Refraction')!.id })
  await reception.goto('/reception')
  await reception.getByRole('row', { name: new RegExp(firstName) }).filter({ hasText: 'رفرکشن' }).getByRole('button', { name: 'دریافت فیس' }).click()
  await reception.getByRole('dialog').getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(reception.getByRole('dialog')).toBeHidden()

  // The optometrist records stages separately; completing sends the patient back to the same doctor.
  await triage.goto(`/refraction/${visitId}`)
  await triage.getByLabel('Auto Refraction: SPH OD').fill('-2.25')
  await triage.getByLabel('Final Refraction: SPH OD').fill('-2.0')
  await triage.getByLabel('Final Refraction: AXIS OD').fill('190')
  await expect(triage.getByRole('alert')).toContainText('محور')
  await triage.getByLabel('Final Refraction: AXIS OD').fill('90')
  await triage.getByLabel('Final Refraction: CYL OD').fill('0.5')
  await expect(triage.getByRole('alert')).toContainText('علامت')
  await triage.getByLabel('Final Refraction: CYL OD').fill('-0.5')
  await triage.getByLabel('Final Refraction: ADD OD').click()
  await expect(triage.getByLabel('Final Refraction: SPH OD')).toHaveValue('-2.00')
  await expect(triage.getByLabel('Final Refraction: CYL OD')).toHaveValue('-0.50')
  await expect(triage.getByLabel('Final Refraction: SPH OS')).not.toHaveAttribute('placeholder')
  await triage.getByLabel(/^PD/).fill('63')
  await triage.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(triage.getByText('ذخیره شد.')).toBeVisible()
  await triage.getByRole('button', { name: 'ذخیره و تکمیل' }).click()
  await expect(triage.getByText('رفرکشن تکمیل شد و مریض به صف داکتر برگشت.')).toBeVisible()

  // Doctor sees it with the triage values, without retyping anything.
  await doctor.reload()
  await expect(docRow).toBeVisible()
  await docRow.getByRole('link', { name: 'باز کردن پرونده' }).click()
  const summary = doctor.getByRole('region', { name: 'خلاصه پیش از معاینه' })
  const triageOd = summary.getByRole('table').first().getByRole('row').nth(1)
  await expect(triageOd).toContainText('6/9')
  await expect(triageOd).toContainText('6/6')
  await expect(summary.getByRole('table').first().getByRole('row').nth(2)).toContainText('17')
  await expect(summary).toContainText('حساسیت به پنسلین')
  await expect(summary.getByRole('table').nth(1).getByRole('row').nth(1)).toContainText('-2.00')

  // Nothing is recorded as a finding until the doctor enters it.
  await expect(doctor.getByLabel('قرنیه OD')).toHaveValue('')
  await doctor.getByLabel('شرح حال').fill('تاری دید از دو ماه')
  await doctor.getByLabel('قرنیه OD').fill('Clear')
  await doctor.getByLabel('عدسیه OS').fill('Nuclear sclerosis')
  await doctor.getByLabel(/یافته‌های چشم راست/).fill('یافته نمونه')
  await doctor.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(doctor.getByRole('status')).toContainText('ذخیره شد')

  // Structured values come back from the database in their own rows, with the free note kept apart.
  await doctor.reload()
  await expect(doctor.getByLabel('قرنیه OD')).toHaveValue('Clear')
  await expect(doctor.getByLabel('قرنیه OS')).toHaveValue('')
  await expect(doctor.getByLabel('عدسیه OS')).toHaveValue('Nuclear sclerosis')
  await expect(doctor.getByLabel('شرح حال')).toHaveValue('تاری دید از دو ماه')
  await expect(doctor.getByLabel(/یافته‌های چشم راست/)).toHaveValue('یافته نمونه')

  // The final refraction fills the glasses form only when asked, and is issued only on submit.
  await doctor.getByRole('tab', { name: 'نسخه عینک' }).click()
  await expect(doctor.getByLabel('نسخه عینک: SPH OD')).toHaveValue('')
  await doctor.getByRole('button', { name: 'انتقال از نسخه نهایی بینایی‌سنجی' }).click()
  await expect(doctor.getByLabel('نسخه عینک: SPH OD')).toHaveValue('-2.00')
  await expect(doctor.getByLabel('PD', { exact: true })).toHaveValue('63')
  await expect(doctor.getByText('نسخه عینک ثبت نشده است.')).toBeVisible()

  // Lab order travels to the lab, and the result comes back to the chart.
  await doctor.getByRole('tab', { name: 'آزمایش' }).click()
  await pick(doctor, doctor.locator('body'), 'درخواست', testName)
  await doctor.getByRole('button', { name: 'درخواست' }).click()
  await expect(doctor.getByRole('row', { name: new RegExp(testName) })).toContainText('درخواست‌شده')

  const lab = await staffPage(browser, `lab_${stamp}`, errors)
  await expect(lab).toHaveURL(/\/lab$/)
  const labRow = lab.getByRole('row', { name: new RegExp(testName) })
  await labRow.getByRole('link', { name: 'ثبت نتیجه' }).click()
  await lab.getByLabel('نتیجه', { exact: true }).fill('نتیجه نمونه آزمایش')
  await lab.getByRole('button', { name: 'ثبت نتیجه' }).click()
  await expect(lab).toHaveURL(/\/lab$/)
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
