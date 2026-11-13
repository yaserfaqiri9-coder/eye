import { expect, test, type Browser, type BrowserContext, type Page, type Route } from '@playwright/test'

// Searches must never show an older answer, lists must follow other users' changes, and loading placeholders must close.
const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'
const stamp = Date.now()

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

/** Calls the API with a signed-in context's cookies, as the page itself would. */
function apiOf(context: BrowserContext) {
  return async <T = unknown>(method: string, path: string, data?: unknown): Promise<T> => {
    const xsrf = (await context.cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
    const response = await context.request.fetch(`/api${path}`, {
      method,
      data,
      headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf), 'Idempotency-Key': crypto.randomUUID().replaceAll('-', '') },
    })
    if (!response.ok()) throw new Error(`${method} ${path}: ${response.status()} ${await response.text()}`)
    return (response.status() === 204 ? null : await response.json()) as T
  }
}

async function signedIn(browser: Browser, user: string, pass = password) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await login(page, user, pass)
  return { context, page, api: apiOf(context) }
}

let admin: Awaited<ReturnType<typeof signedIn>>

async function staff(browser: Browser, role: string) {
  const userName = `live_${role}_${stamp}_${Math.floor(Math.random() * 1e6)}`
  await admin.api('POST', '/users', { userName, fullName: userName, password, roles: [role] })
  return signedIn(browser, userName)
}

async function newService() {
  const service = await admin.api<{ id: number }>('POST', '/services', { name: `خدمت زنده ${stamp}${Math.random()}`, isActive: true, price: 300 })
  return service.id
}

async function newPatient(firstName: string) {
  return admin.api<{ id: number; fileNumber: string }>('POST', '/patients', { firstName, gender: 'Male', estimatedAge: 30, confirmNotDuplicate: true })
}

async function newInvoice(patientId: number, serviceId: number) {
  return admin.api<{ id: number; invoiceNumber: string; balance: number }>('POST', '/invoices', { patientId, items: [{ serviceId, quantity: 1 }] })
}

/** Answers one request late: the first matching term waits, so a newer search answers first. */
function delayWhen(match: (url: URL) => boolean, ms: number) {
  return async (route: Route) => {
    if (match(new URL(route.request().url()))) await sleep(ms)
    try {
      await route.continue()
    } catch {
      // The page cancelled this request; that is the point.
    }
  }
}

test.beforeAll(async ({ browser }) => {
  admin = await signedIn(browser, adminUser, adminPassword)
})

test.afterAll(async () => {
  await admin.context.close()
})

test('pharmacy search: a slow answer for an older term never replaces the newer list', async ({ browser }) => {
  // Digits would also match visit and prescription numbers, so the names carry letters only.
  const tag = String(stamp).replace(/\d/g, (d) => 'abcdefghij'[Number(d)])
  await newPatient(`فارم${tag}الف`)
  await newPatient(`فارم${tag}ب`)
  const { page, context } = await staff(browser, 'Pharmacist')
  await page.route('**/api/pharmacy/search**', delayWhen((url) => url.searchParams.get('q') === 'مریض1', 1500))
  await page.goto('/pharmacy')
  const box = page.getByLabel('جستجوی مریض و نسخه')

  await box.fill('مریض1')
  await sleep(500)
  await box.fill(`فارم${tag}`)

  const names = page.locator('table.xr-table tbody tr .xr-person__name')
  await expect(names).toHaveCount(2)
  await sleep(2000) // longer than the delayed older answer
  await expect(names).toHaveCount(2)
  for (const name of await names.allTextContents()) expect(name).toContain(`فارم${tag}`)
  await context.close()
})

test('inventory search: typing reloads only products and the older answer is dropped', async ({ browser }) => {
  const { page, context, api } = await staff(browser, 'Inventory')
  for (const prefix of ['PFA', 'PFB']) {
    await api('POST', '/inventory/products', { name: `جنس ${prefix} ${stamp}`, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 10 })
  }
  await page.goto('/inventory')
  await page.getByRole('tab', { name: 'اجناس' }).click()
  await expect(page.getByLabel('جستجوی جنس')).toBeVisible()
  // Let the first load and the live event for the two new products finish before watching.
  await page.waitForLoadState('networkidle')
  await sleep(2500)

  const others: string[] = []
  page.on('request', (r) => {
    const path = new URL(r.url()).pathname
    if (/\/api\/inventory\/(batches|alerts|movements|purchases|locations)$/.test(path)) others.push(`${new Date().toISOString()} ${path}`)
  })
  await page.route('**/api/inventory/products**', delayWhen((url) => url.searchParams.get('q') === `PFA${stamp}`, 1500))

  await page.getByLabel('جستجوی جنس').fill(`PFA${stamp}`)
  await sleep(450)
  await page.getByLabel('جستجوی جنس').fill(`PFB${stamp}`)

  const codes = page.locator('table.xr-table tbody tr td:first-child')
  await expect(codes).toHaveText([`PFB${stamp}`])
  await sleep(2000)
  await expect(codes).toHaveText([`PFB${stamp}`])
  expect(others).toEqual([])
  await context.close()
})

