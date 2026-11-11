import { call, ensureTariffs, openDesk, paidToken } from './helpers'
import { expect, test, type Browser, type Page } from '@playwright/test'

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

/** Every delete asks first, and these tests always mean it. */
function answerYes(page: Page) {
  page.on('dialog', (dialog) => dialog.accept())
}

async function staffPage(admin: Page, browser: Browser, role: string, errors: string[]) {
  const userName = `rec_${crypto.randomUUID().slice(0, 13)}`
  await call(admin, 'POST', '/users', { userName, fullName: `کارمند ${role}`, password, roles: [role] })
  const page = await (await browser.newContext()).newPage()
  page.on('pageerror', (e) => errors.push(e.message))
  answerYes(page)
  await login(page, userName, password)
  return page
}

test('directory lists edit and delete, and a used record is deactivated instead', async ({ page }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))
  answerYes(page)
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)

  // A department nothing uses goes for good.
  await page.goto('/departments')
  await page.getByRole('button', { name: 'بخش جدید' }).click()
  let dialog = page.getByRole('dialog')
  await dialog.getByLabel('نام', { exact: true }).fill(`بخش ${stamp}`)
  await dialog.getByLabel('کد بخش').fill(`d${stamp}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  const department = page.getByRole('row', { name: new RegExp(`بخش ${stamp}`) })
  await department.getByRole('button', { name: 'حذف' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'حذف شد' })).toBeVisible()
  await expect(department).toHaveCount(0)

  // A seeded one stays, and the screen says why.
  await page.getByRole('row', { name: /triage/ }).getByRole('button', { name: 'حذف' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'سیستمی' })).toBeVisible()

  // Services: edited, then deleted.
  await page.goto('/settings')
  await page.getByRole('tab', { name: 'خدمات و تعرفه‌ها' }).click()
  await page.getByRole('button', { name: 'خدمت جدید' }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByLabel('کد').fill(`S${stamp}`)
  await dialog.getByLabel('نام خدمت').fill(`خدمت ${stamp}`)
  await dialog.getByLabel('قیمت').fill('100')
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  const service = page.getByRole('row', { name: new RegExp(`S${stamp}`) })
  await service.getByRole('button', { name: 'ویرایش' }).click()
  dialog = page.getByRole('dialog', { name: 'ویرایش خدمت' })
  await dialog.getByLabel('نام خدمت').fill(`خدمت تازه ${stamp}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(service).toContainText(`خدمت تازه ${stamp}`)
  await service.getByRole('button', { name: 'حذف' }).click()
  await expect(service).toHaveCount(0)

  // The tests list was plain text before; it now has its own actions.
  await page.getByRole('tab', { name: 'فهرست آزمایش‌ها' }).click()
  await page.getByRole('button', { name: 'آزمایش جدید' }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByLabel('نام آزمایش').fill(`آزمایش ${stamp}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  const labTest = page.getByRole('row', { name: new RegExp(`آزمایش ${stamp}`) })
  await labTest.getByRole('button', { name: 'ویرایش' }).click()
  dialog = page.getByRole('dialog', { name: 'ویرایش آزمایش' })
  await dialog.getByLabel('نام آزمایش').fill(`آزمایش تازه ${stamp}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  const renamedTest = page.getByRole('row', { name: new RegExp(`آزمایش تازه ${stamp}`) })
  await expect(renamedTest).toBeVisible()
  await renamedTest.getByRole('button', { name: 'حذف' }).click()
  await expect(renamedTest).toHaveCount(0)

  // Registers: an unused one goes.
  await page.getByRole('tab', { name: 'صندوق‌ها' }).click()
  await page.getByLabel('نام صندوق جدید').fill(`صندوق ${stamp}`)
  await page.getByRole('button', { name: 'افزودن صندوق' }).click()
  const register = page.getByRole('row', { name: new RegExp(`صندوق ${stamp}`) })
  await register.getByRole('button', { name: 'ویرایش' }).click()
  dialog = page.getByRole('dialog', { name: 'ویرایش صندوق' })
  await dialog.getByLabel('نام صندوق').fill(`صندوق تازه ${stamp}`)
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  const renamedRegister = page.getByRole('row', { name: new RegExp(`صندوق تازه ${stamp}`) })
  await expect(renamedRegister).toBeVisible()
  await renamedRegister.getByRole('button', { name: 'حذف' }).click()
  await expect(renamedRegister).toHaveCount(0)

  // One with a session behind it is only deactivated, and the list still shows it.
  const used = await call<{ id: number }>(page, 'POST', '/cash/registers', { name: `صندوق کارکرده ${stamp}` })
  const cashier = `rec_${crypto.randomUUID().slice(0, 13)}`
  await call(page, 'POST', '/users', { userName: cashier, fullName: 'صندوق‌دار', password, roles: ['Cashier'] })
  const context = await page.context().browser()!.newContext()
  const cashierPage = await context.newPage()
  await login(cashierPage, cashier, password)
  await call(cashierPage, 'POST', '/cash/sessions', { registerId: used.id, openingAmount: 0 })
  await context.close()
  await page.reload()
  await page.getByRole('tab', { name: 'صندوق‌ها' }).click()
  const usedRow = page.getByRole('row', { name: new RegExp(`صندوق کارکرده ${stamp}`) })
  await usedRow.getByRole('button', { name: 'حذف' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'غیرفعال شد' })).toBeVisible()
  await expect(usedRow).toContainText('غیرفعال')

  expect(errors).toEqual([])
})

