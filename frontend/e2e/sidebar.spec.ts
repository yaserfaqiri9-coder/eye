import { expect, test, type Page } from '@playwright/test'

// Layout only, so the API is answered here: the /api/ pattern must not also catch the /src/api/ modules.

const permissions = ['visits.view', 'patients.view', 'surgery.view', 'reports.view', 'users.view', 'audit.view']

async function openPatients(page: Page, mini: boolean) {
  await page.addInitScript((value) => localStorage.setItem('alshifa.sidebar.mini', value), mini ? '1' : '0')
  await page.route(/^https?:\/\/[^/]+\/api\//, (route) =>
    route.request().url().includes('/auth/me')
      ? route.fulfill({ json: { id: 1, userName: 'admin', fullName: 'مدیر', roles: ['Admin'], permissions } })
      : route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 20 } }),
  )
  await page.route(/^https?:\/\/[^/]+\/hubs\//, (route) => route.abort())
  await page.setViewportSize({ width: 1600, height: 900 })
  await page.goto('/patients')
  await page.locator('.sidebar__link').first().waitFor()
  await page.mouse.move(600, 500)
}

/** Waits for the width transition, so a width read mid-animation cannot pass or fail by timing. */
const sidebarWidth = (page: Page, width: number) =>
  expect.poll(() => page.locator('.sidebar').evaluate((el) => el.getBoundingClientRect().width)).toBe(width)

test('collapsing closes the menu at once, with the pointer still on the button', async ({ page }) => {
  await openPatients(page, false)
  await sidebarWidth(page, 260)

  await page.getByRole('button', { name: 'جمع کردن منو' }).click()

  await sidebarWidth(page, 80)
  await expect(page.locator('.layout__main')).toHaveCSS('margin-right', '80px')
})

test('the page makes room for the slid-out menu and takes the space back', async ({ page }) => {
  await openPatients(page, true)
  await sidebarWidth(page, 80)

  await page.locator('.sidebar').hover()

  await sidebarWidth(page, 260)
  await expect(page.locator('.sidebar')).not.toHaveCSS('background-image', 'none')
  await expect(page.locator('.sidebar__link', { hasText: 'مریضان' })).toBeVisible()
  await expect(page.locator('.layout__main')).toHaveCSS('margin-right', '260px')

  await page.mouse.move(600, 500)
  await sidebarWidth(page, 80)
  await expect(page.locator('.layout__main')).toHaveCSS('margin-right', '80px')
})

test('the page and the menu move together, frame by frame', async ({ page }) => {
  await openPatients(page, true)
  await sidebarWidth(page, 80)

  const recording = page.evaluate(
    () =>
      new Promise<number[]>((resolve) => {
        const sidebar = document.querySelector('.sidebar')!
        const main = document.querySelector('.layout__main')!
        const gaps: number[] = []
        const started = performance.now()
        const frame = () => {
          gaps.push(Math.abs(sidebar.getBoundingClientRect().left - main.getBoundingClientRect().right))
          if (performance.now() - started < 600) requestAnimationFrame(frame)
          else resolve(gaps)
        }
        requestAnimationFrame(frame)
      }),
  )
  await page.locator('.sidebar').hover()

  expect(Math.max(...(await recording))).toBeLessThanOrEqual(2)
})

test('the button keeps the menu open and says so', async ({ page }) => {
  await openPatients(page, true)
  await page.locator('.sidebar').hover()

  await page.getByRole('button', { name: 'باز نگه داشتن منو' }).click()
  await page.mouse.move(600, 500)

  await sidebarWidth(page, 260)
  await expect(page.locator('.layout__main')).toHaveCSS('margin-right', '260px')
  await expect(page.getByRole('button', { name: 'جمع کردن منو' })).toBeVisible()
})

test('the keyboard opens the collapsed menu and can reach the button', async ({ page }) => {
  await openPatients(page, true)
  await sidebarWidth(page, 80)

  await page.keyboard.press('Tab')
  await sidebarWidth(page, 260)

  await page.keyboard.press('Shift+Tab')
  await expect(page.getByRole('button', { name: 'باز نگه داشتن منو' })).toBeFocused()
  await sidebarWidth(page, 260)
})

test('a clicked link does not hold the collapsed menu open', async ({ page }) => {
  await openPatients(page, true)
  await page.locator('.sidebar').hover()

  await page.locator('.sidebar__link').first().click()
  await page.mouse.move(600, 500)

  await sidebarWidth(page, 80)
})

test('touch screens keep the full menu even when collapsing was saved earlier', async ({ browser }) => {
  const context = await browser.newContext({ hasTouch: true, isMobile: true, viewport: { width: 1600, height: 900 } })
  const page = await context.newPage()
  await openPatients(page, true)

  await sidebarWidth(page, 260)
  await expect(page.locator('.layout__main')).toHaveCSS('margin-right', '260px')
  await expect(page.locator('.sidebar__toggle')).toBeHidden()

  await context.close()
})
