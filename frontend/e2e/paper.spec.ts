import { mkdirSync, writeFileSync } from 'node:fs'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { call, ensureTariffs, openDesk, paidToken, pick } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'
const evidence = process.env.ALSHIFA_EVIDENCE_DIR

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function staff(browser: Browser, user: string) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
  const page = await context.newPage()
  await login(page, user, password)
  return page
}

/** Page count and size read from the PDF itself, not from how the browser preview looks. */
function pdfFacts(pdf: Buffer) {
  const text = pdf.toString('latin1')
  const pages = (text.match(/\/Type\s*\/Page[^s]/g) ?? []).length
  const box = text.match(/\/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)/)
  return { pages, width: Math.round(Number(box?.[1])), height: Math.round(Number(box?.[2])) }
}

async function pdfOf(page: Page, name: string) {
  await page.emulateMedia({ media: 'print' })
  const pdf = await page.pdf({ preferCSSPageSize: true, printBackground: false })
  if (evidence) {
    mkdirSync(evidence, { recursive: true })
    writeFileSync(`${evidence}/${name}.pdf`, pdf)
  }
  await page.emulateMedia({ media: 'screen' })
  return pdfFacts(pdf)
}

const A4 = { width: 595, height: 842 }

test('the file prints on one double-sided A4 sheet from recorded data, and the other papers from the same records', async ({ page, browser }) => {
  test.setTimeout(180_000)
  const stamp = Date.now()
  const longName = stamp.toString()

  await login(page, adminUser, adminPassword)
  await call(page, 'PUT', '/settings', { hospitalName: 'شفاخانه آزمایشی', address: 'آدرس آزمایشی', phone: '0700000000', printFooter: 'پاورقی آزمایشی' })
  for (const [user, role] of [[`pp_r${stamp}`, 'Receptionist'], [`pp_t${stamp}`, 'Triage'], [`pp_d${stamp}`, 'Doctor'], [`pp_o${stamp}`, 'Optical'], [`pp_s${stamp}`, 'Surgery']]) {
    await call(page, 'POST', '/users', { userName: user, fullName: `کارمند ${role}`, password, roles: [role] })
  }

  const reception = await staff(browser, `pp_r${stamp}`)
  const patient = await call<{ id: number; fileNumber: string }>(reception, 'POST', '/patients', { firstName: 'احمد', lastName: longName, fatherName: 'محمود', gender: 'Male', estimatedAge: 54, phone: '0799999999', address: 'هرات، سرک آزمایشی', confirmNotDuplicate: true })
  const departments = await call<{ id: number; name: string }[]>(reception, 'GET', '/lookups/departments')
  await ensureTariffs(page)
  await openDesk(page, reception)
  const triageDepartment = departments.find((d) => d.name === 'معاینه ابتدایی')!.id
  const visit = await call<{ id: number }>(reception, 'POST', '/visits', { patientId: patient.id, departmentId: triageDepartment, tokenId: await paidToken(reception, triageDepartment) })

  // The optometrist fills the triage form and the refraction form through the real pages.
  const triage = await staff(browser, `pp_t${stamp}`)
  await triage.goto(`/triage/${visit.id}`)
  await triage.getByLabel('شکایت اصلی').fill('تاری دید چشم راست')
  await triage.getByLabel('مدت (Duration)').fill('سه ماه')
  await triage.getByLabel(/VA OD/).fill('6/18')
  await triage.getByLabel(/VA OS/).fill('6/9')
  await triage.getByLabel(/PH OD/).fill('6/12')
  await triage.getByLabel('GL OD').fill('6/12')
  await triage.getByLabel('NV OS').fill('N8')
  await triage.getByLabel(/IOP OD/).fill('16')
  await triage.getByLabel(/IOP OS/).fill('17')
  await triage.getByLabel(/^BP/).fill('130/85')
  await triage.getByRole('group', { name: 'Diabetes' }).getByRole('button', { name: 'Y' }).click()
  await triage.getByLabel('Years using Rx Diabetes').fill('7')
  await triage.getByRole('group', { name: 'HTN' }).getByRole('button', { name: 'N' }).click()
  await triage.getByRole('group', { name: 'Allergies' }).getByRole('button', { name: 'Y' }).click()
  await triage.getByLabel('Allergic to').fill('پنسلین')
  await triage.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(triage.getByText('ذخیره شد.')).toBeVisible()

  // A saved structured value comes back in its own field.
  await triage.reload()
  await expect(triage.getByLabel('Years using Rx Diabetes')).toHaveValue('7')
  await expect(triage.getByRole('group', { name: 'HTN' }).getByRole('button', { name: 'N' })).toHaveAttribute('aria-pressed', 'true')
  // An unanswered question stays unanswered rather than turning into a no.
  await expect(triage.getByRole('group', { name: 'Asthma' }).getByRole('button', { name: 'N' })).toHaveAttribute('aria-pressed', 'false')
  await expect(triage.getByLabel('Allergic to')).toHaveValue('پنسلین')
  await triage.getByRole('button', { name: 'ذخیره و ارسال به داکتر' }).click()
  await expect(triage).toHaveURL(/\/triage$/)

  // Refraction starts only from the doctor's request, paid in full.
  const doctor = await staff(browser, `pp_d${stamp}`)
  await call(doctor, 'PUT', `/consultations/${visit.id}`, { diagnoses: [], version: null })
  const tests = await call<{ id: number; kind: string }[]>(doctor, 'GET', '/diagnostic-tests')
  await call(doctor, 'POST', '/diagnostic-orders', { patientId: patient.id, visitId: visit.id, testId: tests.find((t) => t.kind === 'Refraction')!.id })
  const fees = await call<{ invoiceId: number; patientFileNumber: string; balance: number; invoiceVersion: number }[]>(reception, 'GET', '/reception-fees')
  const fee = fees.find((f) => f.patientFileNumber === patient.fileNumber)!
  await call(reception, 'POST', '/reception-fees/pay', { invoiceId: fee.invoiceId, amount: fee.balance, method: 'Cash', invoiceVersion: fee.invoiceVersion })

  await triage.goto(`/refraction/${visit.id}`)
  await triage.getByLabel('دید بدون اصلاح OD').fill('6/18')
  await triage.getByLabel('دید بدون اصلاح OS').fill('6/9')
  await triage.getByLabel('دید نزدیک OD').fill('N6')
  await triage.getByLabel('Auto Refraction: SPH OD').fill('-1.75')
  await triage.getByLabel('Subjective Refraction: SPH OD').fill('-1.5')
  await triage.getByLabel('Final Refraction: SPH OD').fill('-1.5')
  await triage.getByLabel('Final Refraction: CYL OD').fill('-0.5')
  await triage.getByLabel('Final Refraction: AXIS OD').fill('90')
  await triage.getByLabel('Final Refraction: ADD OD').fill('+1.5')
  await triage.getByLabel('Final Refraction: VA OD').fill('6/6')
  await triage.getByLabel('Final Refraction: SPH OS').fill('-0.75')
  await triage.getByLabel('Final Refraction: VA OS').fill('6/6')
  await triage.getByLabel(/^PD/).fill('63')
  await triage.getByLabel('Bifocal').check()
  await triage.getByLabel('White').check()
  await triage.getByText('Retinoscopy و اندازه‌گیری‌های تکمیلی').click()
  await triage.getByLabel('K1 (H) OD').fill('43.5')
  await triage.getByLabel('AL OD').fill('23.4')
  await triage.getByLabel('IOL OD').fill('21.0')
  await triage.getByLabel(/^RBS/).fill('110')
  await triage.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(triage.getByText('ذخیره شد.')).toBeVisible()
  await triage.reload()
  await expect(triage.getByLabel('K1 (H) OD')).toHaveValue('43.5')
  await expect(triage.getByLabel('Bifocal')).toBeChecked()

  // The refraction slip: A5 landscape, one page, from those values, retyping nothing.
  const [slip] = await Promise.all([triage.context().waitForEvent('page'), triage.getByRole('link', { name: 'چاپ نسخه رفرکشن' }).click()])
  const slipSheet = slip.getByRole('article', { name: /Refraction/ })
  await expect(slipSheet).toContainText('-1.50')
  await expect(slipSheet).toContainText('63')
  await expect(slipSheet).toContainText('شفاخانه آزمایشی')
  await expect(slipSheet.locator('.tick--on')).toHaveCount(2)
  expect(await pdfOf(slip, 'refraction-slip')).toEqual({ pages: 1, width: 595, height: 420 })
  await call(triage, 'POST', `/refractions/${visit.id}/complete`)

  // The doctor sees those values, records findings and issues the prescriptions.
  await doctor.goto(`/chart/${visit.id}`)
  await expect(doctor.getByRole('region', { name: 'خلاصه پیش از معاینه' })).toContainText('6/18')
  await doctor.getByLabel('پلک OD').fill('Normal')
  await doctor.getByLabel('قرنیه OD').fill('Clear')
  await doctor.getByLabel('حرکات چشم OD').fill('Full')
  await doctor.getByLabel('عدسیه OD').fill('Nuclear sclerosis')
  await doctor.getByText('بخش خلفی (Posterior Segment)').click()
  await doctor.getByLabel('گشادسازی مردمک OD').fill('T+Ph')
  await doctor.getByLabel('روش معاینه OD').fill('90D')
  await doctor.getByLabel('دیسک بینایی OD').fill('Pink')
  await doctor.getByRole('tab', { name: 'تشخیص و پلان' }).click()
  await doctor.getByRole('button', { name: 'تشخیص چشم راست (OD)' }).click()
  await doctor.getByLabel('متن تشخیص 1').fill('Cataract')
  await doctor.getByRole('button', { name: 'تشخیص چشم چپ (OS)' }).click()
  await doctor.getByLabel('متن تشخیص 2').fill('Refractive error')
  await doctor.getByLabel('توصیه (Advice)').fill('پرهیز از مالیدن چشم')
  await doctor.getByLabel('یادداشت محرمانه داکتر').fill(`محرمانه-${stamp}`)
  await doctor.getByRole('button', { name: 'ذخیره', exact: true }).click()
  await expect(doctor.getByRole('status').first()).toContainText('ذخیره شد')

  // Older plans keep working, and the advice returns to its own box.
  await doctor.reload()
  await doctor.getByRole('tab', { name: 'تشخیص و پلان' }).click()
  await expect(doctor.getByLabel('توصیه (Advice)')).toHaveValue('پرهیز از مالیدن چشم')

  await doctor.getByRole('tab', { name: 'نسخه دوا' }).click()
  await doctor.getByLabel('نام دوا 1').fill('Timolol 0.5%')
  await doctor.getByLabel('مقدار 1').fill('1 drop')
  await doctor.getByLabel('مدت 1').fill('30 days')
  await doctor.getByRole('button', { name: 'ثبت نسخه' }).click()
  await expect(doctor.getByRole('status').first()).toContainText('نسخه ثبت')

  await doctor.getByRole('tab', { name: 'نسخه عینک' }).click()
  await doctor.getByRole('button', { name: 'انتقال از نسخه نهایی بینایی‌سنجی' }).click()
  await expect(doctor.getByLabel('نسخه عینک: VA OD')).toHaveValue('6/6')
  await expect(doctor.getByLabel('Bifocal')).toBeChecked()
  await doctor.getByRole('button', { name: 'ثبت نسخه عینک' }).click()
  await expect(doctor.getByRole('status').first()).toContainText('نسخه عینک ثبت')

  await doctor.getByRole('tab', { name: 'پیگیری' }).click()
  await doctor.getByRole('textbox', { name: 'تاریخ پیگیری', exact: true }).fill('1410/01/15')
  await doctor.getByRole('textbox', { name: 'تاریخ پیگیری', exact: true }).press('Tab')
  await doctor.getByRole('button', { name: 'ثبت پیگیری' }).click()
  await expect(doctor.getByRole('status').first()).toContainText('مراجعه بعدی ثبت شد')

  await doctor.getByRole('tab', { name: 'عملیات' }).click()
  await doctor.getByLabel('نوع عملیات').fill('Phaco + IOL')
  await pick(doctor, doctor, 'خدمت قابل پرداخت', 'فیس عکس')
  await doctor.getByRole('button', { name: 'ارجاع به عملیات' }).click()
  await expect(doctor.getByRole('row', { name: /Phaco/ })).toBeVisible()

  // The file: exactly two A4 pages.
  const [dossier] = await Promise.all([doctor.context().waitForEvent('page'), doctor.getByRole('link', { name: 'چاپ دوسیه' }).click()])
  const front = dossier.getByRole('article', { name: 'روی دوسیه' })
  const back = dossier.getByRole('article', { name: 'پشت دوسیه' })
  await expect(front).toContainText(patient.fileNumber)
  await expect(front).toContainText(longName)
  await expect(front).toContainText('6/18')
  await expect(front).toContainText('130/85')
  await expect(front).toContainText('7 Years using Rx')
  await expect(front).toContainText('پنسلین')
  await expect(front).toContainText('Nuclear sclerosis')
  await expect(front).toContainText('90D')
  await expect(front).toContainText('43.5')
  await expect(front).toContainText('-1.75')
  await expect(back).toContainText('پرهیز از مالیدن چشم')
  await expect(back).toContainText('Cataract')
  await expect(back).toContainText('Refractive error')
  await expect(back).toContainText('Timolol')
  await expect(back).toContainText('1410/01/15')
  await expect(back).toContainText('پاورقی آزمایشی')

  // Never on paper: the confidential note and any money.
  for (const sheet of [front, back]) {
    await expect(sheet).not.toContainText(`محرمانه-${stamp}`)
    await expect(sheet).not.toContainText('افغانی')
  }
  await expect(dossier.getByRole('alert').filter({ hasText: 'بیشتر شد' })).toHaveCount(0)
  await dossier.emulateMedia({ media: 'print' })
  await expect(dossier.getByRole('button', { name: 'چاپ پشت و رو' })).toBeHidden()
  await dossier.emulateMedia({ media: 'screen' })
  expect(await pdfOf(dossier, 'dossier')).toEqual({ pages: 2, ...A4 })

  // The glasses desk prints the slip without any access to the medical record.
  const optical = await staff(browser, `pp_o${stamp}`)
  await optical.goto('/optical')
  const [opticalSlip] = await Promise.all([optical.context().waitForEvent('page'), optical.getByRole('row', { name: new RegExp(longName) }).getByRole('link', { name: 'چاپ نسخه' }).click()])
  const opticalSheet = opticalSlip.locator('.slip')
  await expect(opticalSheet).toContainText('-1.50')
  await expect(opticalSheet).toContainText('6/6')
  await expect(opticalSheet.locator('.tick--on')).toHaveCount(2)
  expect(await pdfOf(opticalSlip, 'optical-slip')).toEqual({ pages: 1, width: 595, height: 420 })
  await expect((await optical.request.get(`/api/consultations/${visit.id}/chart`)).status()).toBe(403)

  // The theatre opens the case as a page and prints its card from the same records.
  const surgery = await staff(browser, `pp_s${stamp}`)
  await surgery.goto('/surgery')
  await surgery.getByRole('row', { name: new RegExp(longName) }).getByRole('link', { name: 'جزئیات' }).click()
  await expect(surgery).toHaveURL(/\/surgery\/\d+$/)
  await expect(surgery.getByText('Cataract')).toBeVisible()
  await surgery.getByLabel('بررسی‌های پیش از عملیات').fill('آماده')
  const [card] = await Promise.all([surgery.context().waitForEvent('page'), surgery.getByRole('link', { name: 'چاپ کارت عملیات‌خانه' }).click()])
  const sheet = card.getByRole('article', { name: 'کارت عملیات‌خانه' })
  await expect(sheet).toContainText('Phaco + IOL')
  await expect(sheet).toContainText('Cataract')
  await expect(sheet).toContainText('سه ماه')
  await expect(sheet).toContainText('130/85')
  await expect(sheet).toContainText('Surging:')
  await expect(sheet).not.toContainText(`محرمانه-${stamp}`)
  expect(await pdfOf(card, 'ot-card')).toEqual({ pages: 1, ...A4 })
  surgery.once('dialog', (dialog) => dialog.dismiss())
  await surgery.getByRole('link', { name: 'بازگشت به عملیات' }).click()
  await expect(surgery).toHaveURL(/\/surgery\/\d+$/)
  await expect(surgery.getByLabel('بررسی‌های پیش از عملیات')).toHaveValue('آماده')
})

