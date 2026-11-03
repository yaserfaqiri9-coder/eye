import { expect, test, type Page } from '@playwright/test'
import { call, collectFee, ensureTariffs, openDesk } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

test('searchable field: typing, keyboard, tag, clearing, no results, and only real choices', async ({ browser, page }) => {
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await call(page, 'POST', '/users', { userName: `cb_${stamp}`, fullName: 'کارمند فهرست', password, roles: ['Receptionist'] })

  const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
  const reception = await context.newPage()
  await login(reception, `cb_${stamp}`, password)
  const patient = await call<{ id: number; fileNumber: string }>(reception, 'POST', '/patients', { firstName: `فهرست${stamp}`, gender: 'Male', confirmNotDuplicate: true })
  await ensureTariffs(page)
  await openDesk(page, reception)
  await reception.goto('/reception')

  // A required choice left empty does not pass the form's own check.
  await reception.getByRole('button', { name: 'ثبت وقت' }).click()
  const booking = reception.getByRole('dialog', { name: 'ثبت وقت' })
  expect(await booking.getByRole('combobox', { name: 'داکتر' }).evaluate((el: HTMLInputElement) => el.validity.valid)).toBe(false)
  await booking.getByRole('button', { name: 'انصراف' }).click()
  await expect(booking).toBeHidden()

  await reception.getByRole('button', { name: 'پذیرش مریض حضوری' }).click()
  const dialog = reception.getByRole('dialog')
  const department = dialog.getByRole('combobox', { name: 'بخش' })
  const list = reception.getByRole('listbox', { name: 'بخش' })

  // ARIA wiring.
  await expect(department).toHaveAttribute('aria-expanded', 'false')
  await department.click()
  await expect(department).toHaveAttribute('aria-expanded', 'true')
  await expect(list).toBeVisible()
  expect(await department.getAttribute('aria-controls')).toBe(await list.getAttribute('id'))

  // The list sits fully on screen and above the dialog.
  const box = (await list.boundingBox())!
  expect(box.y + box.height).toBeLessThanOrEqual(768)
  expect(await list.evaluate((el) => el.contains(document.elementFromPoint(el.getBoundingClientRect().left + 10, el.getBoundingClientRect().top + 10)))).toBe(true)

  // Esc closes the list and leaves the dialog open.
  await reception.keyboard.press('Escape')
  await expect(list).toBeHidden()
  await expect(dialog).toBeVisible()

  // Typing narrows the list; Persian Yeh and Arabic Yeh find the same thing.
  await department.fill('ابتداي')
  await expect(reception.getByRole('listbox').getByRole('option')).toHaveCount(1)
  await department.fill('هیچ‌چیز این‌طور نیست')
  await expect(reception.getByRole('status').filter({ hasText: 'موردی پیدا نشد' })).toBeVisible()
  await expect(reception.getByRole('listbox').getByRole('option')).toHaveCount(0)

  // Enter with nothing to choose never submits; closing drops the text, so free text cannot be saved.
  await department.press('Enter')
  await expect(dialog).toBeVisible()
  // The department the desk starts with stays chosen.
  await department.press('Escape')
  await expect(department).toHaveValue('')
  await expect(dialog.locator('.xr-chip')).toContainText('معاینه ابتدایی')
  expect(await department.evaluate((el: HTMLInputElement) => el.validity.valid)).toBe(true)

  // Keyboard only: open, move, confirm. The tag appears and the stored value is the record's id.
  await department.fill('')
  await department.press('ArrowDown')
  await department.press('ArrowDown')
  const active = await department.getAttribute('aria-activedescendant')
  expect(active).toBeTruthy()
  await department.press('Enter')
  await expect(list).toBeHidden()
  await expect(dialog.locator('.xr-chip')).toHaveCount(1)
  expect(await department.evaluate((el: HTMLInputElement) => el.validity.valid)).toBe(true)

  // A required choice has no clear button; an optional one does.
  await expect(dialog.getByRole('button', { name: 'پاک کردن بخش' })).toHaveCount(0)
  const doctor = dialog.getByRole('combobox', { name: 'داکتر (اختیاری)' })
  await doctor.click()
  await reception.keyboard.press('Escape')
  await expect(doctor).toHaveValue('')

  // Server search, with the same field type.
  await collectFee(dialog)
  const patientField = dialog.getByRole('combobox', { name: 'مریض', exact: true })
  await patientField.fill(`فهرست${stamp}`)
  await reception.getByRole('listbox').getByRole('option', { name: new RegExp(patient.fileNumber) }).click()
  await expect(dialog.locator('.xr-chip').first()).toContainText(patient.fileNumber)

  // Choosing marks the dialog as edited, so closing asks first.
  await reception.keyboard.press('Escape')
  await expect(dialog.getByRole('alert')).toContainText('ذخیره نشده')
  await dialog.getByRole('button', { name: 'ادامه ویرایش' }).click()

  // The chosen ids are what reach the server.
  const posted: Record<string, unknown>[] = []
  reception.on('request', (request) => {
    if (request.method() === 'POST' && request.url().endsWith('/api/visits')) posted.push(request.postDataJSON())
  })
  await dialog.getByRole('button', { name: 'ثبت مراجعه' }).click()
  await expect(dialog).toBeHidden()
  expect(posted[0].patientId).toBe(patient.id)
  expect(typeof posted[0].departmentId).toBe('number')
  await context.close()
})