test('a purchase is corrected and cancelled from the inventory list, and a transfer reversed', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  const inventory = await staffPage(page, browser, 'Inventory', errors)

  const name = `جنس${stamp}`
  const product = await call<{ id: number }>(inventory, 'POST', '/inventory/products', { code: `P${stamp}`, name, kind: 'General', unit: 'عدد', reorderLevel: 0, salePrice: 10, isActive: true })
  const supplier = await call<{ id: number }>(inventory, 'POST', '/suppliers', { name: `شرکت ${stamp}`, isActive: true })
  const onHand = async () => (await call<{ items: { id: number; onHand: number }[] }>(inventory, 'GET', `/inventory/products?q=${encodeURIComponent(name)}`)).items[0].onHand
  const buy = (quantity: number) => call<{ purchaseNumber: string }>(inventory, 'POST', '/inventory/purchases', { supplierId: supplier.id, locationCode: 'central', items: [{ productId: product.id, quantity, unitCost: 5 }] })

  const first = await buy(10)
  await inventory.goto('/inventory')
  await inventory.getByRole('link', { name: /گدام مرکزی/ }).click()
  await expect(inventory).toHaveURL(/\/inventory\/locations\/central$/)
  await expect(inventory.getByRole('region', { name: 'موجودی فعلی' }).getByRole('row', { name: new RegExp(name) })).toContainText('۱۰')
  await expect(inventory.getByRole('region', { name: 'تاریخچه ورود و خروج' }).getByRole('row', { name: new RegExp(name) }).first()).toContainText('ورود')
  await inventory.getByRole('link', { name: 'بازگشت به گدام' }).click()
  await inventory.getByRole('tab', { name: 'خریدها' }).click()
  const row = inventory.getByRole('row', { name: new RegExp(first.purchaseNumber) })
  await row.getByRole('link', { name: 'اصلاح' }).click()

  // The form opens with what was recorded, and asks why.
  await expect(inventory.getByRole('heading', { name: new RegExp(`اصلاح خرید ${first.purchaseNumber}`) })).toBeVisible()
  await expect(inventory.getByText(name)).toBeVisible()
  await inventory.getByLabel('مقدار ردیف 1').fill('7')
  await inventory.getByLabel('دلیل اصلاح').fill('مقدار اشتباه بود')
  await inventory.getByRole('button', { name: 'لغو و ثبت خرید تازه' }).click()
  await expect(inventory).toHaveURL(/\/inventory$/)
  expect(await onHand()).toBe(7)

  await inventory.getByRole('tab', { name: 'خریدها' }).click()
  await expect(inventory.getByRole('row', { name: new RegExp(first.purchaseNumber) })).toContainText('لغو شده')

  // Moved to the wrong place, then moved back from the movements list.
  const batch = (await call<{ id: number }[]>(inventory, 'GET', `/inventory/batches?productId=${product.id}`))[0]
  await call(inventory, 'POST', '/inventory/transfers', { batchId: batch.id, toLocationCode: 'pharmacy', quantity: 2 })
  await inventory.reload()
  await inventory.getByRole('tab', { name: 'گردش موجودی' }).click()
  await inventory.getByRole('row', { name: new RegExp(`${name}.*انتقال خروجی`) }).getByRole('button', { name: 'برگشت' }).click()
  let dialog = inventory.getByRole('dialog')
  await dialog.getByLabel('دلیل').fill('مقصد اشتباه بود')
  await dialog.getByRole('button', { name: 'ثبت برگشت' }).click()
  await expect(dialog).toBeHidden()
  const batches = await call<{ locationCode: string; quantity: number }[]>(inventory, 'GET', `/inventory/batches?productId=${product.id}`)
  expect(batches.find((b) => b.locationCode === 'central')?.quantity).toBe(7)
  expect(batches.find((b) => b.locationCode === 'pharmacy')).toBeUndefined()

  // Cancelled outright: the stock leaves again.
  const second = await buy(3)
  await inventory.reload()
  await inventory.getByRole('tab', { name: 'خریدها' }).click()
  await inventory.getByRole('row', { name: new RegExp(second.purchaseNumber) }).getByRole('button', { name: 'لغو' }).click()
  dialog = inventory.getByRole('dialog')
  await dialog.getByLabel('دلیل').fill('خرید ثبت نشده بود')
  await dialog.getByRole('button', { name: 'لغو خرید' }).click()
  await expect(dialog).toBeHidden()
  expect(await onHand()).toBe(7)

  // A deleted product with this history is only deactivated, and leaves the pickers.
  await inventory.getByRole('tab', { name: 'اجناس' }).click()
  await inventory.getByRole('row', { name: new RegExp(`P${stamp}`) }).getByRole('button', { name: 'حذف' }).click()
  await expect(inventory.getByRole('status').filter({ hasText: 'غیرفعال شد' })).toBeVisible()
  await expect(inventory.getByRole('row', { name: new RegExp(`P${stamp}`) })).toContainText('غیرفعال')

  expect(errors).toEqual([])
})