test('a very full record still prints on two sides: long text is cut with a visible mark and listed, nothing dropped silently', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  for (const [user, role] of [[`pl_r${stamp}`, 'Receptionist'], [`pl_t${stamp}`, 'Triage'], [`pl_d${stamp}`, 'Doctor']]) {
    await call(page, 'POST', '/users', { userName: user, fullName: `کارمند ${role}`, password, roles: [role] })
  }
  const reception = await staff(browser, `pl_r${stamp}`)
  const patient = await call<{ id: number }>(reception, 'POST', '/patients', { firstName: 'پر', lastName: `${stamp}`, gender: 'Female', estimatedAge: 70, address: 'آ'.repeat(280), confirmNotDuplicate: true })
  const departments = await call<{ id: number; name: string }[]>(reception, 'GET', '/lookups/departments')
  await ensureTariffs(page)
  await openDesk(page, reception)
  const triageDepartment = departments.find((d) => d.name === 'معاینه ابتدایی')!.id
  const visit = await call<{ id: number }>(reception, 'POST', '/visits', { patientId: patient.id, departmentId: triageDepartment, tokenId: await paidToken(reception, triageDepartment) })

  const triage = await staff(browser, `pl_t${stamp}`)
  const long = (text: string, size: number) => text.repeat(Math.ceil(size / text.length)).slice(0, size)
  await call(triage, 'PUT', `/pre-examinations/${visit.id}`, { chiefComplaint: long('شکایت طولانی ', 500), history: long('سابقه طولانی ', 1000), visualAcuityOd: '6/60', visualAcuityOs: '6/60', version: null })
  await call(triage, 'POST', `/pre-examinations/${visit.id}/complete`)

  const doctor = await staff(browser, `pl_d${stamp}`)
  const findings = ['Lids', 'Conjunctiva', 'Cornea', 'Sclera', 'AC', 'Iris', 'Pupil', 'Lens', 'Vitreous', 'EOM', 'Fundus', 'Disc', 'Macula', 'Vessels', 'Periphery'].map((c) => `${c}: ${long('long finding ', 190)}`).join('\n')
  await call(doctor, 'PUT', `/consultations/${visit.id}`, {
    findingsOd: findings,
    findingsOs: findings,
    treatmentPlan: `[توصیه]\n${long('توصیه طولانی ', 790)}`,
    confidentialNote: '',
    diagnoses: Array.from({ length: 8 }, (_, i) => ({ eye: i % 2 ? 'OS' : 'OD', text: long(`تشخیص ${i} `, 300) })),
    version: null,
  })
  await call(doctor, 'POST', '/prescriptions', { visitId: visit.id, notes: long('یادداشت ', 900), items: Array.from({ length: 30 }, (_, i) => ({ drugName: `دوا ${i + 1}`, dose: '1', route: 'Topical', duration: '7 days', instructions: 'روزانه', quantity: 1 })) })

  await doctor.goto(`/print/dossier/${visit.id}`)
  const notice = doctor.getByRole('alert').filter({ hasText: 'در چاپ خلاصه شد' })
  await expect(notice).toBeVisible()
  await expect(notice).toContainText('Advice')
  await expect(doctor.getByRole('article', { name: 'پشت دوسیه' })).toContainText('14 قلم دیگر')
  const sizes = await doctor.locator('.sheet').evaluateAll((els) => els.map((el) => `${el.scrollHeight} of ${el.clientHeight}`))
  if (evidence) await doctor.screenshot({ path: `${evidence}/dossier-full-record-screen.png`, fullPage: true })
  const facts = await pdfOf(doctor, 'dossier-full-record')
  await expect(doctor.getByRole('alert').filter({ hasText: 'بیشتر شد' }), `sheet heights: ${sizes.join(', ')}`).toHaveCount(0)
  expect(facts).toEqual({ pages: 2, ...A4 })
})
