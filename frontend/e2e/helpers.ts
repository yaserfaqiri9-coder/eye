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

/** A register holds one open session, so each test's receptionist gets one of their own. */
export async function openDesk(admin: Page, desk: Page) {
  const register = await call<{ id: number }>(admin, 'POST', '/cash/registers', { name: `صندوق ${crypto.randomUUID().slice(0, 13)}` })
  await call(desk, 'POST', '/cash/sessions', { registerId: register.id, openingAmount: 0 })
}

/** What reception does before a visit can exist: a token with its fee paid in full. Returns the token's id. */
export async function paidToken(desk: Page, departmentId: number) {
  const token = await call<{ id: number; invoiceId: number; balance: number; invoiceVersion: number }>(desk, 'POST', '/visit-tokens', { departmentId })
  await call(desk, 'POST', '/reception-fees/pay', { invoiceId: token.invoiceId, amount: token.balance, method: 'Cash', invoiceVersion: token.invoiceVersion })
  return token.id
}

/** The first step of the visit dialog: issues the token and collects the fee, which opens the second step. */
export async function collectFee(dialog: Locator) {
  await dialog.getByRole('button', { name: 'ثبت نوبت و دریافت فیس' }).click()
  await expect(dialog.getByRole('status').filter({ hasText: 'فیس ویزیت دریافت شد' })).toBeVisible()
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
