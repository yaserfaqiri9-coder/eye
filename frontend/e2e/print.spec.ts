import { admitFromRecord, ensureTariffs, openDesk } from './helpers'
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

test('admin configures the hospital, a charged visit prints a slip and an A4 invoice renders to PDF', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const stamp = Date.now()
  const hospitalName = `شفاخانه ${stamp}`
  const serviceName = `معاینه${stamp}`

  await login(page, adminUser, adminPassword)
  await page.goto('/settings')
  await page.getByLabel('نام شفاخانه').fill(hospitalName)
  await page.getByLabel('پاورقی چاپ').fill('پاورقی آزمایشی')
  await page.getByRole('button', { name: 'ذخیره' }).click()
  await expect(page.getByRole('status')).toContainText('ذخیره شد')

  await page.getByRole('tab', { name: 'خدمات و تعرفه‌ها' }).click()
  await page.getByRole('button', { name: 'خدمت جدید' }).click()
  const sd = page.getByRole('dialog')
  await sd.getByLabel('کد').fill(`S${stamp}`)
  await sd.getByLabel('نام خدمت').fill(serviceName)
  await sd.getByLabel('قیمت').fill('750')
  await sd.getByRole('button', { name: 'ذخیره' }).click()
  await expect(sd).toBeHidden()
  await expect(page.getByRole('row', { name: new RegExp(serviceName) })).toContainText('۷۵۰')

  await createUser(page, `prr_${stamp}`, 'پذیرش')
  await createUser(page, `prc_${stamp}`, 'صندوق‌دار')

  // Reception charges the service while creating the visit.
  const receptionContext = await browser.newContext()
  const reception = await receptionContext.newPage()
  await login(reception, `prr_${stamp}`, password)
  await ensureTariffs(page)
  await openDesk(page, reception)
  const firstName = `چاپ${stamp}`
  await reception.goto('/patients')
  await reception.getByRole('link', { name: 'مریض جدید' }).click()
  await reception.getByLabel('نام', { exact: true }).fill(firstName)
  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  await admitFromRecord(reception, 'داکتر', serviceName)

  // Admission slip carries hospital details and the queue, in Dari, with no controls in print mode.
  await reception.goto('/reception')
  await reception.getByRole('tab', { name: /در جریان/ }).click()
  const row = reception.getByRole('row', { name: new RegExp(firstName) })
  const [slip] = await Promise.all([reception.context().waitForEvent('page'), row.getByRole('link', { name: 'رسید' }).click()])
  await expect(slip.getByRole('heading', { name: 'رسید پذیرش' })).toBeVisible()
  await expect(slip.getByText(hospitalName)).toBeVisible()
  await expect(slip.getByText(firstName)).toBeVisible()
  await expect(slip.getByText('پاورقی آزمایشی')).toBeVisible()
  await slip.emulateMedia({ media: 'print' })
  await expect(slip.getByRole('button', { name: 'چاپ' })).toBeHidden()

  // Cashier sees the charge and its invoice prints correctly to A4 PDF.
  const cashierContext = await browser.newContext()
  const cashier = await cashierContext.newPage()
  await login(cashier, `prc_${stamp}`, password)
  await cashier.getByLabel('جستجوی فاکتور').fill(firstName)
  const invoiceRow = cashier.getByRole('row', { name: new RegExp(firstName) })
  await expect(invoiceRow).toContainText('۷۵۰')
  const [invoicePage] = await Promise.all([cashier.context().waitForEvent('page'), invoiceRow.getByRole('link', { name: 'فاکتور' }).click()])
  await expect(invoicePage.getByRole('heading', { name: 'فاکتور خدمات' })).toBeVisible()
  await expect(invoicePage.getByRole('row', { name: new RegExp(serviceName) })).toBeVisible()
  await invoicePage.emulateMedia({ media: 'print' })
  const pdf = await invoicePage.pdf({ format: 'A4' })
  expect(pdf.subarray(0, 4).toString()).toBe('%PDF')
  expect(pdf.length).toBeGreaterThan(5000)

  // A cashier has no right to print a patient card, and the page says so instead of showing it.
  const denied = await cashier.context().newPage()
  await denied.goto('/print/patient/1')
  await expect(denied.getByRole('alert')).toContainText('اجازه')
})

test('reports and dashboard load for the manager and show no error', async ({ page, browser }) => {
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await createUser(page, `mgr_${stamp}`, 'مدیر شفاخانه')

  const context = await browser.newContext()
  const manager = await context.newPage()
  await login(manager, `mgr_${stamp}`, password)
  await expect(manager.getByText('دریافت امروز')).toBeVisible()
  await expect(manager.getByText('باقی حساب‌ها')).toBeVisible()
  await expect(manager.getByRole('alert')).toHaveCount(0)

  await manager.getByRole('link', { name: 'گزارش‌ها' }).click()
  await expect(manager.getByRole('heading', { name: 'خلاصه کار', level: 1 })).toBeVisible()
  await expect(manager.getByText('پول دریافت‌شده')).toBeVisible()
  await expect(manager.getByRole('alert')).toHaveCount(0)
})
