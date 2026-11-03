import { expect, test, type Page } from '@playwright/test'
import { call } from './helpers'

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

// Conversion runs in the browser, so it is checked in zones on both sides of the date line.
for (const timezoneId of ['Asia/Kabul', 'America/Los_Angeles', 'Pacific/Kiritimati', 'UTC']) {
  test(`every day from 1300 to 1480 converts both ways without shifting in ${timezoneId}`, async ({ browser }) => {
    const context = await browser.newContext({ timezoneId })
    const page = await context.newPage()
    await page.goto('/login')
    const failures = await page.evaluate(async () => {
      const { isoToShamsi, shamsiToIso, shamsiMonthLength, isShamsiLeap, parseShamsi, formatShamsi } = await import('/src/utils/date.ts')
      const found: string[] = []
      let days = 0
      for (let year = 1300; year <= 1480; year++) {
        for (let month = 1; month <= 12; month++) {
          const length = shamsiMonthLength(year, month)
          for (let day = 1; day <= length; day++) {
            const iso = shamsiToIso(year, month, day)
            const back = isoToShamsi(iso)
            days++
            if (back.year !== year || back.month !== month || back.day !== day) found.push(`${year}/${month}/${day} -> ${iso} -> ${back.year}/${back.month}/${back.day}`)
            if (parseShamsi(formatShamsi(iso)) !== iso) found.push(`text ${iso}`)
          }
        }
        // Day one of each year must directly follow the last day of the year before, leap or not.
        const lastDay = isShamsiLeap(year) ? 30 : 29
        const next = new Date(`${shamsiToIso(year, 12, lastDay)}T00:00:00Z`)
        next.setUTCDate(next.getUTCDate() + 1)
        if (next.toISOString().slice(0, 10) !== shamsiToIso(year + 1, 1, 1)) found.push(`year end ${year}`)
      }
      if (days < 65000) found.push(`only ${days} days checked`)
      return found.slice(0, 10)
    })
    expect(failures).toEqual([])
    await context.close()
  })
}

test('known leap years and calendar facts', async ({ page }) => {
  await page.goto('/login')
  const facts = await page.evaluate(async () => {
    const { isShamsiLeap, shamsiMonthLength, shamsiToIso, shamsiWeekColumn, parseShamsi } = await import('/src/utils/date.ts')
    return {
      leaps: [1399, 1403, 1408, 1412].map(isShamsiLeap),
      notLeaps: [1400, 1404, 1405, 1407].map(isShamsiLeap),
      hut1403: shamsiMonthLength(1403, 12),
      hut1404: shamsiMonthLength(1404, 12),
      nowruz1405: shamsiToIso(1405, 1, 1),
      // 1 Hamal 1405 is Saturday 21 March 2026, so it opens the first column.
      column: shamsiWeekColumn(1405, 1, 1),
      badDay: parseShamsi('1404/12/30'),
      goodDay: parseShamsi('۱۴۰۳/۱۲/۳۰'),
      badMonth: parseShamsi('1405/13/01'),
      noise: parseShamsi('hello'),
    }
  })
  expect(facts).toEqual({
    leaps: [true, true, true, true],
    notLeaps: [false, false, false, false],
    hut1403: 30,
    hut1404: 29,
    nowruz1405: '2026-03-21',
    column: 0,
    badDay: null,
    goodDay: '2025-03-20',
    badMonth: null,
    noise: null,
  })
})