test('an expense, a sale and an invoice are corrected in place, and the books stay in balance', async ({ page, browser }) => {
  test.setTimeout(150_000)
  const errors: string[] = []
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  const accountant = await staffPage(page, browser, 'Accountant', errors)
  const inventory = await staffPage(page, browser, 'Inventory', errors)
  const pharmacist = await staffPage(page, browser, 'Pharmacist', errors)
  const cashier = await staffPage(page, browser, 'Cashier', errors)

  // Expense.
  await accountant.goto('/accounting')
  await accountant.getByRole('tab', { name: 'مصارف' }).click()
  await accountant.getByRole('button', { name: 'ثبت مصرف' }).click()
  let dialog = accountant.getByRole('dialog')
  await dialog.getByLabel('دسته').fill(`برق ${stamp}`)
  await dialog.getByLabel('مبلغ').fill('100')
  await dialog.getByRole('button', { name: 'ثبت مصرف' }).click()
  await expect(dialog).toBeHidden()
  await accountant.getByRole('row', { name: new RegExp(`برق ${stamp}`) }).getByRole('button', { name: 'اصلاح' }).click()
  dialog = accountant.getByRole('dialog')
  await expect(dialog.getByLabel('دسته')).toHaveValue(`برق ${stamp}`)
  await dialog.getByLabel('مبلغ').fill('150')
  await dialog.getByLabel('دلیل اصلاح').fill('مبلغ اشتباه بود')
  await dialog.getByRole('button', { name: 'ثبت اصلاح' }).click()
  await expect(dialog).toBeHidden()
  const rows = accountant.getByRole('row', { name: new RegExp(`برق ${stamp}`) })
  await expect(rows).toHaveCount(2)
  await expect(rows.filter({ hasText: 'لغو شده' })).toHaveCount(1)

  // Pharmacy sale.
  const drug = `دوا${stamp}`
  const product = await call<{ id: number }>(inventory, 'POST', '/inventory/products', { code: `D${stamp}`, name: drug, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 10, isActive: true })
  await call(inventory, 'POST', '/inventory/purchases', { supplierName: 'تأمین', locationCode: 'pharmacy', items: [{ productId: product.id, quantity: 10, unitCost: 5 }] })
  const sale = await call<{ saleNumber: string }>(pharmacist, 'POST', '/pharmacy/sales', { items: [{ productId: product.id, quantity: 4 }] })
  await pharmacist.goto('/pharmacy')
  await pharmacist.getByRole('tab', { name: 'سوابق فروش' }).click()
  await pharmacist.getByRole('row', { name: new RegExp(sale.saleNumber) }).getByRole('link', { name: 'اصلاح' }).click()
  await expect(pharmacist.getByRole('heading', { name: new RegExp(`اصلاح فروش ${sale.saleNumber}`) })).toBeVisible()
  await expect(pharmacist.getByText(drug)).toBeVisible()
  await pharmacist.getByLabel('مقدار ردیف 1').fill('2')
  await pharmacist.getByLabel('دلیل اصلاح').fill('مقدار اشتباه بود')
  await pharmacist.getByRole('button', { name: 'لغو و ثبت بل تازه' }).click()
  await expect(pharmacist.getByRole('status').filter({ hasText: 'ثبت و موجودی' })).toBeVisible()
  const stock = await call<{ items: { onHand: number }[] }>(inventory, 'GET', `/inventory/products?q=${encodeURIComponent(drug)}`)
  // A bill only holds stock; it leaves the shelf when the paid bill is handed over.
  expect(stock.items[0].onHand).toBe(10)

  // Standalone invoice, from the cashier's list.
  const code = `S${stamp}`
  const service = await call<{ id: number }>(page, 'POST', '/services', { code, name: `خدمت ${stamp}`, isActive: true, price: 100 })
  const invoice = await call<{ invoiceNumber: string }>(cashier, 'POST', '/invoices', { items: [{ serviceId: service.id, quantity: 1 }] })
  await cashier.goto('/cashier')
  await cashier.getByRole('row', { name: new RegExp(invoice.invoiceNumber) }).getByRole('link', { name: 'اصلاح' }).click()
  await expect(cashier.getByRole('heading', { name: new RegExp(`اصلاح فاکتور ${invoice.invoiceNumber}`) })).toBeVisible()
  await expect(cashier.getByText(`خدمت ${stamp}`).first()).toBeVisible()
  await cashier.getByLabel('تعداد ردیف 1').fill('3')
  await cashier.getByLabel('دلیل اصلاح').fill('تعداد اشتباه بود')
  await cashier.getByRole('button', { name: 'باطل کردن و صدور فاکتور تازه' }).click()
  await expect(cashier).toHaveURL(/\/cashier$/)
  await expect(cashier.getByRole('row', { name: new RegExp(invoice.invoiceNumber) })).toHaveCount(0)

  // After all of it, the ledger still agrees with the documents.
  await accountant.goto('/accounting')
  await expect(accountant.getByRole('status').filter({ hasText: 'تطبیق دارد' })).toBeVisible()

  expect(errors).toEqual([])
})

