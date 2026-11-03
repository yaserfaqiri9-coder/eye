// Measures how fast the main forms open, against a built frontend (vite preview) and a test database.
// Usage: node scripts/perf/form-perf.mjs <label> [baseUrl]
// Needs ALSHIFA_E2E_USER / ALSHIFA_E2E_PASSWORD for an admin of the TEST database. Never point it at real data: it creates users, patients and visits.
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const frontend = join(here, '..', '..', 'frontend')
const { chromium } = createRequire(join(frontend, 'package.json'))('@playwright/test')

const label = process.argv[2] ?? 'run'
const base = process.argv[3] ?? 'http://localhost:4173'
const adminUser = process.env.ALSHIFA_E2E_USER
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD
const password = 'E2e-Password-123'
const stamp = Date.now()
const repeats = 5

// A hospital LAN on an ordinary laptop: 20ms round trip, 100Mbit, CPU at a quarter of this development machine.
const network = { offline: false, latency: 20, downloadThroughput: (100 * 1024 * 1024) / 8, uploadThroughput: (100 * 1024 * 1024) / 8 }
const cpuRate = 4

async function call(page, method, path, body) {
  return page.evaluate(
    async ({ method, path, body }) => {
      const cookie = document.cookie.match(/XSRF-TOKEN=([^;]+)/)
      let token = cookie ? decodeURIComponent(cookie[1]) : ''
      if (!token) {
        await fetch('/api/auth/csrf')
        token = decodeURIComponent(document.cookie.match(/XSRF-TOKEN=([^;]+)/)[1])
      }
      const response = await fetch(`/api${path}`, {
        method,
        headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token, 'Idempotency-Key': crypto.randomUUID().replaceAll('-', '') },
        body: body === undefined ? undefined : JSON.stringify(body),
      })
      const text = await response.text()
      if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`)
      return text ? JSON.parse(text) : null
    },
    { method, path, body },
  )
}

async function login(page, user, pass) {
  await page.goto(`${base}/login`)
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await page.waitForURL((url) => !url.pathname.startsWith('/login'))
}

async function session(browser, user, pass) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
  const page = await context.newPage()
  await login(page, user, pass)
  const cdp = await context.newCDPSession(page)
  await cdp.send('Network.enable')
  await cdp.send('Network.emulateNetworkConditions', network)
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: cpuRate })
  return { context, page, cdp }
}

/**
 * Timed inside the page, from the click event to the first frame where the target is on screen ("paint")
 * and to the first frame it accepts typing ("ready"), so the test tool's own polling is not in the number.
 */
async function measure(page, scenario) {
  const requests = []
  const onRequest = (request) => {
    const url = request.url()
    if (url.includes('/api/') || url.endsWith('.js') || url.endsWith('.css')) requests.push(url)
  }
  page.on('request', onRequest)
  await page.evaluate(({ paint, ready }) => {
    const seen = (selector) => {
      const el = document.querySelector(selector)
      return !!el && el.getClientRects().length > 0
    }
    window.__timing = {}
    document.addEventListener(
      'click',
      () => {
        const t = window.__timing
        t.start = performance.now()
        const frame = () => {
          const now = performance.now()
          if (!t.paint && seen(paint)) t.paint = now - t.start
          if (!t.ready && seen(ready)) t.ready = now - t.start
          if (!t.ready) requestAnimationFrame(frame)
        }
        requestAnimationFrame(frame)
      },
      { capture: true, once: true },
    )
  }, { paint: scenario.paint, ready: scenario.ready })
  await scenario.open(page)
  await page.waitForFunction(() => window.__timing.ready !== undefined, null, { timeout: 15000 })
  const timing = await page.evaluate(() => window.__timing)
  await page.waitForTimeout(400)
  page.off('request', onRequest)
  return { paint: Math.round(timing.paint), ready: Math.round(timing.ready), api: requests.filter((u) => u.includes('/api/')).length, assets: requests.filter((u) => !u.includes('/api/')).length }
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[Math.floor(sorted.length / 2)]
}

function bundleSizes() {
  const dist = process.env.ALSHIFA_DIST ?? join(frontend, 'dist')
  const dir = join(dist, 'assets')
  const files = readdirSync(dir).map((name) => ({ name, size: statSync(join(dir, name)).size }))
  const js = files.filter((f) => f.name.endsWith('.js'))
  const css = files.filter((f) => f.name.endsWith('.css'))
  const index = readFileSync(join(dist, 'index.html'), 'utf8')
  const entry = js.filter((f) => index.includes(f.name)).reduce((sum, f) => sum + f.size, 0)
  return { jsFiles: js.length, jsTotalKb: Math.round(js.reduce((s, f) => s + f.size, 0) / 1024), entryJsKb: Math.round(entry / 1024), cssTotalKb: Math.round(css.reduce((s, f) => s + f.size, 0) / 1024), largestJs: js.sort((a, b) => b.size - a.size).slice(0, 5).map((f) => `${f.name.replace(/-[\w-]{8}\.js$/, '')}: ${Math.round(f.size / 1024)}KB`) }
}

const browser = await chromium.launch({ channel: 'msedge' })
const admin = await session(browser, adminUser, adminPassword)

for (const [user, role] of [[`pr_${stamp}`, 'Receptionist'], [`pt_${stamp}`, 'Triage'], [`pd_${stamp}`, 'Doctor'], [`pp_${stamp}`, 'Pharmacist']]) {
  await call(admin.page, 'POST', '/users', { userName: user, fullName: `Perf ${role}`, password, roles: [role] })
}

const reception = await session(browser, `pr_${stamp}`, password)
const triage = await session(browser, `pt_${stamp}`, password)
const doctor = await session(browser, `pd_${stamp}`, password)
const pharmacy = await session(browser, `pp_${stamp}`, password)

const departments = await call(reception.page, 'GET', '/lookups/departments')
const department = departments.find((d) => d.name.includes('ابتدایی'))

// One visit waits in the triage queue and one is ready for the doctor.
async function seedVisit(name) {
  const patient = await call(reception.page, 'POST', '/patients', { firstName: name, lastName: `${stamp}`, gender: 'Male', estimatedAge: 40, confirmNotDuplicate: true })
  return call(reception.page, 'POST', '/visits', { patientId: patient.id, departmentId: department.id })
}
const queued = await seedVisit('صف')
const ready = await seedVisit('داکتر')
await call(triage.page, 'PUT', `/pre-examinations/${ready.id}`, { chiefComplaint: 'perf', version: null })
await call(triage.page, 'POST', `/pre-examinations/${ready.id}/complete`)

const scenarios = [
  {
    name: 'ثبت مریض جدید',
    who: reception,
    start: '/patients',
    open: (p) => p.getByRole('button', { name: 'مریض جدید' }).or(p.getByRole('link', { name: 'مریض جدید' })).click(),
    paint: '#p-first',
    ready: '#p-first:enabled',
  },
  {
    name: 'مراجعه جدید (مودال)',
    who: reception,
    start: '/reception',
    open: (p) => p.getByRole('button', { name: 'مراجعه جدید' }).click(),
    paint: '.xr-modal__panel',
    ready: '.xr-modal__panel button[type=submit]:enabled',
  },
  {
    name: 'شروع معاینه ابتدایی',
    who: triage,
    start: '/triage',
    open: (p) => p.getByRole('row', { name: new RegExp(`صف ${stamp}`) }).getByRole('link', { name: 'ثبت معاینه' }).click(),
    paint: 'input[aria-label*="VA OD"]',
    ready: 'input[aria-label*="VA OD"]:enabled',
  },
  {
    name: 'ثبت بینایی‌سنجی',
    who: triage,
    start: '/triage',
    open: (p) => p.getByRole('row', { name: new RegExp(`صف ${stamp}`) }).getByRole('link', { name: 'بینایی‌سنجی' }).click(),
    paint: 'input[aria-label="Final Refraction: SPH OD"]',
    ready: 'input[aria-label="Final Refraction: SPH OD"]:enabled',
  },
  {
    name: 'شروع معاینه داکتر',
    who: doctor,
    start: '/doctor',
    open: (p) => p.getByRole('row', { name: new RegExp(`داکتر ${stamp}`) }).getByRole('link', { name: 'باز کردن پرونده' }).click(),
    paint: '.chart-placeholder, input[aria-label="قرنیه OD"]',
    ready: 'input[aria-label="قرنیه OD"]:enabled',
  },
  {
    name: 'فروش مستقل دواخانه',
    who: pharmacy,
    start: '/pharmacy',
    open: (p) => p.getByRole('button', { name: 'فروش مستقل' }).or(p.getByRole('link', { name: 'فروش مستقل' })).click(),
    paint: '.xr-modal__panel button[type=submit], form.xr-card button[type=submit]',
    ready: '.xr-modal__panel button[type=submit]:enabled, form.xr-card button[type=submit]:enabled',
  },
]

async function returnToStart(page, scenario) {
  const dialog = page.getByRole('dialog').first()
  if (scenario.closes) await scenario.closes(page)
  else if (await dialog.isVisible()) await page.keyboard.press('Escape')
  else await page.goBack()
  await page.waitForTimeout(500)
  await page.waitForLoadState('networkidle').catch(() => {})
}

// Controls that exist only in the new build, measured from a field already on screen.
if (label !== 'before') {
  scenarios.push(
    {
      name: 'باز شدن تقویم شمسی',
      who: reception,
      start: '/patients/new',
      open: (p) => p.getByRole('textbox', { name: 'تاریخ تولد (اختیاری)' }).click(),
      paint: '.xr-calendar',
      ready: '.xr-calendar__day',
      closes: (p) => p.keyboard.press('Escape'),
    },
    {
      name: 'باز شدن ComboBox بخش',
      who: reception,
      start: '/reception',
      prepare: (p) => p.getByRole('button', { name: 'مراجعه جدید' }).click().then(() => p.getByRole('dialog').getByRole('combobox', { name: 'بخش' }).waitFor()),
      open: (p) => p.getByRole('dialog').getByRole('combobox', { name: 'بخش' }).click(),
      paint: '.xr-combo__panel',
      ready: '.xr-combo__option',
      closes: (p) => p.keyboard.press('Escape'),
    },
  )
}

const results = []
for (const scenario of scenarios) {
  const page = scenario.who.page
  // Cold: a fresh page load and an empty browser cache. The wait is the few seconds any real user spends before the first click.
  await page.goto(`${base}${scenario.start}`)
  await page.waitForLoadState('networkidle').catch(() => {})
  await page.waitForTimeout(4000)
  await scenario.who.cdp.send('Network.clearBrowserCache')
  if (scenario.prepare) await scenario.prepare(page)
  const cold = await measure(page, scenario)

  // Warm: the same click again within a working session.
  const warm = []
  for (let i = 0; i < repeats; i++) {
    await returnToStart(page, scenario)
    warm.push(await measure(page, scenario))
  }
  results.push({
    name: scenario.name,
    coldPaintMs: cold.paint,
    coldReadyMs: cold.ready,
    warmPaintMs: median(warm.map((r) => r.paint)),
    warmReadyMs: median(warm.map((r) => r.ready)),
    warmReadyWorstMs: Math.max(...warm.map((r) => r.ready)),
    apiRequests: warm[0].api,
    coldAssets: cold.assets,
  })
  console.log(results.at(-1))
}

const report = { label, at: new Date().toISOString(), conditions: { ...network, cpuRate, viewport: '1366x768', repeats }, bundle: bundleSizes(), results }
const out = join(here, 'results')
mkdirSync(out, { recursive: true })
writeFileSync(join(out, `${label}.json`), JSON.stringify(report, null, 2))
console.log(JSON.stringify(report.bundle, null, 2))
await browser.close()
