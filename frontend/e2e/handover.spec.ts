import { expect, test, type Browser, type Page } from '@playwright/test'
import { call, ensureTariffs, openDesk, paidToken, pick } from './helpers'

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

async function staff(browser: Browser, user: string) {
  const page = await (await browser.newContext()).newPage()
  await login(page, user, password)
  return page
}

test('the responsible doctor and the manager hand a patient over through the screens, and the desk cannot skip Vision', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await ensureTariffs(page)

  const accounts: Record<string, number> = {}
  for (const [user, role] of [[`ho_r${stamp}`, 'Receptionist'], [`ho_t${stamp}`, 'Triage'], [`ho_a${stamp}`, 'Doctor'], [`ho_b${stamp}`, 'Doctor'], [`ho_m${stamp}`, 'Manager']]) {
    accounts[user] = (await call<{ id: number }>(page, 'POST', '/users', { userName: user, fullName: `کارمند ${user}`, password, roles: [role] })).id
  }
  const doctorA = `داکتر الف ${stamp}`
  const doctorB = `داکتر ب ${stamp}`
  const staffA = await call<{ id: number }>(page, 'POST', '/staff', { fullName: doctorA, isDoctor: true, isActive: true, userId: accounts[`ho_a${stamp}`] })
  await call(page, 'POST', '/staff', { fullName: doctorB, isDoctor: true, isActive: true, userId: accounts[`ho_b${stamp}`] })

  const reception = await staff(browser, `ho_r${stamp}`)
  await openDesk(page, reception)
  const patient = await call<{ id: number }>(reception, 'POST', '/patients', { firstName: `حمید${stamp}`, gender: 'Male', estimatedAge: 40, confirmNotDuplicate: true })
  const departments = await call<{ id: number; name: string }[]>(reception, 'GET', '/lookups/departments')
  const department = departments.find((d) => d.name === 'معاینه ابتدایی')!.id
  const visit = await call<{ id: number }>(reception, 'POST', '/visits', { patientId: patient.id, departmentId: department, doctorStaffId: staffA.id, tokenId: await paidToken(reception, department) })

  // The desk's status list no longer offers a way to the doctor before Vision.
  await reception.goto('/reception')
  await reception.getByRole('tab', { name: /در جریان/ }).click()
  const receptionRow = reception.getByRole('row', { name: new RegExp(`حمید${stamp}`) })
  await receptionRow.getByRole('button', { name: /گزینه‌های/ }).click()
  const options = await reception.getByRole('menuitem').allTextContents()
  expect(options.join('|')).not.toContain('آماده برای داکتر')
  await reception.keyboard.press('Escape')

  const triage = await staff(browser, `ho_t${stamp}`)
  await call(triage, 'PUT', `/pre-examinations/${visit.id}`, { visualAcuityOd: '6/9', version: null })
  await call(triage, 'POST', `/pre-examinations/${visit.id}/complete`)

  // The responsible doctor hands over from the chart, with a reason.
  const first = await staff(browser, `ho_a${stamp}`)
  await first.goto(`/chart/${visit.id}`)
  await first.getByRole('button', { name: 'انتقال مسئولیت به داکتر دیگر' }).click()
  const fromChart = first.getByRole('dialog')
  await pick(first, fromChart, 'داکتر جدید', doctorB)
  await fromChart.getByLabel('دلیل انتقال').fill('رفتن به عملیات')
  await fromChart.getByRole('button', { name: 'ثبت انتقال' }).click()
  await expect(fromChart).toBeHidden()
  await expect(first.getByRole('status').first()).toContainText('انتقال یافت')

  // The manager covers a doctor who cannot, from the reception list.
  const manager = await staff(browser, `ho_m${stamp}`)
  await manager.goto('/reception')
  await manager.getByRole('tab', { name: /در جریان/ }).click()
  await manager.getByRole('row', { name: new RegExp(`حمید${stamp}`) }).getByRole('button', { name: /گزینه‌های/ }).click()
  await manager.getByRole('menuitem', { name: 'انتقال داکتر' }).click()
  const fromDesk = manager.getByRole('dialog')
  await pick(manager, fromDesk, 'داکتر جدید', doctorA)
  await fromDesk.getByLabel('دلیل انتقال').fill('غیبت داکتر')
  await fromDesk.getByRole('button', { name: 'ثبت انتقال' }).click()
  await expect(fromDesk).toBeHidden()
  await expect(manager.getByRole('row', { name: new RegExp(`حمید${stamp}`) })).toContainText(doctorA)

  const second = await staff(browser, `ho_b${stamp}`)
  await second.goto(`/chart/${visit.id}`)
  await second.getByRole('button', { name: 'انتقال مسئولیت به داکتر دیگر' }).click()
  const refused = second.getByRole('dialog')
  await pick(second, refused, 'داکتر جدید', doctorB)
  await refused.getByLabel('دلیل انتقال').fill('می‌خواهم انتقال دهم')
  await refused.getByRole('button', { name: 'ثبت انتقال' }).click()
  await expect(refused.getByRole('alert')).toContainText('فقط داکتر مسئول')
})
