import { readFile } from 'node:fs/promises'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { call } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

const moneyReports = ['درآمد و مصارف', 'صندوق و دریافت‌ها', 'پول‌های گرفته‌نشده', 'تخفیف، برگشت وجه و باطل']

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

const nav = (page: Page) => page.getByRole('navigation', { name: 'فهرست گزارش‌ها' })
const title = (page: Page) => page.getByRole('heading', { level: 1 })

test('an accountant sees every group and can print and export', async ({ browser }) => {
  const page = await userWithRole(browser, 'Accountant')
  await page.goto('/reports')
  await expect(title(page)).toHaveText('خلاصه کار')
  for (const group of ['کار شفاخانه', 'پول و حساب', 'دوا و گدام']) await expect(nav(page).getByRole('heading', { name: group })).toBeVisible()
  for (const name of moneyReports) await expect(nav(page).getByRole('button', { name })).toBeVisible()
  await expect(page.getByRole('button', { name: 'چاپ' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'خروجی اکسل' })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a doctor sees no money report, and an address asking for one falls back without asking the server', async ({ browser }) => {
  const page = await userWithRole(browser, 'Doctor')
  const asked: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/api/reports/')) asked.push(new URL(request.url()).pathname)
  })

  await page.goto('/reports?report=income&period=month')
  await expect(title(page)).toHaveText('خلاصه کار')
  await expect(page).toHaveURL(/report=overview/)
  await expect(nav(page).getByRole('button', { name: 'کار داکتران' })).toBeVisible()
  for (const name of moneyReports) await expect(nav(page).getByRole('button', { name })).toHaveCount(0)
  await expect(nav(page).getByRole('heading', { name: 'پول و حساب' })).toHaveCount(0)
  await expect(nav(page).getByRole('heading', { name: 'دوا و گدام' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'چاپ' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'خروجی اکسل' })).toHaveCount(0)
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(asked).not.toContain('/api/reports/income')
})

test('changing the report or the period loads at once and survives a reload', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(title(page)).toHaveText('خلاصه کار')

  const patients = page.waitForResponse((r) => r.url().includes('/api/reports/patients'))
  await nav(page).getByRole('button', { name: 'مریضان و مراجعات' }).click()
  await patients
  await expect(title(page)).toHaveText('مریضان و مراجعات')
  await expect(page).toHaveURL(/report=patients/)

  const lastMonth = page.waitForResponse((r) => r.url().includes('/api/reports/patients') && r.url().includes('from='))
  await page.getByRole('button', { name: 'ماه گذشته' }).click()
  const response = await lastMonth
  const asked = new URL(response.url()).searchParams
  expect(asked.get('from')! < asked.get('to')!).toBe(true)
  await expect(page).toHaveURL(/period=last-month/)
  await expect(page.getByRole('button', { name: 'ماه گذشته' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('heading', { name: 'مراجعات هر روز' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'مراجعات روز به روز' })).toBeVisible()

  await page.reload()
  await expect(title(page)).toHaveText('مریضان و مراجعات')
  await expect(page.getByRole('button', { name: 'ماه گذشته' })).toHaveAttribute('aria-pressed', 'true')

  await page.getByRole('button', { name: 'دوره دلخواه' }).click()
  await expect(page.getByRole('textbox', { name: 'از تاریخ' })).toBeVisible()
  const custom = page.waitForResponse((r) => r.url().includes('/api/reports/patients') && r.url().includes('from='))
  await page.getByRole('textbox', { name: 'از تاریخ' }).fill('1405/01/01')
  await page.getByRole('textbox', { name: 'تا تاریخ' }).fill('1405/01/05')
  await page.getByRole('button', { name: 'نمایش', exact: true }).click()
  await custom
  await expect(page).toHaveURL(/period=custom.*from=2026-03-21.*to=2026-03-25/)
  await expect(page.getByText('۱ حمل تا ۵ حمل ۱۴۰۵')).toBeVisible()
})

test('a late answer never replaces a newer choice', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(title(page)).toHaveText('خلاصه کار')

  await page.route('**/api/reports/patients*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500))
    await route.continue().catch(() => {})
  })
  await nav(page).getByRole('button', { name: 'مریضان و مراجعات' }).click()
  await nav(page).getByRole('button', { name: 'عملیات' }).click()
  await expect(title(page)).toHaveText('عملیات')
  await page.waitForTimeout(2000)
  await expect(title(page)).toHaveText('عملیات')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a report of the present moment has no period controls', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports?report=stock')
  await expect(title(page)).toHaveText('موجودی گدام')
  await expect(page.getByText('وضعیت همین حالا')).toBeVisible()
  await expect(page.getByRole('group', { name: 'دوره گزارش' })).toHaveCount(0)

  await nav(page).getByRole('button', { name: 'خلاصه کار' }).click()
  await expect(page.getByRole('group', { name: 'دوره گزارش' })).toBeVisible()
})

