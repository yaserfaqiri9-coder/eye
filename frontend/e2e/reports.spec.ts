import { readFile } from 'node:fs/promises'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { call } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

const financialReports = [
  ['profit-loss', 'مفاد و ضرر کل سیستم'],
  ['revenue-sources', 'عواید بخش‌ها'],
  ['expense-summary', 'خلاصه مصارف به تفکیک نوع'],
  ['daily-finance', 'عملکرد مالی روزانه'],
  ['cash-flow', 'گردش حساب صندوق'],
]
const moneyReports = ['دریافت‌ها', 'طلبات', 'مصارف', ...financialReports.map(([, name]) => name)]

test.afterEach(async ({ browser }) => {
  await Promise.all(browser.contexts().map(context => context.close()))
})

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

/** A signed-in page of a new user holding one role. */
async function userWithRole(browser: Browser, role: string, viewport?: { width: number; height: number }) {
  const context = await browser.newContext()
  const admin = await context.newPage()
  await login(admin, adminUser, adminPassword)
  const userName = `rep_${crypto.randomUUID().slice(0, 13)}`
  await call(admin, 'POST', '/users', { userName, fullName: `گزارش ${role}`, password, roles: [role] })
  await context.close()

  const own = await browser.newContext({ acceptDownloads: true, viewport: viewport ?? { width: 1400, height: 900 } })
  const page = await own.newPage()
  await login(page, userName, password)
  return page
}

const title = (page: Page) => page.getByRole('heading', { level: 1 })
const tabs = (page: Page) => page.getByRole('tablist', { name: 'دسته گزارش‌ها' })
/** The one bar of an open report: its name, days and actions. */
const bar = (page: Page) => page.getByRole('region', { name: /^فیلترهای / })
const back = (page: Page) => bar(page).getByRole('link', { name: 'گزارش‌ها' })

/** From an open report or the gallery: back to the gallery, the report's tab, then its picture. */
async function openReport(page: Page, tab: string, name: string) {
  if (await back(page).isVisible()) await back(page).click()
  await tabs(page).getByRole('tab', { name: tab }).click()
  await page.getByRole('button', { name, exact: true }).click()
}