test('searchable field: a slow old answer cannot replace a newer one, and a failed search keeps the choice', async ({ browser, page }) => {
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await call(page, 'POST', '/users', { userName: `cs_${stamp}`, fullName: 'کارمند جستجو', password, roles: ['Receptionist'] })

  const context = await browser.newContext()
  const reception = await context.newPage()
  await login(reception, `cs_${stamp}`, password)
  const old = await call<{ fileNumber: string }>(reception, 'POST', '/patients', { firstName: `کهنه${stamp}`, gender: 'Male', confirmNotDuplicate: true })
  const fresh = await call<{ fileNumber: string }>(reception, 'POST', '/patients', { firstName: `تازه${stamp}`, gender: 'Male', confirmNotDuplicate: true })

  await reception.goto('/reception')
  await reception.getByRole('button', { name: 'ثبت وقت' }).click()
  const dialog = reception.getByRole('dialog', { name: 'ثبت وقت' })
  const field = dialog.getByRole('combobox', { name: 'مریض', exact: true })

  await reception.route(/\/api\/patients\?.*کهنه|\/api\/patients\?.*%DA%A9%D9%87%D9%86%D9%87/, async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500))
    await route.continue().catch(() => {})
  })
  await field.fill(`کهنه${stamp}`)
  await reception.waitForTimeout(400)
  await field.fill(`تازه${stamp}`)
  await expect(reception.getByRole('listbox').getByRole('option', { name: new RegExp(fresh.fileNumber) })).toBeVisible()
  await reception.waitForTimeout(1600)
  await expect(reception.getByRole('listbox').getByRole('option', { name: new RegExp(fresh.fileNumber) })).toBeVisible()
  await expect(reception.getByRole('listbox').getByRole('option', { name: new RegExp(old.fileNumber) })).toHaveCount(0)
  await reception.getByRole('listbox').getByRole('option', { name: new RegExp(fresh.fileNumber) }).click()

  // The network drops during the next search: the message says so, and the patient already chosen stays chosen.
  await reception.route('**/api/patients?*', (route) => route.abort())
  await field.fill('هرچیز')
  await expect(reception.getByRole('alert').filter({ hasText: 'انتخاب قبلی حفظ شده است' })).toBeVisible()
  await field.press('Escape')
  await expect(dialog.locator('.xr-chip').first()).toContainText(fresh.fileNumber)
  await context.close()
})