test('calendar: year, month and day selection, keyboard, limits, closing, and no shifted day on save', async ({ browser, page }) => {
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await call(page, 'POST', '/users', { userName: `dt_${stamp}`, fullName: 'کارمند تقویم', password, roles: ['Receptionist'] })

  // A zone far from Kabul is where a date-only value would shift if it were ever treated as a moment in time.
  const context = await browser.newContext({ timezoneId: 'America/Los_Angeles', viewport: { width: 1366, height: 768 } })
  const reception = await context.newPage()
  await login(reception, `dt_${stamp}`, password)
  await reception.goto('/patients/new')
  await reception.getByLabel('نام', { exact: true }).fill(`تقویم${stamp}`)

  const field = reception.getByRole('textbox', { name: 'تاریخ تولد (اختیاری)' })
  await field.click()
  const calendar = reception.getByRole('dialog', { name: /تقویم/ })
  await expect(calendar).toBeVisible()

  // Direct year and month choice; 30 Hut exists only in a leap year.
  await calendar.getByLabel('سال', { exact: true }).selectOption('1403')
  await calendar.getByLabel('ماه', { exact: true }).selectOption({ label: 'حوت' })
  await expect(calendar.getByRole('button', { name: /^۳۰ حوت ۱۴۰۳$/ })).toBeVisible()
  await calendar.getByLabel('سال', { exact: true }).selectOption('1404')
  await expect(calendar.getByRole('button', { name: /^۲۹ حوت ۱۴۰۴$/ })).toBeVisible()
  await expect(calendar.getByRole('button', { name: /^۳۰ حوت ۱۴۰۴$/ })).toHaveCount(0)

  // Birth dates cannot be in the future: tomorrow is off, today works.
  await expect(calendar.getByRole('button', { name: 'امروز' })).toBeEnabled()
  const today = calendar.getByRole('button', { name: 'امروز' })
  await today.click()
  await expect(calendar).toBeHidden()
  await expect(field).not.toHaveValue('')
  await field.click()
  await expect(calendar.locator('[aria-current="date"]')).toBeVisible()
  const nextEnabled = await calendar.locator('.xr-calendar__day:not(:disabled)').last().getAttribute('aria-current')
  expect(nextEnabled).toBe('date')

  // Esc and clicking outside both close it, and the rest of the form stays as it was.
  await reception.keyboard.press('Escape')
  await expect(calendar).toBeHidden()
  await field.click()
  await reception.mouse.click(5, 400)
  await expect(calendar).toBeHidden()

  // The clear action empties an optional field.
  await field.click()
  await calendar.getByRole('button', { name: 'پاک کردن' }).click()
  await expect(field).toHaveValue('')

  // Typed in either digit set; a day that does not exist is refused, and blocks saving instead of being rounded.
  await field.fill('1404/12/30')
  await field.press('Tab')
  await expect(reception.getByRole('alert').filter({ hasText: 'معتبر نیست' })).toBeVisible()
  expect(await field.evaluate((el: HTMLInputElement) => el.validity.valid)).toBe(false)
  await field.fill('۱۳۸۳/۱۱/۰۲')
  await field.press('Tab')
  await expect(reception.getByRole('alert').filter({ hasText: 'معتبر نیست' })).toHaveCount(0)

  // Keyboard only: open, move a day, confirm. The grid is right to left, so the left arrow is the next day.
  await field.focus()
  await field.press('ArrowDown')
  await expect(calendar).toBeVisible()
  await expect(calendar.locator('[data-cursor="true"]')).toBeFocused()
  await reception.keyboard.press('ArrowLeft')
  await expect(calendar.locator('[data-cursor="true"]')).toHaveAttribute('aria-label', /^۳ دلو ۱۳۸۳$/)
  await reception.keyboard.press('ArrowRight')
  await reception.keyboard.press('Enter')
  await expect(calendar).toBeHidden()
  await expect(field).toHaveValue('۱۳۸۳/۱۱/۰۲')
  await expect(field).toBeFocused()

  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(/\/patients\/\d+$/)
  const id = reception.url().match(/(\d+)$/)![1]
  // 2 Dalw 1383 is 21 January 2005, stored date-only, whatever zone this computer is in.
  expect((await call<{ birthDate: string }>(reception, 'GET', `/patients/${id}`)).birthDate).toBe('2005-01-21')

  // Editing shows the stored date and leaves it intact. The old input wiped it on opening.
  await reception.getByRole('link', { name: 'ویرایش' }).click()
  await expect(field).toHaveValue('۱۳۸۳/۱۱/۰۲')
  await reception.getByLabel('آدرس').fill('هرات')
  await reception.getByRole('button', { name: 'ذخیره' }).click()
  await expect(reception).toHaveURL(new RegExp(`/patients/${id}$`))
  expect((await call<{ birthDate: string }>(reception, 'GET', `/patients/${id}`)).birthDate).toBe('2005-01-21')
  await context.close()
})

test('calendar inside a dialog stays on screen, is not clipped, and Esc closes only the calendar', async ({ browser, page }) => {
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await call(page, 'POST', '/users', { userName: `dm_${stamp}`, fullName: 'کارمند مودال', password, roles: ['Receptionist'] })

  const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
  const reception = await context.newPage()
  await login(reception, `dm_${stamp}`, password)
  await reception.goto('/reception')
  await reception.getByRole('button', { name: 'ثبت وقت' }).click()
  const dialog = reception.getByRole('dialog', { name: 'ثبت وقت' })
  const field = dialog.getByRole('textbox', { name: 'تاریخ', exact: true })
  await expect(field).not.toHaveValue('')
  await field.click()
  const calendar = reception.getByRole('dialog', { name: /تقویم/ })
  await expect(calendar).toBeVisible()

  const box = (await calendar.boundingBox())!
  expect(box.x).toBeGreaterThanOrEqual(0)
  expect(box.y).toBeGreaterThanOrEqual(0)
  expect(box.x + box.width).toBeLessThanOrEqual(1366)
  expect(box.y + box.height).toBeLessThanOrEqual(768)
  // Nothing covers the far corners of the calendar, so it really sits on top of the dialog.
  const onTop = await calendar.evaluate((el) => {
    const r = el.getBoundingClientRect()
    return [[r.left + 4, r.top + 4], [r.right - 4, r.bottom - 4]].every(([x, y]) => el.contains(document.elementFromPoint(x, y)))
  })
  expect(onTop).toBe(true)

  // Past days are off for a new appointment.
  await expect(calendar.getByRole('button', { name: 'ماه قبل' })).toBeEnabled()
  await calendar.getByRole('button', { name: 'ماه قبل' }).click()
  await expect(calendar.locator('.xr-calendar__day:not(:disabled)')).toHaveCount(0)

  await reception.keyboard.press('Escape')
  await expect(calendar).toBeHidden()
  await expect(dialog).toBeVisible()
  await context.close()
})