test('the gallery opens on the first tab, a tab survives a reload, and a picture opens its report', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(title(page)).toHaveText('گزارش‌ها')
  await expect(tabs(page).getByRole('tab', { name: 'کار شفاخانه' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('button', { name: 'کار داکتران' })).toBeVisible()

  await tabs(page).getByRole('tab', { name: 'پول و حساب' }).click()
  await expect(page).toHaveURL(/group=money/)
  await page.reload()
  await expect(tabs(page).getByRole('tab', { name: 'پول و حساب' })).toHaveAttribute('aria-selected', 'true')

  await page.getByRole('button', { name: 'دریافت‌ها', exact: true }).click()
  await expect(title(page)).toHaveText('دریافت‌ها')
  await expect(page).toHaveURL(/report=cash/)
  await expect(page.getByRole('heading', { name: 'فهرست دریافت‌ها' })).toBeVisible()

  await back(page).click()
  await expect(title(page)).toHaveText('گزارش‌ها')
  await expect(tabs(page).getByRole('tab', { name: 'پول و حساب' })).toHaveAttribute('aria-selected', 'true')
})

test('a report opens as one list with its few figures and row numbers', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports?report=patients&period=month')
  await expect(title(page)).toHaveText('مراجعات مریضان')
  await expect(page.locator('.report-tiles > *')).toHaveCount(4)
  await expect(page.locator('.report-sheet .xr-card')).toHaveCount(1)
  await expect(page.getByRole('columnheader', { name: 'ردیف' })).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'نام مریض' })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('an accountant sees the money reports and can take Excel and PDF files', async ({ browser }) => {
  const page = await userWithRole(browser, 'Accountant')
  await page.goto('/reports')
  for (const group of ['کار شفاخانه', 'پول و حساب', 'دوا و گدام']) await expect(tabs(page).getByRole('tab', { name: group })).toBeVisible()
  await tabs(page).getByRole('tab', { name: 'پول و حساب' }).click()
  for (const name of moneyReports) await expect(page.getByRole('button', { name, exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'مصارف', exact: true }).click()
  await expect(title(page)).toHaveText('مصارف')

  const [excel] = await Promise.all([page.waitForEvent('download'), bar(page).getByRole('button', { name: 'Excel' }).click()])
  expect(excel.suggestedFilename()).toMatch(/^report-expenses-\d{4}-\d{2}-\d{2}\.xlsx$/)
  const workbook = await readFile(await excel.path())
  expect([...workbook.subarray(0, 2)]).toEqual([0x50, 0x4b])

  const [pdf] = await Promise.all([page.waitForEvent('download'), bar(page).getByRole('button', { name: 'PDF' }).click()])
  expect(pdf.suggestedFilename()).toMatch(/^report-expenses-\d{4}-\d{2}-\d{2}\.pdf$/)
  expect((await readFile(await pdf.path())).subarray(0, 4).toString('ascii')).toBe('%PDF')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('the five financial reports open through the existing gallery with periods, files and a single table on desktop and mobile', async ({ browser }) => {
  const page = await userWithRole(browser, 'Accountant')
  await page.goto('/reports?group=money')
  await expect(page.locator('.rg__item')).toHaveCount(8)
  expect(await page.getByRole('button', { name: 'خلاصه مصارف به تفکیک نوع', exact: true }).locator('.rg__name').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await page.locator('.rg').screenshot({ path: '../.artifacts/financial-reports/gallery-desktop.png' })
  for (const [key, name] of financialReports) {
    await openReport(page, 'پول و حساب', name)
    await expect(title(page)).toHaveText(name)
    await expect(page).toHaveURL(new RegExp(`report=${key}`))
    await expect(page.locator('.report-tiles > *')).toHaveCount(4)
    await expect(page.locator('.report-sheet .xr-card')).toHaveCount(1)
    await expect(page.getByRole('group', { name: 'دوره گزارش' })).toBeVisible()
    await expect(bar(page).getByRole('button', { name: 'Excel' })).toBeVisible()
    await expect(bar(page).getByRole('button', { name: 'PDF' })).toBeVisible()
    await expect(page.getByRole('alert')).toHaveCount(0)
    await page.screenshot({ path: `../.artifacts/financial-reports/${key}-desktop.png`, fullPage: true })
    await page.setViewportSize({ width: 375, height: 812 })
    await expect.poll(() => page.locator('.layout__main').evaluate(el => Math.round(parseFloat(getComputedStyle(el).marginInlineStart)))) .toBe(0)
    await expect.poll(() => page.locator('.sidebar').evaluate(el => el.getBoundingClientRect().left >= window.innerWidth)).toBe(true)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: `../.artifacts/financial-reports/${key}-mobile.png`, fullPage: true })
    await page.setViewportSize({ width: 1400, height: 900 })
  }
  await page.goto('/reports?group=money')
  await page.setViewportSize({ width: 375, height: 812 })
  await expect(page.locator('.rg__item')).toHaveCount(8)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  expect(await page.getByRole('button', { name: 'خلاصه مصارف به تفکیک نوع', exact: true }).locator('.rg__name').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await page.locator('.rg').screenshot({ path: '../.artifacts/financial-reports/gallery-mobile.png' })
})

test('a doctor sees no money report, cannot take files, and an address asking for one falls back without asking the server', async ({ browser }) => {
  const page = await userWithRole(browser, 'Doctor')
  const asked: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/api/reports/')) asked.push(new URL(request.url()).pathname)
  })

  await page.goto('/reports?report=cash&period=month')
  await expect(title(page)).toHaveText('گزارش‌ها')
  await expect(page).not.toHaveURL(/report=/)
  await expect(tabs(page).getByRole('tab', { name: 'کار شفاخانه' })).toBeVisible()
  await expect(tabs(page).getByRole('tab', { name: 'پول و حساب' })).toHaveCount(0)
  await expect(tabs(page).getByRole('tab', { name: 'دوا و گدام' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'مراجعات مریضان' })).toBeVisible()

  await page.getByRole('button', { name: 'کار داکتران' }).click()
  await expect(title(page)).toHaveText('کار داکتران')
  await expect(bar(page).getByRole('button', { name: 'Excel' })).toHaveCount(0)
  await expect(bar(page).getByRole('button', { name: 'PDF' })).toHaveCount(0)
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(asked).not.toContain('/api/reports/cash')
})

test('changing the period loads at once and survives a reload', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports?report=doctors')
  await expect(title(page)).toHaveText('کار داکتران')

  const patients = page.waitForResponse((r) => r.url().includes('/api/reports/patients'))
  await openReport(page, 'کار شفاخانه', 'مراجعات مریضان')
  await patients
  await expect(title(page)).toHaveText('مراجعات مریضان')
  await expect(page).toHaveURL(/report=patients/)

  const lastMonth = page.waitForResponse((r) => r.url().includes('/api/reports/patients') && r.url().includes('from='))
  await page.getByRole('button', { name: 'ماه گذشته' }).click()
  const response = await lastMonth
  const asked = new URL(response.url()).searchParams
  expect(asked.get('from')! < asked.get('to')!).toBe(true)
  await expect(page).toHaveURL(/period=last-month/)
  await expect(page.getByRole('button', { name: 'ماه گذشته' })).toHaveAttribute('aria-pressed', 'true')

  await page.reload()
  await expect(title(page)).toHaveText('مراجعات مریضان')
  await expect(page.getByRole('button', { name: 'ماه گذشته' })).toHaveAttribute('aria-pressed', 'true')

  await page.getByRole('button', { name: 'دوره دلخواه' }).click()
  await expect(page.getByRole('textbox', { name: 'از تاریخ' })).toBeVisible()
  const custom = page.waitForResponse((r) => r.url().includes('/api/reports/patients') && r.url().includes('from='))
  await page.getByRole('textbox', { name: 'از تاریخ' }).fill('1405/01/01')
  await page.getByRole('textbox', { name: 'از تاریخ' }).press('Tab')
  await page.getByRole('textbox', { name: 'تا تاریخ' }).fill('1405/01/05')
  await page.getByRole('textbox', { name: 'تا تاریخ' }).press('Tab')
  await page.getByRole('button', { name: 'نمایش', exact: true }).click()
  await custom
  await expect(page).toHaveURL(/period=custom.*from=2026-03-21.*to=2026-03-25/)
  await expect(bar(page).getByText('1 حمل تا 5 حمل 1405')).toBeVisible()
})

