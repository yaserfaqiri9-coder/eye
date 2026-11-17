import { expect, test } from '@playwright/test'
import { setup } from './inventory-reference-fixture.mjs'

test.use({ channel: 'chromium', viewport: { width: 1440, height: 1000 } })

const list = (page: import('@playwright/test').Page) => page.getByRole('region', { name: 'فهرست اجناس' })

test('the inventory uses one flat row of tabs and the same table, badges, search and pager as other lists', async ({ page }) => {
  const problems = await setup(page)
  await page.goto('/inventory', { waitUntil: 'domcontentloaded' })

  const tabs = page.getByRole('tablist', { name: 'بخش‌های گدام' })
  await expect(tabs.getByRole('tab')).toHaveText(['محصولات', 'گدام‌ها'])
  // No second row of tabs inside a tab.
  await expect(page.getByRole('tablist')).toHaveCount(1)

  const table = list(page).locator('table.xr-table')
  await expect(table.locator('tbody tr')).toHaveCount(20)
  await expect(table.getByRole('columnheader')).toHaveText(['نام محصول', 'نوعیت', 'مقدار', 'قیمت خرید', 'قیمت فروش', 'تاریخ انقضا', ''])
  const first = table.getByRole('row', { name: /Vigamox/ })
  await expect(first.getByRole('button', { name: 'انتقال', exact: true })).toBeVisible()
  await expect(first.getByRole('button', { name: 'تعدیل', exact: true })).toBeVisible()
  await expect(page.getByText('108 جنس', { exact: true })).toBeVisible()
  await expect(table.getByText('320', { exact: true })).toBeVisible()
  await expect(table.getByText('1,234.125', { exact: true })).toBeVisible()
  await expect(table.locator('.xr-badge', { hasText: 'ناموجود' })).toHaveCount(1)

  await page.getByRole('button', { name: 'بعدی', exact: true }).click()
  await expect(table.getByText('جنس آزمایشی 21', { exact: true })).toBeVisible()

  await page.getByRole('searchbox', { name: 'جستجوی نام یا کد جنس' }).fill('DR-108')
  await expect(table.locator('tbody tr')).toHaveCount(1)
  await page.getByRole('searchbox', { name: 'جستجوی نام یا کد جنس' }).fill('')

  await page.getByRole('combobox', { name: 'فیلتر محل', exact: true }).selectOption({ label: 'دواخانه' })
  await expect(table.locator('tbody tr')).toHaveCount(1)
  await expect(table.getByText('DR-108', { exact: true })).toBeVisible()
  await page.getByRole('combobox', { name: 'فیلتر محل', exact: true }).selectOption('')

  await page.getByRole('combobox', { name: 'فیلتر وضعیت', exact: true }).selectOption('out')
  await expect(table.locator('tbody tr')).toHaveCount(1)
  await expect(table.getByText('پماد چشمی', { exact: true })).toBeVisible()
  // Out of stock: nothing to move or count.
  await expect(table.getByRole('button', { name: 'انتقال', exact: true })).toHaveCount(0)

  await page.getByRole('combobox', { name: 'فیلتر وضعیت', exact: true }).selectOption('expired')
  await expect(table.locator('tbody tr')).toHaveCount(1)
  await expect(table.getByText('Timolol', { exact: true })).toBeVisible()
  await expect(table.locator('.xr-badge--red')).toBeVisible()
  await page.getByRole('combobox', { name: 'فیلتر وضعیت', exact: true }).selectOption('')

  await page.getByRole('button', { name: 'عملیات Vigamox' }).click()
  await page.getByRole('menuitem', { name: 'ویرایش جنس' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(page.getByLabel('کد', { exact: true })).toHaveValue('DR-001')
  await page.getByRole('button', { name: 'انصراف', exact: true }).click()

  await tabs.getByRole('tab', { name: 'گدام‌ها' }).click()
  await expect(page.getByRole('link', { name: 'گدام مرکزی', exact: true })).toBeVisible()
  expect(problems).toEqual([])
})

test('the inventory list stays within a mobile viewport and empty searches are explicit', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const problems = await setup(page)
  await page.goto('/inventory', { waitUntil: 'domcontentloaded' })
  await expect(list(page).getByText('Vigamox', { exact: true })).toBeVisible()
  const widths = await page.evaluate(() => ({ actual: document.documentElement.scrollWidth, viewport: innerWidth }))
  expect(widths.actual).toBeLessThanOrEqual(widths.viewport)
  await page.getByRole('searchbox', { name: 'جستجوی نام یا کد جنس' }).fill('does-not-exist')
  await expect(page.getByText('هیچ جنسی با این جستجو پیدا نشد.')).toBeVisible()
  expect(problems).toEqual([])
})

test('a store shows each product with its prices, expiry and row actions, and its stock history in a tab', async ({ page }) => {
  const problems = await setup(page)
  await page.goto('/inventory/locations/central', { waitUntil: 'domcontentloaded' })
  const stock = page.getByRole('region', { name: 'موجودی فعلی' })
  await expect(stock.locator('tbody tr')).toHaveCount(107)
  await expect(stock.getByRole('columnheader')).toHaveText(['نام محصول', 'نوعیت', 'مقدار', 'قیمت خرید', 'قیمت فروش', 'تاریخ انقضا', ''])
  const row = stock.getByRole('row', { name: /Timolol/ })
  await expect(row.locator('.xr-badge--red')).toBeVisible()
  await expect(row.getByRole('button', { name: 'انتقال', exact: true })).toBeVisible()
  await expect(row.getByRole('button', { name: 'تعدیل', exact: true })).toBeVisible()

  const tabs = page.getByRole('tablist', { name: 'بخش‌های گدام' })
  await expect(tabs.getByRole('tab')).toHaveText(['موجودی', 'تاریخچه ورود و خروج'])
  await tabs.getByRole('tab', { name: 'تاریخچه ورود و خروج' }).click()
  await expect(page.getByText('گردشی ثبت نشده است.')).toBeVisible()
  expect(problems).toEqual([])
})

test('purchases have their own page under pharmacy and optical in the sidebar', async ({ page }) => {
  const problems = await setup(page)
  await page.goto('/purchases', { waitUntil: 'domcontentloaded' })
  const link = page.getByRole('link', { name: 'خرید', exact: true })
  await expect(link).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('heading', { name: 'خریدها' })).toBeVisible()
  await expect(page.getByText('خریدی ثبت نشده است.')).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'فیلتر گدام' })).toContainText('گدام مرکزی')
  await expect(page.getByRole('link', { name: 'موجودی اول دوره' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'ثبت خرید' })).toHaveAttribute('href', '/purchases/new')
  expect(problems).toEqual([])
})
