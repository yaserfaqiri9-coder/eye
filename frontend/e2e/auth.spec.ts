import { expect, test } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!

async function login(page: import('@playwright/test').Page, user: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(password)
  await page.getByRole('button', { name: 'ورود' }).click()
}

test('wrong password shows a Dari error and stays on login', async ({ page }) => {
  await login(page, adminUser, 'definitely-wrong-password')
  await expect(page.getByRole('alert')).toContainText('نام کاربری یا رمز درست نیست')
  await expect(page).toHaveURL(/\/login/)
})

test('anonymous visitor is sent to login', async ({ page }) => {
  await page.goto('/users')
  await expect(page).toHaveURL(/\/login\?next=/)
})

test('admin creates a receptionist who sees no user management, then is deactivated', async ({ page, browser }) => {
  const consoleErrors: string[] = []
  page.on('pageerror', (e) => consoleErrors.push(e.message))

  await login(page, adminUser, adminPassword)
  await expect(page.getByText('خوش آمدید')).toBeVisible()

  await page.getByRole('link', { name: 'کاربران' }).click()
  await expect(page).toHaveURL(/\/users$/)

  const userName = `e2e_${Date.now()}`
  const password = 'E2e-Password-123'
  await page.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('نام کاربری').fill(userName)
  await dialog.getByLabel('نام کامل').fill('کارمند آزمایشی')
  await dialog.getByLabel(/رمز عبور/).fill(password)
  await dialog.getByLabel('پذیرش').check()
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()

  await page.getByRole('searchbox', { name: 'جستجو', exact: true }).fill(userName)
  await page.getByRole('button', { name: 'جستجو', exact: true }).click()
  const row = page.getByRole('row', { name: new RegExp(userName) })
  await expect(row).toBeVisible()

  const staffContext = await browser.newContext()
  const staff = await staffContext.newPage()
  await login(staff, userName, password)
  await expect(staff).toHaveURL(/\/reception$/)
  await expect(staff.getByRole('link', { name: 'کاربران' })).toHaveCount(0)

  // Typing the URL must not open the page, and the API itself must refuse.
  await staff.goto('/users')
  await expect(staff).toHaveURL(/\/reception$/)
  const direct = await staff.evaluate(() => fetch('/api/users').then((r) => r.status))
  expect(direct).toBe(403)

  await row.getByRole('button', { name: 'غیرفعال‌سازی' }).click()
  await expect(row.getByText('غیرفعال', { exact: true })).toBeVisible()

  const afterDeactivate = await staff.evaluate(() => fetch('/api/auth/me').then((r) => r.status))
  expect(afterDeactivate).toBe(401)

  await staffContext.close()
  expect(consoleErrors).toEqual([])
})
