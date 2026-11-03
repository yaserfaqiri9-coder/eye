import { expect, test, type Page } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!

async function login(page: Page) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(adminUser)
  await page.getByLabel('رمز عبور').fill(adminPassword)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

const style = (page: Page, label: string) =>
  page.getByLabel(label).evaluate((el) => {
    const s = getComputedStyle(el)
    return { background: s.backgroundColor, border: s.borderTopColor, radius: s.borderTopLeftRadius, height: s.height }
  })

test('fields are white with the template border; only the header search stays tinted', async ({ page }) => {
  await login(page)
  await page.goto('/users')
  await page.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('نام کامل').focus()

  expect(await style(dialog as unknown as Page, 'نام کاربری')).toEqual({ background: 'rgb(255, 255, 255)', border: 'rgb(215, 219, 218)', radius: '10px', height: '45px' })
  // The border animates into its focus colour.
  await expect.poll(async () => (await style(dialog as unknown as Page, 'نام کامل')).border).toBe('rgb(8, 155, 171)')
  expect((await style(page, 'جستجوی مریض در هدر')).background).toBe('rgb(206, 235, 238)')
})

test('a dialog takes focus, keeps Tab inside, and never drops typed values silently', async ({ page }) => {
  await login(page)
  await page.goto('/users')
  const opener = page.getByRole('button', { name: 'کاربر جدید' })
  await opener.click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByLabel('نام کاربری')).toBeFocused()

  for (let i = 0; i < 25; i++) await page.keyboard.press('Tab')
  expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true)

  // Untouched: Esc closes at once and focus returns to the button that opened it.
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(opener).toBeFocused()

  // Edited: Esc, the backdrop, the close icon and Cancel all ask first.
  await opener.click()
  await dialog.getByLabel('نام کامل').fill('نگه‌داشته‌شود')
  const confirm = dialog.getByRole('alert')
  await page.keyboard.press('Escape')
  await expect(confirm).toContainText('ذخیره نشده')
  await dialog.getByRole('button', { name: 'ادامه ویرایش' }).click()
  await page.mouse.click(5, 5)
  await expect(confirm).toBeVisible()
  await dialog.getByRole('button', { name: 'ادامه ویرایش' }).click()
  await dialog.getByRole('button', { name: 'انصراف' }).click()
  await expect(confirm).toBeVisible()
  await dialog.getByRole('button', { name: 'ادامه ویرایش' }).click()
  await expect(dialog.getByLabel('نام کامل')).toHaveValue('نگه‌داشته‌شود')

  await dialog.getByRole('button', { name: 'بستن', exact: true }).click()
  await dialog.getByRole('button', { name: 'بستن بدون ذخیره' }).click()
  await expect(dialog).toBeHidden()
})

test('a double click on save creates one record and shows a busy button meanwhile', async ({ page }) => {
  await login(page)
  await page.goto('/users')
  await page.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = page.getByRole('dialog')
  const userName = `dbl_${Date.now()}`
  await dialog.getByLabel('نام کاربری').fill(userName)
  await dialog.getByLabel('نام کامل').fill('آزمون کلیک دوباره')
  await dialog.getByLabel(/رمز عبور/).fill('E2e-Password-123')
  await dialog.getByLabel('پذیرش', { exact: true }).check()

  // Holds the response so the saving state can be seen; the dialog must refuse to close during it.
  await page.route('**/api/users', async (route) => {
    if (route.request().method() === 'POST') await new Promise((resolve) => setTimeout(resolve, 500))
    await route.continue()
  })
  const posts: string[] = []
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().endsWith('/api/users')) posts.push(request.url())
  })

  const save = dialog.getByRole('button', { name: 'ذخیره' })
  await save.dblclick()
  await expect(save).toBeDisabled()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeVisible()
  await expect(dialog).toBeHidden()

  expect(posts).toHaveLength(1)
  await page.getByLabel('جستجو', { exact: true }).fill(userName)
  await page.getByRole('button', { name: 'جستجو' }).click()
  await expect(page.getByRole('row', { name: new RegExp(userName) })).toHaveCount(1)
})