test('a failed report says so and retry brings it back', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.route('**/api/reports/overview*', (route) => route.fulfill({ status: 500, json: {} }))
  await page.goto('/reports')
  await expect(page.getByRole('alert')).toBeVisible()
  await page.unroute('**/api/reports/overview*')
  await page.getByRole('button', { name: 'تلاش دوباره' }).click()
  await expect(title(page)).toHaveText('خلاصه کار')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('export is a BOM-marked CSV with formulas neutralised and numbers left as numbers', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.route('**/api/reports/income*', (route) =>
    route.fulfill({
      json: {
        title: 'درآمد و مصارف',
        description: 'شرح',
        from: '2026-03-21',
        to: '2026-03-25',
        tiles: [{ label: 'نتیجه دوره', value: -250.5, kind: 'money' }],
        trend: null,
        tables: [
          {
            title: 'حساب, دوره',
            columns: [{ label: 'شرح', kind: 'text' }, { label: 'تاریخ', kind: 'date' }, { label: 'مبلغ', kind: 'money' }],
            rows: [
              ['=HYPERLINK("http://x")', '2026-03-21', -100],
              ['+1', null, 200],
              ['@sum', null, 0],
              ['-2', null, 1],
              ['\tcmd', null, 1],
            ],
            total: ['مجموع', null, 102],
            note: null,
          },
          {
            title: 'روز به روز',
            columns: [{ label: 'تاریخ', kind: 'date' }, { label: 'تعداد', kind: 'count' }],
            rows: [['2026-03-22', 5]],
            total: ['همه', 5],
            note: null,
          },
        ],
      },
    }),
  )
  await page.goto('/reports?report=income&period=today')
  await expect(page.getByText('نتیجه دوره')).toBeVisible()

  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'خروجی اکسل' }).click()])
  expect(download.suggestedFilename()).toBe('report-income-1405-01-01.csv')
  const bytes = await readFile(await download.path())
  expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf])

  const lines = bytes.toString('utf8').slice(1).split('\r\n')
  expect(lines[0]).toBe('درآمد و مصارف')
  expect(lines[1]).toBe('۱ حمل تا ۵ حمل ۱۴۰۵')
  expect(lines).toContain('نتیجه دوره,-250.5')
  expect(lines).toContain('"حساب, دوره"')
  expect(lines).toContain(`"'=HYPERLINK(""http://x"")",1405/01/01,-100`)
  expect(lines).toContain(`'+1,,200`)
  expect(lines).toContain(`'@sum,,0`)
  expect(lines).toContain(`'-2,,1`)
  expect(lines).toContain(`'\tcmd,,1`)
  expect(lines).toContain('مجموع,,102')
  expect(lines).toContain('1405/01/02,5')
  expect(lines).toContain('همه,5')
})

test('at phone width a select replaces the list and the page never scrolls sideways', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager', { width: 375, height: 760 })
  await page.goto('/reports?period=month')
  await expect(title(page)).toHaveText('خلاصه کار')
  await expect(nav(page)).toBeHidden()

  await page.getByLabel('گزارش', { exact: true }).selectOption('cash')
  await expect(title(page)).toHaveText('صندوق و دریافت‌ها')
  await expect(page).toHaveURL(/report=cash/)
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})
