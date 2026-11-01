// Drives the running API with concurrent staff doing realistic work, then checks the books still agree.
// Usage: node loadtest.mjs <baseUrl> <adminUser> <adminPassword> <seconds> <users>
import { randomUUID } from 'node:crypto'
import { writeFileSync } from 'node:fs'

const [baseUrl, adminUser, adminPassword, secondsArg = '120', usersArg = '30', outFile] = process.argv.slice(2)
const seconds = Number(secondsArg)
const totalUsers = Number(usersArg)
const PASSWORD = 'Load-Test-Password-1'

const samples = new Map()
const statusCounts = new Map()
const errors = new Map()

class Client {
  cookies = new Map()

  async request(name, method, path, body, { idempotent = false, form } = {}) {
    const headers = {}
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (method !== 'GET') {
      const token = this.cookies.get('XSRF-TOKEN')
      if (token) headers['X-XSRF-TOKEN'] = decodeURIComponent(token)
    }
    if (idempotent) headers['Idempotency-Key'] = randomUUID().replaceAll('-', '')
    if (this.cookies.size) headers.Cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ')

    const started = performance.now()
    let response
    try {
      response = await fetch(baseUrl + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' })
    } catch (cause) {
      record(name, performance.now() - started, 0, String(cause.cause?.code ?? cause.message))
      return { status: 0, data: null }
    }
    for (const line of response.headers.getSetCookie()) {
      const [pair] = line.split(';')
      const index = pair.indexOf('=')
      this.cookies.set(pair.slice(0, index), pair.slice(index + 1))
    }
    const text = await response.text()
    let data = null
    try {
      data = text ? JSON.parse(text) : null
    } catch {
      data = null
    }
    record(name, performance.now() - started, response.status, response.ok ? null : data?.code ?? `http_${response.status}`)
    return { status: response.status, data }
  }

  async login(userName, password) {
    await this.request('csrf', 'GET', '/api/auth/csrf')
    return this.request('login', 'POST', '/api/auth/login', { userName, password })
  }
}

function record(name, ms, status, error) {
  if (!samples.has(name)) samples.set(name, [])
  samples.get(name).push(ms)
  statusCounts.set(status, (statusCounts.get(status) ?? 0) + 1)
  if (error) errors.set(`${name}: ${error}`, (errors.get(`${name}: ${error}`) ?? 0) + 1)
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const think = () => sleep(300 + Math.random() * 1200)
const pick = (list) => list[Math.floor(Math.random() * list.length)]

const admin = new Client()
const adminLogin = await admin.login(adminUser, adminPassword)
if (adminLogin.status !== 200) throw new Error(`admin login failed: ${adminLogin.status}`)

const stamp = Date.now()

async function createUser(role, index) {
  const userName = `load_${role}_${index}_${stamp}`
  const result = await admin.request('setup', 'POST', '/api/users', { userName, fullName: userName, password: PASSWORD, roles: [role] })
  if (result.status !== 201) throw new Error(`could not create ${userName}: ${JSON.stringify(result.data)}`)
  return userName
}

const departments = (await admin.request('setup', 'GET', '/api/lookups/departments')).data
const triageDepartment = departments.find((d) => d.name === 'معاینه ابتدایی').id

const service = await admin.request('setup', 'POST', '/api/services', { code: `LOAD${stamp}`, name: `خدمت آزمون ${stamp}`, isActive: true, price: 500 })

const inventoryUser = await createUser('Inventory', 0)
const inventory = new Client()
await inventory.login(inventoryUser, PASSWORD)
const product = await inventory.request('setup', 'POST', '/api/inventory/products', { code: `LOADD${stamp}`, name: `دوای آزمون ${stamp}`, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 100 })
const productId = product.data.id
const INITIAL_STOCK = 200
await inventory.request('setup', 'POST', '/api/inventory/purchases', { locationCode: 'pharmacy', items: [{ productId, lotNumber: 'L1', quantity: INITIAL_STOCK, unitCost: 40 }] }, { idempotent: true })

const share = { Receptionist: 0.34, Triage: 0.17, Doctor: 0.17, Cashier: 0.16, Pharmacist: 0.16 }
const assignments = []
for (const [role, fraction] of Object.entries(share)) {
  const count = Math.max(1, Math.round(totalUsers * fraction))
  for (let i = 0; i < count; i++) assignments.push({ role, index: i })
}

const workers = {
  async Receptionist(client) {
    const term = `مریض${Math.floor(Math.random() * 50000)}`
    await client.request('patient_search', 'GET', `/api/patients?pageSize=25&q=${encodeURIComponent(term)}`)
    await think()
    await client.request('queue_today', 'GET', '/api/visits?pageSize=100')
    await think()
    const created = await client.request('patient_create', 'POST', '/api/patients', { firstName: `آزمون${randomUUID().slice(0, 8)}`, gender: 'Male', estimatedAge: 40 }, { idempotent: true })
    if (created.status === 201) {
      await client.request('visit_create', 'POST', '/api/visits', { patientId: created.data.id, departmentId: triageDepartment, serviceId: service.data.id, reason: 'آزمون' }, { idempotent: true })
    }
  },
  async Triage(client) {
    const list = await client.request('triage_queue', 'GET', '/api/clinical/worklist?kind=triage')
    if (list.status === 200 && list.data.length) {
      const visit = pick(list.data)
      const saved = await client.request('preexam_save', 'PUT', `/api/pre-examinations/${visit.id}`, { chiefComplaint: 'آزمون', visualAcuityOd: '6/9' })
      if (saved.status === 200) await client.request('preexam_complete', 'POST', `/api/pre-examinations/${visit.id}/complete`)
    }
  },
  async Doctor(client) {
    const list = await client.request('doctor_queue', 'GET', '/api/clinical/worklist?kind=doctor')
    if (list.status === 200 && list.data.length) {
      const visit = pick(list.data)
      const chart = await client.request('chart_read', 'GET', `/api/consultations/${visit.id}/chart`)
      await think()
      if (chart.status === 200) {
        const saved = await client.request('consultation_save', 'PUT', `/api/consultations/${visit.id}`, { findingsOd: 'آزمون', version: chart.data.consultation?.version ?? null })
        if (saved.status === 200 && !saved.data.finalizedAt) {
          await client.request('consultation_finalize', 'POST', `/api/consultations/${visit.id}/finalize`, { version: saved.data.version, closeVisit: true })
        }
      }
    }
  },
  async Cashier(client) {
    const list = await client.request('invoices_unpaid', 'GET', '/api/invoices?unpaid=true&pageSize=25')
    if (list.status === 200 && list.data.items.length) {
      const invoice = pick(list.data.items)
      const amount = Math.random() < 0.3 ? Math.min(100, invoice.balance) : invoice.balance
      await client.request('payment_create', 'POST', '/api/payments', { invoiceId: invoice.id, amount, method: 'Cash' }, { idempotent: true })
    }
  },
  async Pharmacist(client) {
    await client.request('prescriptions_list', 'GET', '/api/prescriptions?status=Issued')
    await think()
    await client.request('pharmacy_sale', 'POST', '/api/pharmacy/sales', { items: [{ productId, quantity: 1 }] }, { idempotent: true })
  },
}

console.log(`users: ${assignments.length}, seconds: ${seconds}`)

const clients = []
for (const { role, index } of assignments) {
  const userName = await createUser(role, index)
  if (role === 'Cashier') {
    const register = await admin.request('setup', 'POST', '/api/cash/registers', { name: `صندوق آزمون ${index} ${stamp}` })
    clients.push({ role, userName, registerId: register.data.id })
  } else {
    clients.push({ role, userName })
  }
}

// All staff log in at once, as at a shift change.
const loginStart = performance.now()
await Promise.all(clients.map(async (c) => {
  c.client = new Client()
  const result = await c.client.login(c.userName, PASSWORD)
  if (result.status !== 200) throw new Error(`login failed for ${c.userName}: ${result.status}`)
  if (c.registerId) {
    const opened = await c.client.request('setup', 'POST', '/api/cash/sessions', { registerId: c.registerId, openingAmount: 0 })
    if (opened.status !== 201) throw new Error(`session failed: ${JSON.stringify(opened.data)}`)
  }
}))
console.log(`concurrent logins: ${clients.length} in ${Math.round(performance.now() - loginStart)} ms`)

samples.delete('setup')
errors.clear()
statusCounts.clear()

const deadline = Date.now() + seconds * 1000
await Promise.all(clients.map(async ({ role, client }) => {
  await sleep(Math.random() * 2000)
  while (Date.now() < deadline) {
    try {
      await workers[role](client)
    } catch (cause) {
      record('worker_exception', 0, 0, String(cause))
    }
    await think()
  }
}))

function percentile(sorted, p) {
  return sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)] : 0
}