test('clinical records change freely until final, and with a visible reason after', async ({ page, browser }) => {
  test.setTimeout(150_000)
  const errors: string[] = []
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  await ensureTariffs(page)
  const reception = await staffPage(page, browser, 'Receptionist', errors)
  const triage = await staffPage(page, browser, 'Triage', errors)
  const doctor = await staffPage(page, browser, 'Doctor', errors)

  const patient = await call<{ id: number }>(reception, 'POST', '/patients', { firstName: `اصلاح${stamp}`, gender: 'Male', confirmNotDuplicate: true })
  const departments = await call<{ id: number; name: string }[]>(reception, 'GET', '/lookups/departments')
  await openDesk(page, reception)
  const departmentId = departments.find((d) => d.name === 'معاینه ابتدایی')!.id
  const visit = await call<{ id: number }>(reception, 'POST', '/visits', { patientId: patient.id, departmentId, tokenId: await paidToken(reception, departmentId) })

  // A completed pre-exam opens only for a correction with its reason.
  await call(triage, 'PUT', `/pre-examinations/${visit.id}`, { visualAcuityOd: '6/9', iopOd: '15' })
  await call(triage, 'POST', `/pre-examinations/${visit.id}/complete`)
  await triage.goto(`/triage/${visit.id}`)
  const vision = triage.getByLabel('دید چشم راست (VA OD)')
  await expect(vision).toBeDisabled()
  await triage.getByRole('button', { name: 'ثبت اصلاحیه' }).click()
  await vision.fill('6/12')
  await triage.getByLabel('دلیل اصلاح').fill('اشتباه خوانده شد')
  await triage.getByRole('button', { name: 'ذخیره اصلاحیه' }).click()
  await expect(triage.getByRole('status').filter({ hasText: 'اصلاحیه ثبت شد' })).toBeVisible()
  await expect(vision).toBeDisabled()

  // A prescription before the exam is final: no reason asked.
  await call(doctor, 'POST', '/prescriptions', { visitId: visit.id, items: [{ drugName: `تیمولول${stamp}`, quantity: 1 }] })
  await doctor.goto(`/chart/${visit.id}`)
  await doctor.getByRole('tab', { name: 'نسخه دوا' }).click()
  await doctor.getByRole('button', { name: 'ویرایش', exact: true }).click()
  await expect(doctor.getByRole('heading', { name: /ویرایش نسخه/ })).toBeVisible()
  await expect(doctor.getByLabel('دلیل اصلاح')).toHaveCount(0)
  await doctor.getByLabel('تعداد 1').fill('5')
  await doctor.getByRole('button', { name: 'ذخیره ویرایش' }).click()
  await expect(doctor.getByRole('status').filter({ hasText: 'نسخه ویرایش شد' })).toBeVisible()
  await expect(doctor.getByText(/تعداد: 5/)).toBeVisible()

  // A follow-up goes through the same screen, and can be removed.
  await doctor.getByRole('tab', { name: /پیگیری/ }).click()
  const date = new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10)
  await call(doctor, 'POST', '/follow-ups', { visitId: visit.id, dueDate: date, note: `یادداشت ${stamp}` })
  await doctor.reload()
  await doctor.getByRole('tab', { name: /پیگیری/ }).click()
  const followUp = doctor.getByRole('row', { name: new RegExp(`یادداشت ${stamp}`) })
  await followUp.getByRole('button', { name: 'ویرایش' }).click()
  await doctor.getByLabel('یادداشت').fill(`یادداشت تازه ${stamp}`)
  await doctor.getByRole('button', { name: 'ذخیره ویرایش' }).click()
  const changed = doctor.getByRole('row', { name: new RegExp(`یادداشت تازه ${stamp}`) })
  await expect(changed).toBeVisible()
  await changed.getByRole('button', { name: 'حذف' }).click()
  await expect(changed).toHaveCount(0)

  // Final: the same edit now asks why, and the history shows old, new and reason.
  await doctor.getByRole('tab', { name: 'نسخه دوا' }).click()
  const chart = await call<{ consultation: { version: number } | null }>(doctor, 'GET', `/consultations/${visit.id}/chart`)
  const version = chart.consultation?.version ?? (await call<{ version: number }>(doctor, 'PUT', `/consultations/${visit.id}`, { findingsOd: 'یافته' })).version
  await call(doctor, 'POST', `/consultations/${visit.id}/finalize`, { version, closeVisit: false })
  await doctor.reload()
  await doctor.getByRole('tab', { name: 'نسخه دوا' }).click()
  await doctor.getByRole('button', { name: 'ویرایش', exact: true }).click()
  await doctor.getByLabel('تعداد 1').fill('6')
  await doctor.getByLabel('دلیل اصلاح').fill('مقدار دوباره بررسی شد')
  await doctor.getByRole('button', { name: 'ذخیره ویرایش' }).click()
  await expect(doctor.getByRole('status').filter({ hasText: 'نسخه ویرایش شد' })).toBeVisible()

  const after = await call<{ amendments: { field: string; reason: string; newValue: string | null }[] }>(doctor, 'GET', `/consultations/${visit.id}/chart`)
  expect(after.amendments.map((a) => a.reason)).toEqual(expect.arrayContaining(['مقدار دوباره بررسی شد', 'اشتباه خوانده شد']))
  expect(after.amendments.find((a) => a.field === 'VisualAcuityOd')?.newValue).toBe('6/12')

  expect(errors).toEqual([])
})

