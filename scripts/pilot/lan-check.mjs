// Drives a running release over its real network address, using only Node. Called by smoke.ps1.
// NODE_EXTRA_CA_CERTS holds only the hospital CA, so these requests also prove the certificate chain and address match.
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs'
import { randomUUID } from 'node:crypto'

const lan = process.env.PILOT_LAN_URL
const loop = process.env.PILOT_LOOP_URL
const statePath = process.env.PILOT_STATE
const ackedPath = `${statePath}.acked`
const PASSWORD = 'Pilot-Test-Password-1'

class Session {
  cookies = new Map()

  constructor(base, headers = {}) {
    this.base = base
    this.headers = headers
  }

  async request(path, { method = 'GET', body, form, idempotent } = {}) {
    const headers = { ...this.headers, cookie: [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ') }
    if (method !== 'GET') headers['X-XSRF-TOKEN'] = decodeURIComponent(this.cookies.get('XSRF-TOKEN') ?? '')
    if (idempotent) headers['Idempotency-Key'] = randomUUID().replaceAll('-', '')
    if (body) headers['Content-Type'] = 'application/json'
    const response = await fetch(`${this.base}/api${path}`, { method, headers, body: form ?? (body ? JSON.stringify(body) : undefined) })
    for (const cookie of response.headers.getSetCookie()) {
      const [pair] = cookie.split(';')
      const at = pair.indexOf('=')
      this.cookies.set(pair.slice(0, at), pair.slice(at + 1))
    }
    return response
  }

  async json(path, options, expected = [200, 201]) {
    const response = await this.request(path, options)
    const text = await response.text()
    if (!expected.includes(response.status)) throw new Error(`${options?.method ?? 'GET'} ${path} -> ${response.status}: ${text}`)
    return text ? JSON.parse(text) : null
  }

  async login(userName, password) {
    await this.json('/auth/csrf', undefined, [204])
    await this.json('/auth/login', { method: 'POST', body: { userName, password } })
    return this
  }
}

async function workflow() {
  const admin = await new Session(lan).login('pilotadmin', process.env.PILOT_ADMIN_PASSWORD)

  const names = { reception: 'Receptionist', doctor: 'Doctor', pharmacy: 'Pharmacist', cashier: 'Cashier', director: 'Manager' }
  const suffix = randomUUID().slice(0, 6)
  for (const [name, role] of Object.entries(names)) {
    await admin.json('/users', { method: 'POST', body: { userName: `${name}_${suffix}`, fullName: name, password: PASSWORD, roles: [role] } })
  }

  const [reception, doctor, pharmacy, cashier] = await Promise.all(
    ['reception', 'doctor', 'pharmacy', 'cashier'].map((name) => new Session(lan).login(`${name}_${suffix}`, PASSWORD)),
  )
  const who = await Promise.all([reception, doctor, pharmacy, cashier].map((s) => s.json('/auth/me')))
  if (new Set(who.map((u) => u.userName)).size !== 4) throw new Error('Sessions are not independent')

  const service = await admin.json('/services', { method: 'POST', body: { code: `S-${suffix}`, name: 'معاینه', isActive: true, price: 300 } })
  const register = await admin.json('/cash/registers', { method: 'POST', body: { name: `صندوق ${suffix}` } })
  await cashier.json('/cash/sessions', { method: 'POST', body: { registerId: register.id, openingAmount: 0 } })

  const departments = await reception.json('/lookups/departments')
  const departmentId = departments.find((d) => d.name === 'داکتر').id

  const patient = await reception.json('/patients', { method: 'POST', idempotent: true, body: { firstName: 'مریض آزمایشی', gender: 'Male', estimatedAge: 40 } })
  const visit = await reception.json('/visits', { method: 'POST', idempotent: true, body: { patientId: patient.id, departmentId, serviceId: service.id } })
  if ((await doctor.json(`/patients/${patient.id}`)).fileNumber !== patient.fileNumber) throw new Error('Doctor saw another patient')

  const rx = await doctor.json('/prescriptions', { method: 'POST', idempotent: true, body: { visitId: visit.id, items: [{ drugName: 'قطره آزمایشی', quantity: 1 }] } })
  if (!JSON.stringify(await pharmacy.json('/prescriptions?status=Issued')).includes(rx.rxNumber)) throw new Error('Pharmacy did not see the prescription')

  const unpaid = await cashier.json(`/invoices?unpaid=true&q=${patient.fileNumber}`)
  const invoice = unpaid.items[0]
  await cashier.json('/payments', { method: 'POST', idempotent: true, body: { invoiceId: invoice.id, amount: invoice.balance, method: 'Cash' } })
  if ((await cashier.json(`/invoices/${invoice.id}`)).invoice.balance !== 0) throw new Error('Balance did not reach zero')

  // Access from one department to another's data must still be refused over the real network path.
  const denied = await pharmacy.request(`/consultations/${visit.id}/chart`)
  if (denied.status !== 403) throw new Error(`Pharmacy read the chart: ${denied.status}`)

  // The upload must come back byte for byte, because that is what a restored file is later compared with.
  const png = Buffer.from('89504e470d0a1a0a0000000d4948445200000001000000010806000000', 'hex')
  const form = new FormData()
  form.set('ownerType', 'Logo')
  form.set('ownerId', '0')
  form.set('file', new Blob([png], { type: 'image/png' }), 'logo.png')
  const attachment = await admin.json('/attachments', { method: 'POST', form })
  const download = await admin.request(`/attachments/${attachment.id}`)
  if (!Buffer.from(await download.arrayBuffer()).equals(png)) throw new Error('Attachment changed on the way back')

  // What Tailscale Serve does: plain HTTP on loopback, announcing that the visitor arrived over HTTPS.
  const proxied = new Session(loop, { 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '100.100.50.1', 'X-Forwarded-Host': 'alshifa.example.ts.net' })
  await proxied.login(`director_${suffix}`, PASSWORD)
  const authCookie = proxied.cookies.get('alshifa.auth')
  if (!authCookie) throw new Error('No session through the proxy path')
  await proxied.json('/reports/financial')
  await proxied.json('/reports/operations')
  const denied2 = await proxied.request('/users', { method: 'POST', body: {} })
  if (denied2.status !== 403 && denied2.status !== 400) throw new Error(`Director reached user management: ${denied2.status}`)
  const proxiedPatient = await proxied.json(`/patients/${patient.id}`)
  if (proxiedPatient.fileNumber !== patient.fileNumber) throw new Error('Remote user saw different data')
  const proxiedAudit = await admin.json('/audit?pageSize=50')
  if (!JSON.stringify(proxiedAudit).includes('100.100.50.1')) throw new Error('Remote address was not recorded in the audit log')

  writeFileSync(statePath, JSON.stringify({ suffix, departmentId }))
  console.log(`workflow ok: ${patient.fileNumber} ${rx.rxNumber}`)
}

async function backupStatus(expected) {
  const admin = await new Session(lan).login('pilotadmin', process.env.PILOT_ADMIN_PASSWORD)
  const status = await admin.json('/settings/backup-status')
  if (status.needsAttention !== expected) throw new Error(`Backup status: ${JSON.stringify(status)}`)
  console.log(`backup status ok: ${JSON.stringify(status)}`)
}

// Runs until killed. A patient is recorded here only after the API confirmed it, so anything listed must survive a crash.
async function writer() {
  const { suffix } = JSON.parse(readFileSync(statePath, 'utf8'))
  const reception = await new Session(lan).login(`reception_${suffix}`, PASSWORD)
  writeFileSync(`${statePath}.cookies`, JSON.stringify([...reception.cookies]))
  for (;;) {
    try {
      const patient = await reception.json('/patients', { method: 'POST', idempotent: true, body: { firstName: `نوشتن ${randomUUID().slice(0, 8)}`, gender: 'Female', estimatedAge: 30 } })
      appendFileSync(ackedPath, `${patient.id}\n`)
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 200))
    }
  }
}

