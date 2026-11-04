import { expect, type Locator, type Page } from '@playwright/test'

/** Chooses from a searchable field the way a user does. The list is on the page body, so it is found from the page. */
export async function pick(page: Page, scope: Page | Locator, label: string, text: string) {
  const field = scope.getByRole('combobox', { name: label, exact: true })
  await field.click()
  await field.fill(text)
  await page.getByRole('listbox').getByRole('option', { name: new RegExp(text) }).first().click()
  await expect(page.getByRole('listbox')).toHaveCount(0)
}

/** Tariffs belong to finance and are never seeded, so a fresh test database has none until a test sets them. */
export async function ensureTariffs(admin: Page) {
  const services = await call<{ id: number; code: string; currentPrice: number | null }[]>(admin, 'GET', '/services')
  for (const service of services.filter((s) => ['VISIT-FEE', 'REFRACTION', 'IMAGING'].includes(s.code) && !s.currentPrice)) {
    await call(admin, 'POST', `/services/${service.id}/price`, { price: 100 })
  }
}

const cashiers = new WeakMap<Page, Page>()

async function newCashier(admin: Page) {
  const userName = `cash_${crypto.randomUUID().slice(0, 13)}`
  const password = 'E2e-Password-123'
  await call(admin, 'POST', '/users', { userName, fullName: 'صندوق‌دار آزمایشی', password, roles: ['Cashier'] })
  const context = await admin.context().browser()!.newContext()
  const cashier = await context.newPage()
  await cashier.goto('/login')
  await cashier.getByLabel('نام کاربری').fill(userName)
  await cashier.getByLabel('رمز عبور').fill(password)
  await cashier.getByRole('button', { name: 'ورود' }).click()
  await expect(cashier).not.toHaveURL(/\/login/)
  const register = await call<{ id: number }>(admin, 'POST', '/cash/registers', { name: `صندوق ${crypto.randomUUID().slice(0, 13)}` })
  await call(cashier, 'POST', '/cash/sessions', { registerId: register.id, openingAmount: 0 })
  return cashier
}

/** Reception takes no money, so each test's receptionist gets a cashier of their own who collects the fees. */
export async function openDesk(admin: Page, desk: Page) {
  cashiers.set(desk, await newCashier(admin))
}

/** The cashier collects a patient's unpaid fee, such as refraction or imaging, in full. */
export async function collectPatientFee(desk: Page, fileNumber: string) {
  const cashier = cashiers.get(desk)!
  const { items } = await call<{ items: { id: number; balance: number; version: number }[] }>(cashier, 'GET', `/invoices?unpaid=true&q=${encodeURIComponent(fileNumber)}`)
  await call(cashier, 'POST', '/payments', { invoiceId: items[0].id, amount: items[0].balance, method: 'Cash', invoiceVersion: items[0].version })
}

/** What must happen before a visit can exist: a token from reception with its fee paid in full at the cashier. Returns the token's id. */
export async function paidToken(desk: Page, departmentId: number) {
  const token = await call<{ id: number; invoiceId: number; balance: number; invoiceVersion: number }>(desk, 'POST', '/visit-tokens', { departmentId })
  await call(cashiers.get(desk)!, 'POST', '/payments', { invoiceId: token.invoiceId, amount: token.balance, method: 'Cash', invoiceVersion: token.invoiceVersion })
  return token.id
}

/** The cashier collects the invoice on reception's screen. Reception then shows the queue number by itself, with no reload. */
export async function collectFee(page: Page) {
  const number = (await page.locator('dt', { hasText: 'فاکتور' }).locator('+ dd').innerText()).trim()
  const cashier = cashiers.get(page)!
  const { items } = await call<{ items: { id: number; balance: number; version: number }[] }>(cashier, 'GET', `/invoices?unpaid=true&q=${encodeURIComponent(number)}`)
  await call(cashier, 'POST', '/payments', { invoiceId: items[0].id, amount: items[0].balance, method: 'Cash', invoiceVersion: items[0].version })
  await expect(page.getByRole('status').filter({ hasText: 'شماره نوبت' })).toBeVisible({ timeout: 15_000 })
}

/** From a patient's record through the reception flow: visit registered, fee collected, number issued. */
export async function admitFromRecord(page: Page, department?: string, service?: string) {
  await page.getByRole('link', { name: 'مراجعه جدید' }).click()
  await expect(page.getByRole('button', { name: 'ثبت مراجعه' })).toBeVisible()
  if (department) await pick(page, page, 'بخش', department)
  if (service) await pick(page, page, 'خدمت اضافی قابل پرداخت (اختیاری)', service)
  await page.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await collectFee(page)
}

/** Calls the API from inside a signed-in page, with its CSRF token. */
export async function call<T = unknown>(page: Page, method: string, path: string, body?: unknown): Promise<T> {
  return page.evaluate(
    async ({ method, path, body }) => {
      let cookie = document.cookie.match(/XSRF-TOKEN=([^;]+)/)
      if (!cookie) {
        await fetch('/api/auth/csrf')
        cookie = document.cookie.match(/XSRF-TOKEN=([^;]+)/)
      }
      const response = await fetch(`/api${path}`, {
        method,
        headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': decodeURIComponent(cookie![1]), 'Idempotency-Key': crypto.randomUUID().replaceAll('-', '') },
        body: body === undefined ? undefined : JSON.stringify(body),
      })
      const text = await response.text()
      if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`)
      return text ? JSON.parse(text) : null
    },
    { method, path, body },
  )
}
