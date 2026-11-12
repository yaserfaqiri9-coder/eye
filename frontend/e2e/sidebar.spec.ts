import { expect, test, type Page } from '@playwright/test'

test.use({ channel: 'chromium' })

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
  await page.goto('/patients', { waitUntil: 'domcontentloaded' })
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

  const pin = page.getByRole('button', { name: 'باز نگه داشتن منو' })
  for (let i = 0; i < 20 && !(await pin.evaluate(el => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  await expect(pin).toBeFocused()
  await sidebarWidth(page, 260)
})

test('a clicked link does not hold the collapsed menu open', async ({ page }) => {
  await openPatients(page, true)
  await page.locator('.sidebar').hover()

  await page.locator('.sidebar__link').first().click()
  await page.mouse.move(600, 500)

  await sidebarWidth(page, 80)
})

/** Everyone who works in several departments, so the submenus are not reduced to single links. */
const allPermissions = [
  'visits.view', 'patients.view', 'preexam.edit', 'consultation.edit', 'diagnostics.edit', 'surgery.view',
  'pharmacy.create', 'optical.view', 'inventory.view', 'payments.create', 'invoices.view',
  'calls.manage', 'reports.view', 'settings.view', 'users.view', 'audit.view',
]

async function openAs(page: Page, path: string, granted: string[], mini = false) {
  await page.addInitScript((value) => localStorage.setItem('alshifa.sidebar.mini', value), mini ? '1' : '0')
  await page.route(/^https?:\/\/[^/]+\/api\//, (route) =>
    route.request().url().includes('/auth/me')
      ? route.fulfill({ json: { id: 1, userName: 'admin', fullName: 'مدیر', roles: ['Admin'], permissions: granted } })
      : route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 20 } }),
  )
  await page.route(/^https?:\/\/[^/]+\/hubs\//, (route) => route.abort())
  await page.setViewportSize({ width: 1600, height: 900 })
  await page.goto(path, { waitUntil: 'domcontentloaded' })
  await page.locator('.sidebar__link').first().waitFor()
  await page.mouse.move(900, 500)
}

const item = (page: Page, label: string) => page.locator('.sidebar__item', { hasText: label })
const parentButton = (page: Page, label: string) => page.getByRole('button', { name: label, exact: true })
const parentItem = (page: Page, label: string) => parentButton(page, label).locator('..')

test('management is reduced to three task-oriented top-level entries', async ({ page }) => {
  await openAs(page, '/staff', allPermissions)

  await expect(page.locator('.sidebar__brand')).toHaveCount(0)
  await expect(page.locator('.sidebar__title')).toHaveCount(0)
  await expect(page.locator('.sidebar__link[href="/reports"]')).toHaveText('گزارش‌ها')
  await expect(parentButton(page, 'مدیریت سازمان')).toHaveAttribute('aria-expanded', 'true')
  await expect(parentItem(page, 'مدیریت سازمان').locator('.sidebar__submenu .sidebar__link')).toHaveText([
    'فهرست داکتران',
    'مدیریت بخش‌ها',
    'تأمین‌کنندگان',
    'کارمندان',
  ])
  const organizationLinks = parentItem(page, 'مدیریت سازمان').locator('.sidebar__submenu .sidebar__link')
  expect(await organizationLinks.evaluateAll((links) => links.map((link) => link.getAttribute('href')))).toEqual([
    '/doctors',
    '/departments',
    '/suppliers',
    '/staff',
  ])
})

test('system management keeps its four routes and opens for an active user page', async ({ page }) => {
  await openAs(page, '/users', allPermissions)

  await expect(parentButton(page, 'مدیریت سیستم')).toHaveAttribute('aria-expanded', 'true')
  const links = parentItem(page, 'مدیریت سیستم').locator('.sidebar__submenu .sidebar__link')
  await expect(links).toHaveText(['مرکز پخش صوت', 'تنظیمات', 'کاربران', 'ثبت فعالیت‌ها'])
  expect(await links.evaluateAll((items) => items.map((link) => link.getAttribute('href')))).toEqual([
    '/audio-station',
    '/settings',
    '/users',
    '/audit',
  ])
  await expect(page.getByRole('link', { name: 'کاربران' })).toHaveAttribute('aria-current', 'page')
})