async function afterCrash() {
  if (!existsSync(ackedPath)) throw new Error('The writer recorded nothing')
  const acked = readFileSync(ackedPath, 'utf8').trim().split('\n')
  const reception = new Session(lan)
  reception.cookies = new Map(JSON.parse(readFileSync(`${statePath}.cookies`, 'utf8')))
  for (const id of acked) await reception.json(`/patients/${id}`)
  // Writing proves the CSRF token survived the restart too, not just the session.
  const fresh = await reception.json('/patients', { method: 'POST', idempotent: true, body: { firstName: `پس از خرابی ${randomUUID().slice(0, 8)}`, gender: 'Male', estimatedAge: 50 } })
  console.log(`after crash ok: ${acked.length} acknowledged patients present, session still valid, new patient ${fresh.fileNumber}`)
}

// With the database down, a save must fail clearly and within a reasonable time, never hang or look successful.
async function dbDown() {
  const reception = new Session(lan)
  reception.cookies = new Map(JSON.parse(readFileSync(`${statePath}.cookies`, 'utf8')))
  const started = Date.now()
  const response = await reception.request('/patients', { method: 'POST', idempotent: true, body: { firstName: 'نباید ثبت شود', gender: 'Male', estimatedAge: 50 } })
  const seconds = (Date.now() - started) / 1000
  if (response.ok) throw new Error('A save was reported as successful with the database down')
  if (seconds > 45) throw new Error(`The failure took ${seconds} seconds`)
  console.log(`database down ok: answered ${response.status} after ${seconds.toFixed(1)}s`)
}

const mode = process.argv[2]
const run = { workflow, writer, 'after-crash': afterCrash, 'db-down': dbDown, 'backup-ok': () => backupStatus(false), 'backup-failed': () => backupStatus(true) }[mode]
await run().catch((error) => {
  console.error(error.message)
  process.exit(1)
})