const rows = [...samples].map(([name, list]) => {
  const sorted = [...list].sort((a, b) => a - b)
  return { name, count: list.length, p50: Math.round(percentile(sorted, 50)), p95: Math.round(percentile(sorted, 95)), max: Math.round(sorted.at(-1) ?? 0) }
}).sort((a, b) => a.name.localeCompare(b.name))

console.table(rows)
console.log('status codes:', Object.fromEntries(statusCounts))
console.log('rejections:', Object.fromEntries(errors))

const accountantName = await createUser('Accountant', 0)
const accountant = new Client()
await accountant.login(accountantName, PASSWORD)
const reconciliation = (await accountant.request('verify', 'GET', '/api/ledger/reconciliation')).data
console.log('reconciliation:', reconciliation)

const stockRows = (await inventory.request('verify', 'GET', `/api/inventory/products?q=${encodeURIComponent(`LOADD${stamp}`)}`)).data.items
const onHand = stockRows[0].onHand
const sold = samples.get('pharmacy_sale')?.length ?? 0
console.log(`stock: start ${INITIAL_STOCK}, attempts ${sold}, on hand ${onHand}`)

const summary = { users: clients.length, seconds, rows, statusCodes: Object.fromEntries(statusCounts), rejections: Object.fromEntries(errors), reconciliation, stock: { initial: INITIAL_STOCK, onHand, productId } }
if (outFile) writeFileSync(outFile, JSON.stringify(summary, null, 2))

const failed = (statusCounts.get(500) ?? 0) > 0 || !reconciliation.balanced || onHand < 0
console.log(failed ? 'RESULT: FAILED' : 'RESULT: integrity checks passed')
process.exit(failed ? 1 : 0)