test('a late answer never replaces a newer choice', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports?report=doctors')
  await expect(title(page)).toHaveText('کار داکتران')

  await page.route('**/api/reports/patients*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500))
    await route.continue().catch(() => {})
  })
  await openReport(page, 'کار شفاخانه', 'مراجعات مریضان')
  await openReport(page, 'کار شفاخانه', 'عملیات‌ها')
  await expect(title(page)).toHaveText('عملیات‌ها')
  await page.waitForTimeout(2000)
  await expect(title(page)).toHaveText('عملیات‌ها')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a report of the present moment has no period controls', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports?report=stock')
  await expect(title(page)).toHaveText('موجودی گدام')
  await expect(bar(page).getByText(/وضعیت همین حالا/)).toBeVisible()
  await expect(page.getByRole('group', { name: 'دوره گزارش' })).toHaveCount(0)

  await openReport(page, 'کار شفاخانه', 'کار داکتران')
  await expect(page.getByRole('group', { name: 'دوره گزارش' })).toBeVisible()
})

test('a failed report says so and retry brings it back', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.route('**/api/reports/doctors*', (route) => route.fulfill({ status: 500, json: {} }))
  await page.goto('/reports?report=doctors')
  await expect(page.getByRole('alert')).toBeVisible()
  await page.unroute('**/api/reports/doctors*')
  await page.getByRole('button', { name: 'تلاش دوباره' }).click()
  await expect(title(page)).toHaveText('کار داکتران')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('at phone width a report still works and the page never scrolls sideways', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager', { width: 375, height: 760 })
  await page.goto('/reports?report=doctors&period=month')
  await expect(title(page)).toHaveText('کار داکتران')
  await expect(back(page)).toBeVisible()

  await openReport(page, 'پول و حساب', 'دریافت‌ها')
  await expect(title(page)).toHaveText('دریافت‌ها')
  await expect(page).toHaveURL(/report=cash/)
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})