test('patient file shows only the bills of that patient id', async () => {
  const serviceId = await newService()
  const own = await newPatient(`دوسیه${stamp}`)
  const other = await newPatient(`دیگر${stamp}`)
  const ownInvoice = await newInvoice(own.id, serviceId)
  const otherInvoice = await newInvoice(other.id, serviceId)

  const page = await admin.context.newPage()
  const asked = page.waitForRequest((r) => r.url().includes('/api/invoices?'))
  await page.goto(`/patients/${own.id}`)
  const url = new URL((await asked).url())
  expect(url.searchParams.get('patientId')).toBe(String(own.id))
  expect(url.searchParams.has('q')).toBe(false)
  await page.getByRole('tab', { name: 'مالی', exact: true }).click()

  await expect(page.getByText(ownInvoice.invoiceNumber, { exact: true })).toBeVisible()
  await expect(page.getByText(otherInvoice.invoiceNumber, { exact: true })).toHaveCount(0)
  await page.close()
})

test('a payment by one cashier reaches another, even when it lands during a reload', async ({ browser }) => {
  const serviceId = await newService()
  const patient = await newPatient(`پرداخت${stamp}`)
  const register = await admin.api<{ id: number }>('POST', '/cash/registers', { name: `صندوق زنده ${stamp}` })
  const payer = await staff(browser, 'Cashier')
  await payer.api('POST', '/cash/sessions', { registerId: register.id, openingAmount: 0 })
  const watcher = await staff(browser, 'Cashier')
  await watcher.page.goto('/cashier')
  await expect(watcher.page.getByLabel('جستجوی فاکتور')).toBeVisible()

  // The reload caused by the new bill reads the database at once but answers late; the payment happens meanwhile.
  // Only the reload whose answer already lists the new bill is held, so a reload caused by an earlier event is not mistaken for it.
  let target = ''
  let heldBody = ''
  let held: () => void = () => {}
  const holding = new Promise<void>((resolve) => (held = resolve))
  let holdStarted = false
  let afterHold = 0
  await watcher.page.route('**/api/invoices?**', async (route) => {
    const response = await route.fetch()
    const body = await response.text()
    if (holdStarted) afterHold++
    if (target && !holdStarted && body.includes(target)) {
      holdStarted = true
      heldBody = body
      held()
      await sleep(2500)
    }
    await route.fulfill({ response, body })
  })

  const invoice = await newInvoice(patient.id, serviceId)
  target = invoice.invoiceNumber
  await holding
  await payer.api('POST', '/payments', { invoiceId: invoice.id, amount: invoice.balance, method: 'Cash' })

  // The held answer still lists the bill as unpaid: it is stale by the time it lands.
  expect(heldBody).toContain(invoice.invoiceNumber)
  // Well inside the 60-second safety refresh, only the queued reload can clear it.
  const row = watcher.page.locator('tr', { hasText: invoice.invoiceNumber })
  await sleep(3000)
  await expect(row).toHaveCount(0, { timeout: 5000 })
  expect(afterHold).toBeGreaterThan(0)
  await payer.context.close()
  await watcher.context.close()
})

test('stock received by one user shows on another user\'s inventory list', async ({ browser }) => {
  const keeper = await staff(browser, 'Inventory')
  const product = await keeper.api<{ id: number }>('POST', '/inventory/products', { name: `جنس زنده ${stamp}`, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 10 })
  const watcher = await staff(browser, 'Inventory')
  await watcher.page.goto('/inventory')
  await watcher.page.getByRole('tab', { name: 'اجناس' }).click()
  // Columns: code, name, group, drug form, on hand …
  const onHand = watcher.page.locator('tr', { hasText: `جنس زنده ${stamp}` }).locator('td').nth(4)
  await expect(onHand).toHaveText(/^(۰|0)\s/)

  await keeper.api('POST', '/inventory/purchases', { locationCode: 'pharmacy', items: [{ productId: product.id, lotNumber: 'LV1', quantity: 37, unitCost: 4 }] })
  await expect(onHand).toHaveText(/^(۳۷|37)\s/, { timeout: 6000 })
  await keeper.context.close()
  await watcher.context.close()
})

test('stock received while another user’s list is reloading is not lost', async ({ browser }) => {
  const keeper = await staff(browser, 'Inventory')
  const name = `جنس بچ ${stamp}`
  const product = await keeper.api<{ id: number }>('POST', '/inventory/products', { name, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 10 })
  const watcher = await staff(browser, 'Inventory')
  await watcher.page.goto('/inventory')
  await watcher.page.getByRole('tab', { name: 'موجودی', exact: true }).click()
  await watcher.page.getByRole('tab', { name: 'بچ‌ها', exact: true }).click()
  await watcher.page.waitForLoadState('networkidle')
  await sleep(2000)

  // This page's lists have no cancel of their own: the reload for the first receipt answers late, after the second receipt.
  let hold = true
  let held: () => void = () => {}
  const holding = new Promise<void>((resolve) => (held = resolve))
  await watcher.page.route('**/api/inventory/batches', async (route) => {
    if (!hold) return route.continue()
    hold = false
    const response = await route.fetch()
    held()
    await sleep(2500)
    await route.fulfill({ response })
  })

  const receive = (lot: string, quantity: number) =>
    keeper.api('POST', '/inventory/purchases', { locationCode: 'pharmacy', items: [{ productId: product.id, lotNumber: lot, quantity, unitCost: 4 }] })
  await receive(`A${stamp}`, 37)
  await holding
  await receive(`B${stamp}`, 5)

  const lots = watcher.page.locator('tr', { hasText: name })
  await expect(lots).toHaveCount(2, { timeout: 8000 })
  await sleep(3000) // the late answer, holding only the first lot, must not land last
  await expect(lots).toHaveCount(2)
  await keeper.context.close()
  await watcher.context.close()
})

