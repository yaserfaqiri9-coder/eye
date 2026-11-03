import { expect, test, type APIRequestContext, type Browser, type Page } from '@playwright/test'

// Runs against a published release through scripts/pilot/smoke.ps1, not against the dev server.
const base = process.env.ALSHIFA_PILOT_BASE
test.skip(!base, 'needs a running release: see scripts/pilot/smoke.ps1')

// The certificate chain is proven by lan-check.mjs; this browser profile simply has no hospital CA installed.
test.use({ baseURL: base, ignoreHTTPSErrors: true })

const staffPassword = 'Pilot-Test-Password-1'

async function login(page: Page, user: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(password)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function staffPage(browser: Browser, user: string) {
  const context = await browser.newContext({ baseURL: base, ignoreHTTPSErrors: true })
  const page = await context.newPage()
  await login(page, user, staffPassword)
  return page
}

/** Same-site API call with the session and CSRF cookies of an open page. */
async function call(page: Page, method: string, path: string, body?: unknown) {
  const request: APIRequestContext = page.request
  const cookies = await page.context().cookies()
  const token = decodeURIComponent(cookies.find((c) => c.name === 'XSRF-TOKEN')?.value ?? '')
  const response = await request.fetch(`/api${path}`, {
    method,
    data: body,
    headers: { 'X-XSRF-TOKEN': token, 'Idempotency-Key': crypto.randomUUID().replaceAll('-', '') },
  })
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy()
  return response.status() === 204 ? null : response.json()
}

async function newUsers(admin: Page, stamp: number, roles: Record<string, string>) {
  for (const [name, role] of Object.entries(roles)) {
    await call(admin, 'POST', '/users', { userName: `${name}_${stamp}`, fullName: name, password: staffPassword, roles: [role] })
  }
}

async function adminPage(browser: Browser) {
  const context = await browser.newContext({ baseURL: base, ignoreHTTPSErrors: true })
  const page = await context.newPage()
  await login(page, 'pilotadmin', process.env.ALSHIFA_PILOT_PASSWORD!)
  return page
}

/** A cashier with an open session of their own and one invoice to collect. */
async function cashierWithInvoice(browser: Browser) {
  const stamp = Date.now()
  const admin = await adminPage(browser)
  await newUsers(admin, stamp, { cashier: 'Cashier' })
  const service = await call(admin, 'POST', '/services', { code: `S${stamp}`, name: `خدمت ${stamp}`, isActive: true, price: 500 })
  const register = await call(admin, 'POST', '/cash/registers', { name: `صندوق ${stamp}` })

  const cashier = await staffPage(browser, `cashier_${stamp}`)
  await call(cashier, 'POST', '/cash/sessions', { registerId: register.id, openingAmount: 0 })
  const invoice = await call(cashier, 'POST', '/invoices', { items: [{ serviceId: service.id, quantity: 1 }] })
  await cashier.goto('/cashier')
  await cashier.getByLabel('جستجوی فاکتور').fill(invoice.invoiceNumber)
  await cashier.getByRole('row', { name: new RegExp(invoice.invoiceNumber) }).getByRole('button', { name: 'دریافت' }).click()
  await expect(cashier.getByRole('dialog').getByLabel('مبلغ')).toHaveValue('500')
  return { cashier, invoice }
}

test('a payment whose answer is lost on the network is recorded once when the cashier tries again', async ({ browser }) => {
  const { cashier, invoice } = await cashierWithInvoice(browser)
  const dialog = cashier.getByRole('dialog')

  // The server processes the payment; only its answer never reaches the browser.
  let delivered = 0
  await cashier.route('**/api/payments', async (route) => {
    if (delivered++ === 0) {
      await route.fetch()
      await route.abort('connectionreset')
    } else {
      await route.continue()
    }
  })

  await dialog.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(dialog.getByRole('alert')).toContainText('معلوم نیست این عملیات ثبت شده یا نه')

  await dialog.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(dialog.getByRole('status')).toContainText('شماره رسید')

  const detail = await call(cashier, 'GET', `/invoices/${invoice.id}`)
  expect(detail.payments).toHaveLength(1)
  expect(detail.invoice.balance).toBe(0)
})

test('a double click on the payment button records one payment', async ({ browser }) => {
  const { cashier, invoice } = await cashierWithInvoice(browser)
  const dialog = cashier.getByRole('dialog')
  await dialog.getByRole('button', { name: 'ثبت دریافت' }).dblclick()
  await expect(dialog.getByRole('status')).toContainText('شماره رسید')

  const detail = await call(cashier, 'GET', `/invoices/${invoice.id}`)
  expect(detail.payments).toHaveLength(1)
  expect(detail.invoice.balance).toBe(0)
})

test('losing the network shows a warning, a failed save stays on screen, and work continues after reconnecting', async ({ browser }) => {
  test.setTimeout(150_000)
  const { cashier, invoice } = await cashierWithInvoice(browser)
  const dialog = cashier.getByRole('dialog')

  await cashier.context().setOffline(true)
  await dialog.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(dialog.getByRole('alert')).toContainText('معلوم نیست این عملیات ثبت شده یا نه')
  await expect(cashier.getByText('اتصال زنده قطع است')).toBeVisible({ timeout: 45_000 })

  await cashier.context().setOffline(false)
  await expect(cashier.getByText('اتصال زنده قطع است')).toBeHidden({ timeout: 45_000 })
  await dialog.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(dialog.getByRole('status')).toContainText('شماره رسید')

  const detail = await call(cashier, 'GET', `/invoices/${invoice.id}`)
  expect(detail.payments).toHaveLength(1)
})

test('two people editing one patient: the second sees a conflict and the first change survives', async ({ browser }) => {
  const stamp = Date.now()
  const admin = await adminPage(browser)
  await newUsers(admin, stamp, { first: 'Receptionist', second: 'Receptionist' })
  const first = await staffPage(browser, `first_${stamp}`)
  const second = await staffPage(browser, `second_${stamp}`)
  const patient = await call(first, 'POST', '/patients', { firstName: `هم‌زمان${stamp}`, gender: 'Male', estimatedAge: 30 })

  await first.goto(`/patients/${patient.id}`)
  await second.goto(`/patients/${patient.id}`)
  await first.getByRole('link', { name: 'ویرایش' }).click()
  await second.getByRole('link', { name: 'ویرایش' }).click()

  await first.getByLabel('آدرس').fill('هرات')
  await first.getByRole('button', { name: 'ذخیره' }).click()
  await expect(first).toHaveURL(new RegExp(`/patients/${patient.id}$`))

  await second.getByLabel('آدرس').fill('بلخ')
  await second.getByRole('button', { name: 'ذخیره' }).click()
  await expect(second.getByRole('alert')).toContainText('کاربر دیگری')

  expect((await call(first, 'GET', `/patients/${patient.id}`)).address).toBe('هرات')
})

test('a change in one department reaches another open screen without a reload', async ({ browser }) => {
  const stamp = Date.now()
  const admin = await adminPage(browser)
  await newUsers(admin, stamp, { rec: 'Receptionist', tri: 'Triage' })
  const reception = await staffPage(browser, `rec_${stamp}`)
  const triage = await staffPage(browser, `tri_${stamp}`)
  await expect(triage).toHaveURL(/\/triage$/)

  const departments = await call(reception, 'GET', '/lookups/departments')
  const departmentId = departments.find((d: { name: string }) => d.name === 'معاینه ابتدایی').id
  const firstName = `زنده${stamp}`
  const patient = await call(reception, 'POST', '/patients', { firstName, gender: 'Female', estimatedAge: 25 })

  // A visit needs its fee paid first. The tariff was set by lan-check.mjs, which smoke.ps1 runs before this.
  const register = await call(admin, 'POST', '/cash/registers', { name: `پذیرش ${stamp}` })
  await call(reception, 'POST', '/cash/sessions', { registerId: register.id, openingAmount: 0 })
  const token = await call(reception, 'POST', '/visit-tokens', { departmentId })
  await call(reception, 'POST', '/reception-fees/pay', { invoiceId: token.invoiceId, amount: token.balance, method: 'Cash', invoiceVersion: token.invoiceVersion })
  await call(reception, 'POST', '/visits', { patientId: patient.id, departmentId, tokenId: token.id })

  await expect(triage.getByText(firstName)).toBeVisible({ timeout: 10_000 })
})

test('works with no internet: nothing outside the host is requested and the page is fully functional', async ({ page, context }) => {
  const host = new URL(base!).host
  const external: string[] = []
  const violations: string[] = []
  const failed: string[] = []

  await context.route('**/*', (route) => {
    const url = new URL(route.request().url())
    if (url.protocol === 'data:' || url.host === host) return route.continue()
    external.push(url.href)
    return route.abort('internetdisconnected')
  })
  page.on('console', (message) => {
    if (message.text().includes('Content Security Policy')) violations.push(message.text())
  })
  page.on('response', (response) => {
    if (response.status() >= 400 && !response.url().endsWith('/api/auth/me')) failed.push(`${response.status()} ${response.url()}`)
  })

  await page.goto('/')
  await page.getByLabel('نام کاربری').fill('pilotadmin')
  await page.getByLabel('رمز').fill(process.env.ALSHIFA_PILOT_PASSWORD!)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/login/)

  // Past the 3 second grace period, the live connection warning would be visible if the WebSocket were blocked.
  await page.waitForTimeout(4000)
  await expect(page.getByText('اتصال زنده قطع است')).toBeHidden()

  await page.reload()
  await expect(page).not.toHaveURL(/login/)
  await page.goto('/users')
  await expect(page.locator('.layout__content')).toBeVisible()

  const font = await page.evaluate(async () => {
    await document.fonts.ready
    return document.fonts.check('16px Vazirmatn')
  })

  expect(external).toEqual([])
  expect(violations).toEqual([])
  expect(failed).toEqual([])
  expect(font).toBe(true)
})

test('a failing backup shows a clear warning to the people who can act on it', async ({ page }) => {
  await page.route('**/api/settings/backup-status', (route) =>
    route.fulfill({ json: { configured: true, lastSuccessAt: null, lastError: 'disk missing', needsAttention: true } }),
  )
  await page.goto('/')
  await page.getByLabel('نام کاربری').fill('pilotadmin')
  await page.getByLabel('رمز').fill(process.env.ALSHIFA_PILOT_PASSWORD!)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'پشتیبان‌گیری خودکار مشکل دارد' })).toBeVisible()
})