test('an appointment is edited at reception, and a mistaken patient file is deleted', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  const stamp = Date.now()
  await login(page, adminUser, adminPassword)
  const reception = await staffPage(page, browser, 'Receptionist', errors)

  const staff = await call<{ id: number }>(page, 'POST', '/staff', { fullName: `داکتر ${stamp}`, isDoctor: true, isActive: true })
  const patient = await call<{ id: number; version: number }>(reception, 'POST', '/patients', { firstName: `وقت${stamp}`, gender: 'Male', confirmNotDuplicate: true })

  // Today, late enough never to be in the past while this runs.
  const today = await reception.evaluate(() => new Date(Date.now() + 4.5 * 3_600_000).toISOString().slice(0, 10))
  const scheduledAt = new Date(`${today}T23:30:00+04:30`).toISOString()
  await call(reception, 'POST', '/appointments', { patientId: patient.id, doctorStaffId: staff.id, scheduledAt, note: 'اول' })
  await reception.goto('/reception')
  await reception.getByRole('tab', { name: /وقت ملاقات/ }).click()
  const row = reception.getByRole('row', { name: new RegExp(`وقت${stamp}`) })
  await row.getByRole('button', { name: /گزینه‌های وقت/ }).click()
  await reception.getByRole('menuitem', { name: 'ویرایش وقت' }).click()
  const dialog = reception.getByRole('dialog', { name: 'ویرایش وقت ملاقات' })
  await expect(dialog.getByLabel('ساعت')).toHaveValue('23:30')
  await expect(dialog.getByLabel('یادداشت')).toHaveValue('اول')
  await dialog.getByLabel('ساعت').fill('23:45')
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()
  const saved = await call<{ scheduledAt: string }[]>(reception, 'GET', `/appointments?date=${today}`)
  expect(saved.some((a) => new Date(a.scheduledAt).toISOString() === new Date(`${today}T23:45:00+04:30`).toISOString())).toBe(true)

  // A file with an appointment stays, and the screen says why.
  await reception.goto(`/patients/${patient.id}`)
  await reception.getByRole('button', { name: 'حذف', exact: true }).click()
  await expect(reception.getByRole('alert').filter({ hasText: 'سوابق' })).toBeVisible()

  // A file with nothing behind it goes.
  const mistaken = await call<{ id: number }>(reception, 'POST', '/patients', { firstName: `اشتباه${stamp}`, gender: 'Female', confirmNotDuplicate: true })
  await reception.goto(`/patients/${mistaken.id}`)
  await reception.getByRole('button', { name: 'حذف', exact: true }).click()
  await expect(reception).toHaveURL(/\/patients$/)

  expect(errors).toEqual([])
})
