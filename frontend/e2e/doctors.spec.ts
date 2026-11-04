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

test('doctors are registered and edited from the doctors list, not from the staff page', async ({ page }) => {
  const name = `داکتر ${Date.now()}`
  await login(page)

  await page.goto('/doctors')
  await page.getByRole('button', { name: 'ثبت داکتر' }).click()
  const form = page.getByRole('dialog', { name: 'ثبت داکتر' })
  await form.getByLabel('نام کامل').fill(name)
  await form.getByLabel('تخصص').fill('چشم')
  await form.getByRole('button', { name: 'ذخیره' }).click()
  await expect(form).toBeHidden()
  const card = page.getByRole('article').filter({ hasText: name })
  await expect(card).toContainText('چشم')

  await card.getByRole('button', { name: `ویرایش ${name}` }).click()
  const edit = page.getByRole('dialog', { name: 'ویرایش داکتر' })
  await edit.getByLabel('تخصص').fill('شبکیه')
  await edit.getByRole('button', { name: 'ذخیره' }).click()
  await expect(edit).toBeHidden()
  await expect(card).toContainText('شبکیه')

  await page.goto('/staff')
  await expect(page.getByRole('heading', { name: 'کارمندان', exact: true })).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: name })).toHaveCount(0)
  await page.getByRole('button', { name: 'کارمند جدید' }).click()
  await expect(page.getByRole('dialog').getByLabel('داکتر است')).toHaveCount(0)
})