test('management parents still filter permissions and flatten one authorized child', async ({ page }) => {
  await openAs(page, '/', ['inventory.view', 'calls.manage'])

  await expect(parentButton(page, 'مدیریت سازمان')).toHaveCount(0)
  await expect(parentButton(page, 'مدیریت سیستم')).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'تأمین‌کنندگان' })).toHaveAttribute('href', '/suppliers')
  await expect(page.getByRole('link', { name: 'مرکز پخش صوت' })).toHaveAttribute('href', '/audio-station')
  await expect(page.locator('.sidebar__link[href="/staff"]')).toHaveCount(0)
  await expect(page.locator('.sidebar__link[href="/users"]')).toHaveCount(0)
})

test('rows start without a brand header, use filled icons and more readable labels', async ({ page }) => {
  await openAs(page, '/patients', allPermissions)
  await expect(page.locator('.sidebar__brand')).toHaveCount(0)
  await expect(page.locator('.sidebar__title')).toHaveCount(0)
  const link = page.getByRole('link', { name: 'گزارش‌ها' })
  await expect(link).toHaveCSS('height', '60px')
  await expect(link).toHaveCSS('font-size', '17px')
  await expect(link).toHaveCSS('font-weight', '500')
  await expect(link).toHaveCSS('color', 'rgb(255, 255, 255)')
  await expect(link.locator('i')).toHaveClass(/-fill/)
  await expect(link.locator('i')).toHaveCSS('font-size', '24px')
  const first = await page.locator('.sidebar__link').first().boundingBox()
  expect(first!.y).toBe(24)
  await page.screenshot({ path: '../docs/sidebar-reference/desktop.png', fullPage: true })
  await page.locator('.sidebar').screenshot({ path: '../docs/sidebar-reference/sidebar.png' })
  await link.hover()
  await expect(link).toHaveCSS('background-color', 'rgba(255, 255, 255, 0.12)')
})

test('active row and its hover restore the white card on the teal sidebar', async ({ page }) => {
  await openAs(page, '/patients', allPermissions)
  const active = item(page, 'مریضان').last()
  await expect(active).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(active).toHaveCSS('border-radius', '25px')
  await expect(active).toHaveCSS('margin-bottom', '12px')
  const link = page.getByRole('link', { name: 'مریضان', exact: true })
  await expect(link).toHaveCSS('color', 'rgb(8, 155, 171)')
  await expect(link).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await link.hover()
  await expect(link).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(link).toHaveAttribute('aria-current', 'page')
})

test('open submenus restore their white container and teal selected child', async ({ page }) => {
  await openAs(page, '/lab', allPermissions)

  await expect(parentButton(page, 'بخش‌های طبی')).toHaveAttribute('aria-expanded', 'true')
  await expect(parentButton(page, 'بخش‌های طبی')).toHaveCSS('color', 'rgb(8, 155, 171)')
  await expect(item(page, 'بخش‌های طبی')).toHaveCSS('background-color', 'rgb(255, 255, 255)')

  const activeChild = page.getByRole('link', { name: 'آزمایشگاه' })
  await expect(activeChild).toHaveCSS('height', '52px')
  await expect(activeChild).toHaveCSS('font-size', '15px')
  await expect(activeChild.locator('i')).toHaveClass(/-fill/)
  await expect(activeChild).toHaveCSS('background-color', 'rgb(206, 235, 238)')
  await expect(activeChild).toHaveCSS('color', 'rgb(8, 155, 171)')
  await expect(activeChild).toHaveCSS('border-radius', '0px')
  await page.locator('.sidebar').screenshot({ path: '../docs/sidebar-reference/submenu.png' })

  await expect(page.getByRole('link', { name: 'عملیات' })).toHaveCSS('color', 'rgb(119, 125, 116)')
  await expect(page.getByRole('link', { name: 'عملیات' }).locator('i')).toHaveCSS('font-size', '22px')
})

