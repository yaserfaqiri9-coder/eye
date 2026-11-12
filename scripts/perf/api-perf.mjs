// Times the read paths staff use most, one request at a time, against a running API.
// Reports median time, response size (plain and gzip) and SQL statements per request. No response body is logged.
// Usage: node api-perf.mjs <baseUrl> <user> <password> <outFile> [mysqlExe]  (MYSQL_PWD = root password)
import { execFileSync } from 'node:child_process'
import { writeFileSync } from 'node:fs'
import { gzipSync } from 'node:zlib'

const [baseUrl, user, password, outFile, mysqlExe] = process.argv.slice(2)
const RUNS = 7
const cookies = new Map()

async function request(method, path, body, accept = {}) {
  const headers = { ...accept }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET' && cookies.has('XSRF-TOKEN')) headers['X-XSRF-TOKEN'] = decodeURIComponent(cookies.get('XSRF-TOKEN'))
  if (cookies.size) headers.Cookie = [...cookies].map(([k, v]) => `${k}=${v}`).join('; ')
  const started = performance.now()
  const response = await fetch(baseUrl + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  const buffer = Buffer.from(await response.arrayBuffer())
  const ms = performance.now() - started
  for (const line of response.headers.getSetCookie()) {
    const [pair] = line.split(';')
    const i = pair.indexOf('=')
    cookies.set(pair.slice(0, i), pair.slice(i + 1))
  }
  return { status: response.status, ms, buffer, encoding: response.headers.get('content-encoding') }
}

function statements() {
  if (!mysqlExe) return 0
  const out = execFileSync(mysqlExe, ['--protocol=TCP', '-h', '127.0.0.1', '-P', '3307', '-u', 'root', '-N', '-e',
    "SELECT COALESCE(SUM(COUNT_STAR),0) FROM performance_schema.events_statements_summary_by_digest WHERE SCHEMA_NAME='alshifa_load'"]).toString()
  return Number(out.trim())
}

// Each sign-in starts from a fresh anonymous session, as a new browser would; the last one stays signed in.
const loginTimes = []
for (let i = 0; i < 3; i++) {
  cookies.clear()
  await request('GET', '/api/auth/csrf')
  const r = await request('POST', '/api/auth/login', { userName: user, password })
  if (r.status !== 200) throw new Error(`login failed: ${r.status}`)
  loginTimes.push(r.ms)
}

const json = async (path) => JSON.parse((await request('GET', path)).buffer.toString())
const patient = (await json('/api/patients?pageSize=1&q=L-0001234')).items[0]
const busyVisit = (await json('/api/visits?pageSize=1')).items?.[0]
const product = (await json('/api/inventory/products?pageSize=1')).items?.[0]

const paths = [
  ['ورود: me', '/api/auth/me'],
  ['داشبورد', '/api/reports/dashboard'],
  ['داشبورد: مالی', '/api/reports/financial'],
  ['داشبورد: مراجعات امروز', '/api/visits?pageSize=100'],
  ['جستجو: نام', `/api/patients?pageSize=8&q=${encodeURIComponent('مریض4321')}`],
  ['جستجو: تخلص', `/api/patients?pageSize=25&q=${encodeURIComponent('تخلص12')}`],
  ['جستجو: شماره دوسیه', '/api/patients?pageSize=8&q=L-00099'],
  ['جستجو: تلفن', '/api/patients?pageSize=8&q=070001'],
  ['فهرست مریضان', '/api/patients?pageSize=25'],
  ['فهرست مریضان صفحه ۱۰۰۰', '/api/patients?pageSize=25&page=1000'],
  ['دوسیه: مریض', `/api/patients/${patient.id}`],
  ['دوسیه: سابقه بالینی', `/api/clinical/patients/${patient.id}/history`],
  ['دوسیه: رفرکشن', `/api/clinical/patients/${patient.id}/refractions`],
  ['دوسیه: بل‌ها', `/api/invoices?q=${patient.fileNumber}&pageSize=100`],
  ['پذیرش: در انتظار', '/api/admissions/pending'],
  ['پذیرش: نوبت‌ها', '/api/appointments'],
  ['پذیرش: توکن‌ها', '/api/visit-tokens'],
  ['پذیرش: بخش‌ها', '/api/lookups/departments'],
  ['پذیرش: داکترها', '/api/lookups/doctors'],
  ['صندوق: بل‌های پرداخت‌نشده', '/api/invoices?unpaid=true&pageSize=25'],
  ['صندوق: بل‌ها', '/api/invoices?pageSize=100'],
  ['صندوق: پرداخت‌ها', '/api/payments?pageSize=100'],
  ['صندوق: جلسه‌ها', '/api/cash/sessions?mine=true'],
  ['دواخانه: صف', '/api/pharmacy/queues'],
  ['دواخانه: فروش‌ها', '/api/pharmacy/sales?pageSize=50'],
  ['دواخانه: جستجو', `/api/pharmacy/search?q=${encodeURIComponent('مریض12')}`],
  ['دواخانه: نسخه‌ها', '/api/prescriptions?status=Issued'],
  ['موجودی: اجناس', '/api/inventory/products'],
  ['موجودی: بچ‌ها', '/api/inventory/batches'],
  ['موجودی: هشدار', '/api/inventory/alerts'],
  ['موجودی: حرکت‌ها', '/api/inventory/movements?pageSize=100'],
  ['موجودی: خریدها', '/api/inventory/purchases?pageSize=100'],
  ['کارتابل داکتر', '/api/clinical/worklist?kind=doctor'],
  ['کارتابل معاینه ابتدایی', '/api/clinical/worklist?kind=triage'],
  ['گزارش: خلاصه', '/api/reports/overview'],
  ['گزارش: مریضان', '/api/reports/patients'],
  ['گزارش: داکترها', '/api/reports/doctors'],
  ['گزارش: عاید', '/api/reports/income'],
  ['گزارش: صندوق', '/api/reports/cash'],
  ['گزارش: باقی‌داری', '/api/reports/outstanding'],
  ['گزارش: فروش', '/api/reports/sales'],
  ['گزارش: موجودی', '/api/reports/stock'],
  ['گزارش: سال', `/api/reports/patients?from=2025-11-12&to=2026-11-12`],
  ['حساب: بیلانس', '/api/ledger/balances'],
  ['حساب: تطبیق', '/api/ledger/reconciliation'],
]
if (busyVisit) paths.push(['مراجعه', `/api/visits/${busyVisit.id}`])
if (product) paths.push(['جنس', `/api/inventory/products?q=${encodeURIComponent(product.code)}`])

const median = (list) => [...list].sort((a, b) => a - b)[Math.floor(list.length / 2)]
const rows = []
for (const [name, path] of paths) {
  await request('GET', path) // warm-up
  const times = []
  const before = statements()
  let last
  for (let i = 0; i < RUNS; i++) {
    last = await request('GET', path)
    times.push(last.ms)
  }
  const sql = (statements() - before - 1) / RUNS
  const gz = await request('GET', path, undefined, { 'Accept-Encoding': 'gzip, br' })
  rows.push({
    name, status: last.status, ms: Math.round(median(times) * 10) / 10, bytes: last.buffer.length,
    enc: gz.encoding ?? '-', gzip: gzipSync(last.buffer).length, sql: Math.round(sql * 10) / 10,
  })
}

const result = { login: Math.round(median(loginTimes)), rows }
console.log(`login median ${result.login} ms`)
console.table(rows.map((r) => ({ ...r, name: r.name })))
writeFileSync(outFile, JSON.stringify(result, null, 2))
