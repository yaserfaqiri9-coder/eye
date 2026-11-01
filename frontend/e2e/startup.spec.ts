import { expect, test } from '@playwright/test'

// None of these failures may ever leave the user looking at an empty page.

test('shows a message and a working retry when the API cannot be reached', async ({ page }) => {
  let blocked = true
  await page.route('**/api/auth/me', (route) => (blocked ? route.abort() : route.continue()))

  await page.goto('/')
  await expect(page.getByRole('alert')).toContainText('سرور در دسترس نیست')

  blocked = false
  await page.getByRole('button', { name: 'تلاش دوباره' }).click()
  await expect(page.getByRole('button', { name: 'ورود' })).toBeVisible()
})

test('shows a message when the API answers with a server error', async ({ page }) => {
  await page.route('**/api/auth/me', (route) => route.fulfill({ status: 500, body: '' }))
  await page.goto('/')
  await expect(page.getByRole('alert')).toContainText('سرور در دسترس نیست')
})

test('shows a message when a page file fails to load', async ({ page }) => {
  await page.route('**/LoginPage.vue*', (route) => route.abort())
  await page.goto('/login')
  await expect(page.getByRole('alert')).toContainText('بارگذاری این صفحه انجام نشد')
  await expect(page.getByRole('button', { name: 'تلاش دوباره' })).toBeVisible()
})

test('shows a message when the application bundle itself fails to load', async ({ page }) => {
  await page.route('**/src/main.ts*', (route) => route.abort())
  await page.goto('/')
  await expect(page.locator('#boot')).toContainText('سیستم بارگذاری نشد')
  await expect(page.getByRole('button', { name: 'تلاش دوباره' })).toBeVisible()
})

test('shows a loading message while the API is slow, then the page', async ({ page }) => {
  await page.route('**/api/auth/me', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500))
    await route.continue()
  })
  await page.goto('/', { waitUntil: 'commit' })
  await expect(page.locator('#boot')).toContainText('در حال بارگذاری')
  await expect(page.getByRole('button', { name: 'ورود' })).toBeVisible({ timeout: 10_000 })
})