test('a hidden tab catches up as soon as it is shown again', async ({ browser }) => {
  const serviceId = await newService()
  const patient = await newPatient(`پنهان${stamp}`)
  const watcher = await staff(browser, 'Cashier')
  // No live events for this tab: only becoming visible can refresh it.
  await watcher.page.route('**/hubs/**', (route) => route.abort())
  await watcher.page.goto('/cashier')
  await expect(watcher.page.getByLabel('جستجوی فاکتور')).toBeVisible()

  const setHidden = (hidden: boolean) =>
    watcher.page.evaluate((value) => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => value })
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => (value ? 'hidden' : 'visible') })
      document.dispatchEvent(new Event('visibilitychange'))
    }, hidden)

  await setHidden(true)
  const invoice = await newInvoice(patient.id, serviceId)
  const row = watcher.page.locator('tr', { hasText: invoice.invoiceNumber })
  await sleep(1500)
  await expect(row).toHaveCount(0)

  await setHidden(false)
  await expect(row).toHaveCount(1, { timeout: 3000 })
  await watcher.context.close()
})

test('the loading placeholder closes after success and after an error', async ({ browser }) => {
  const { page, context } = await staff(browser, 'Receptionist')
  const skeleton = page.locator('.ui-list-skeleton')

  await page.route('**/api/patients?**', delayWhen(() => true, 1200))
  await page.goto('/patients')
  await expect(skeleton).toBeVisible()
  await expect(skeleton).toBeHidden({ timeout: 5000 })
  await expect(page.locator('table.xr-table tbody tr').first()).toBeVisible()

  await page.unroute('**/api/patients?**')
  await page.route('**/api/patients?**', async (route) => {
    await sleep(800)
    await route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"title":"error"}' })
  })
  await page.goto('/')
  await page.goto('/patients')
  await expect(skeleton).toBeVisible()
  await expect(skeleton).toBeHidden({ timeout: 5000 })
  await expect(page.getByRole('alert')).toBeVisible()
  await context.close()
})

test('inventory shows the drug form apart from group and unit, finds drugs in any letter case, and requires the form', async ({ browser }) => {
  const { page, context, api } = await staff(browser, 'Inventory')
  const tag = String(stamp).slice(-6)
  await api('POST', '/inventory/products', { name: `Vigamox${tag} Eye Drops`, kind: 'Drug', category: 'قطره چشمی', unit: 'قطی', reorderLevel: 0, salePrice: 10 })
  await page.goto('/inventory')
  await page.getByRole('tab', { name: 'اجناس' }).click()
  await expect(page.getByRole('columnheader', { name: 'گروه جنس' })).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'نوعیت دوا' })).toBeVisible()

  for (const q of [`vigamox${tag}`, `VIGAMOX${tag}`, `  Vigamox${tag}  `]) {
    await page.getByLabel('جستجوی جنس').fill(q)
    const row = page.locator('table.xr-table tbody tr')
    await expect(row).toHaveCount(1)
    const cells = row.locator('td')
    await expect(cells.nth(1)).toHaveText(`Vigamox${tag} Eye Drops`)
    await expect(cells.nth(2)).toHaveText('دوا')
    await expect(cells.nth(3)).toHaveText('قطره چشمی')
    await expect(cells.nth(4)).toContainText('قطی')
    await expect(cells.nth(4)).not.toContainText('قطره')
  }

  // A new drug cannot be saved without its form.
  await page.getByRole('button', { name: /جنس جدید/ }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('کد').fill(`NF${tag}`)
  await dialog.getByLabel('نام جنس').fill(`بدون نوعیت ${tag}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeVisible()
  expect(await dialog.getByLabel('نوعیت دوا').evaluate((el) => (el as HTMLSelectElement).validity.valueMissing)).toBe(true)
  const page2 = await api<{ total: number }>('GET', `/inventory/products?q=NF${tag}`)
  expect(page2.total).toBe(0)

  await dialog.getByLabel('نوعیت دوا').selectOption('تابلیت')
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()
  const saved = await api<{ items: { category: string; unit: string }[] }>('GET', `/inventory/products?q=NF${tag}`)
  expect(saved.items[0].category).toBe('تابلیت')
  expect(saved.items[0].unit).toBe('عدد')
  await context.close()
})