test('submenus open independently, and the active one can still be closed', async ({ page }) => {
  await openAs(page, '/lab', allPermissions)

  await parentButton(page, 'مالی').click()
  await expect(page.getByRole('link', { name: 'صندوق' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'آزمایشگاه' })).toBeVisible()

  await parentButton(page, 'بخش‌های طبی').click()
  await expect(page.getByRole('link', { name: 'آزمایشگاه' })).toBeHidden()
  // Closed, it still marks where the user is.
  await expect(item(page, 'بخش‌های طبی')).toHaveCSS('background-color', 'rgb(255, 255, 255)')
})

test('going to a page leaves only its own submenu open', async ({ page }) => {
  await openAs(page, '/lab', allPermissions)

  await parentButton(page, 'مالی').click()
  await page.getByRole('link', { name: 'صندوق' }).click()

  await expect(parentButton(page, 'مالی')).toHaveAttribute('aria-expanded', 'true')
  await expect(parentButton(page, 'بخش‌های طبی')).toHaveAttribute('aria-expanded', 'false')
})

test('the keyboard skips the links of a closed submenu', async ({ page }) => {
  await openAs(page, '/', allPermissions)

  await parentButton(page, 'مالی').focus()
  await page.keyboard.press('Tab')

  await expect(page.getByRole('link', { name: 'گزارش‌ها' })).toBeFocused()
})

test('a role with one page in a submenu gets a direct link', async ({ page }) => {
  await openAs(page, '/', ['diagnostics.edit', 'visits.view'])

  await expect(page.getByRole('link', { name: 'آزمایشگاه' })).toBeVisible()
  await expect(parentButton(page, 'بخش‌های طبی')).toHaveCount(0)
  await expect(page.locator('.sidebar__title', { hasText: 'مدیریت' })).toHaveCount(0)
})

test('collapsed sidebar keeps accessible route icons without a logo or section labels', async ({ page }) => {
  await openAs(page, '/lab', allPermissions, true)
  await sidebarWidth(page, 80)

  await expect(page.locator('.sidebar__brand, .sidebar__title')).toHaveCount(0)

  const activeChild = page.getByRole('link', { name: 'آزمایشگاه' })
  await expect(activeChild.locator('i')).toBeVisible()
  await expect(activeChild.locator('span')).toBeHidden()
  await expect(activeChild).toHaveCSS('background-color', 'rgb(206, 235, 238)')
  await expect(item(page, 'بخش‌های طبی')).toHaveCSS('width', '56px')
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

test('pages an administrator hid for the user stay out of the menu', async ({ page }) => {
  await page.route(/^https?:\/\/[^/]+\/api\//, (route) =>
    route.request().url().includes('/auth/me')
      ? route.fulfill({
          json: { id: 1, userName: 'doc', fullName: 'داکتر', roles: ['Manager'], permissions, hiddenMenu: ['/reception', '/reports'] },
        })
      : route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 20 } }),
  )
  await page.route(/^https?:\/\/[^/]+\/hubs\//, (route) => route.abort())
  await page.setViewportSize({ width: 1600, height: 900 })
  await page.goto('/patients', { waitUntil: 'domcontentloaded' })
  await page.locator('.sidebar__link').first().waitFor()

  const sidebar = page.locator('.sidebar')
  await expect(sidebar.getByRole('link', { name: 'مریضان' })).toBeVisible()
  await expect(sidebar.getByRole('link', { name: 'پذیرش و صف' })).toHaveCount(0)
  await expect(sidebar.getByRole('link', { name: 'گزارش‌ها' })).toHaveCount(0)
})


test('mobile sidebar opens and closes without a logo and keeps all authorized navigation', async ({ page }) => {
  await openAs(page, '/patients', allPermissions)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('button', { name: 'باز کردن منو', exact: true }).click()
  await expect(page.locator('.sidebar')).toHaveClass(/sidebar--open/)
  await expect.poll(() => page.locator('.sidebar').evaluate(el => Math.round(el.getBoundingClientRect().right))).toBe(390)
  await expect(page.locator('.sidebar__brand')).toHaveCount(0)
  await expect(page.locator('.sidebar').getByRole('link', { name: 'مریضان', exact: true })).toBeVisible()
  await page.screenshot({ path: '../docs/sidebar-reference/mobile.png', fullPage: true })
  await page.getByRole('button', { name: 'بستن منو', exact: true }).click()
  await expect(page.locator('.sidebar')).not.toHaveClass(/sidebar--open/)
})
