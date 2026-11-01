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

async function staffPage(browser: Browser, userName: string, errors: string[]) {
  const context = await browser.newContext()
  const page = await context.newPage()
  page.on('pageerror', (e) => errors.push(e.message))
  await login(page, userName, password)
  return page
}

test('stock is bought, sold at the pharmacy, paid at the cashier and reconciled by accounting', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  const stamp = Date.now()

  await login(page, adminUser, adminPassword)
  await createUser(page, `inv_${stamp}`, 'مسئول گدام')
  await createUser(page, `pha_${stamp}`, 'دواخانه')
  await createUser(page, `cas_${stamp}`, 'صندوق‌دار')
  await createUser(page, `acc_${stamp}`, 'حسابدار')

  const drug = `قطره${stamp}`
  const inventory = await staffPage(browser, `inv_${stamp}`, errors)
  await expect(inventory).toHaveURL(/\/inventory$/)
  await inventory.getByRole('button', { name: 'جنس جدید' }).click()
  const pd = inventory.getByRole('dialog')
  await pd.getByLabel('کد').fill(`D${stamp}`)
  await pd.getByLabel('نام جنس').fill(drug)
  await pd.getByLabel('قیمت فروش').fill('250')
  await pd.getByRole('button', { name: 'ذخیره' }).click()
  await expect(pd).toBeHidden()

  await inventory.getByRole('button', { name: 'ثبت خرید' }).click()
  const bd = inventory.getByRole('dialog')
  await bd.getByLabel('جنس', { exact: true }).selectOption({ label: drug })
  await bd.getByLabel('محل ورود').selectOption({ label: 'دواخانه' })
  await bd.getByLabel('مقدار').fill('5')
  await bd.getByLabel('قیمت خرید واحد').fill('100')
  await bd.getByRole('button', { name: 'ثبت خرید' }).click()
  await expect(bd).toBeHidden()
  await expect(inventory.getByRole('row', { name: new RegExp(drug) })).toContainText('۵')

  const pharmacy = await staffPage(browser, `pha_${stamp}`, errors)
  await expect(pharmacy).toHaveURL(/\/pharmacy$/)
  await pharmacy.getByRole('button', { name: 'فروش مستقل' }).click()
  const sd = pharmacy.getByRole('dialog')
  await sd.getByLabel('جنس', { exact: true }).selectOption({ index: 1 })
  const option = await sd.getByLabel('جنس', { exact: true }).locator('option', { hasText: drug }).getAttribute('value')
  await sd.getByLabel('جنس', { exact: true }).selectOption(option!)
  await sd.getByLabel('مقدار').fill('2')
  await expect(sd).toContainText('۵۰۰')
  await sd.getByRole('button', { name: 'ثبت فروش' }).click()
  await expect(sd.getByRole('status')).toContainText('در صندوق قابل پرداخت است')
  const invoiceNumber = (await sd.getByRole('status').locator('.ltr-num').nth(1).innerText()).trim()
  await sd.getByRole('button', { name: 'بستن' }).click()

  // Requesting more than is left is refused with a clear message.
  await pharmacy.getByRole('button', { name: 'فروش مستقل' }).click()
  const over = pharmacy.getByRole('dialog')
  await expect(over.getByLabel('جنس', { exact: true }).locator('option', { hasText: drug })).toHaveCount(1)
  await over.getByLabel('جنس', { exact: true }).selectOption(option!)
  await over.getByLabel('مقدار').fill('4')
  await over.getByRole('button', { name: 'ثبت فروش' }).click()
  await expect(over.getByRole('alert')).toContainText('موجودی این جنس کافی نیست')
  await over.getByRole('button', { name: 'بستن' }).click()

  // Cashier has to open a session first, then takes a partial payment.
  const cashier = await staffPage(browser, `cas_${stamp}`, errors)
  await expect(cashier).toHaveURL(/\/cashier$/)
  const openButton = cashier.getByRole('button', { name: 'باز کردن صندوق' })
  await expect(openButton).toBeVisible()
  await openButton.click()
  await expect(cashier.getByRole('button', { name: 'بستن صندوق' })).toBeVisible()

  await cashier.getByLabel('جستجوی فاکتور').fill(invoiceNumber)
  const row = cashier.getByRole('row', { name: new RegExp(invoiceNumber) })
  await expect(row).toContainText('۵۰۰')
  await row.getByRole('button', { name: 'دریافت' }).click()
  const pay = cashier.getByRole('dialog')
  await pay.getByLabel('مبلغ').fill('200')
  await pay.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(pay.getByRole('status')).toContainText('شماره رسید')
  await pay.getByRole('button', { name: 'بستن' }).click()
  await expect(row).toContainText('پرداخت ناقص')
  await expect(row).toContainText('۳۰۰')

  // Accountant sees the ledger reconciled and can see the same invoice.
  const accounting = await staffPage(browser, `acc_${stamp}`, errors)
  await expect(accounting).toHaveURL(/\/accounting$/)
  await expect(accounting.getByRole('status')).toContainText('تطبیق دارد')
  await accounting.getByRole('tab', { name: 'فاکتورها' }).click()
  await expect(accounting.getByRole('row', { name: new RegExp(invoiceNumber) })).toContainText('پرداخت ناقص')

  // Close the session so the shared register is free for the next run.
  await cashier.getByRole('button', { name: 'بستن صندوق' }).click()
  const closeDialog = cashier.getByRole('dialog')
  await closeDialog.getByLabel('مبلغ شمارش‌شده').fill('200')
  await closeDialog.getByRole('button', { name: 'بستن صندوق' }).click()
  await expect(closeDialog).toBeHidden()
  await expect(cashier.getByRole('button', { name: 'باز کردن صندوق' })).toBeVisible()

  expect(errors).toEqual([])
})
