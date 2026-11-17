import { expect, test, type Page } from '@playwright/test'

const base = '/audio/recorded-c98dd13ed170/WAV/'
const clips = (n: number, destination: string) => [`${base}numbers/number_${String(n).padStart(3,'0')}.wav`,`${base}destinations/destination_${destination}.wav`]
async function fixture(page:Page, allowed=true) {
  const state={requests:[] as {path:string;method:string}[], claims:[] as any[], reports:[] as any[], pending:[] as any[], display:{id:1,queueNumber:12,destination:'اتاق شماره ۳',patientName:'مریض آزمایشی'}, displayOffline:false}
  // All hospital API requests are intercepted. These tests cannot create real calls or patients.
  await page.route(url=>url.pathname.startsWith('/api/'),async route=>{
    const request=route.request();const path=new URL(request.url()).pathname
    state.requests.push({path,method:request.method()})
    const reply=(json:unknown)=>route.fulfill({json})
    if(path==='/api/auth/me')return allowed?reply({id:1,userName:'audio-test',fullName:'Audio Test',roles:['AudioStation'],permissions:['calls.manage']}):route.fulfill({status:401,json:{}})
    if(path==='/api/auth/csrf')return route.fulfill({status:204,headers:{'Set-Cookie':'XSRF-TOKEN=isolated; Path=/'}})
    if(path==='/api/calls/station/status')return reply({active:true,otherStationOnline:false,clipsTotal:59,clipsMissing:0,missingSample:[],testClips:clips(25,'initial_exam'),history:[]})
    if(path==='/api/calls/display')return state.displayOffline?route.abort():reply(state.display)
    if(path==='/api/calls/station/claim'){state.claims.push(request.postDataJSON());return reply({})}
    if(path==='/api/calls/station/next')return state.pending.length?reply(state.pending.shift()):route.fulfill({status:204})
    if(path.endsWith('/result')){state.reports.push({path,...request.postDataJSON()});return route.fulfill({status:204})}
    return route.fulfill({status:404,json:{code:'unexpected_test_request',title:path}})
  })
  await page.route('**/hubs/**',route=>route.abort())
  return state
}

test('passive TV follows only the server display feed, preserves doctor room, and never claims or consumes audio',async({page})=>{
  const state=await fixture(page)
  await page.goto('/call-screen')
  await expect(page.locator('.lcd__number')).toHaveText('۱۲')
  await expect(page.locator('.lcd__destination')).toHaveText('اتاق شماره ۳')
  await expect(page.locator('.lcd__patient-name')).toHaveText('مریض آزمایشی')
  state.display={...state.display,id:2,destination:'بخش رفرکشن'}
  await expect(page.locator('.lcd__destination')).toHaveText('بخش رفرکشن')
  state.displayOffline=true
  await expect(page.getByRole('status')).toContainText('ارتباط نمایش با سرور قطع است')
  state.displayOffline=false
  await expect(page.locator('.lcd__destination')).toHaveText('بخش رفرکشن')
  expect(state.requests.filter(x=>x.method!=='GET')).toEqual([])
  expect(state.claims).toEqual([])
})

test('TV shows a missing local file, activation retry recovers without silently claiming the station',async({page})=>{
  const state=await fixture(page)
  const missing=`**${base}destinations/destination_surgery.wav`
  await page.route(missing,route=>route.fulfill({status:404,body:'missing'}))
  await page.goto('/call-screen')
  const activate=page.getByRole('button',{name:/برای فعال‌سازی صدا/})
  await activate.click()
  await expect(activate).toContainText('بارگذاری نشد')
  expect(state.claims).toHaveLength(0)
  await page.unroute(missing)
  await activate.click()
  await expect(activate).toHaveCount(0)
  expect(state.claims).toHaveLength(1)
  await page.screenshot({path:test.info().outputPath('tv-ready.png')})
})

test('station uses only event clips, reports unsupported audio, and keeps processing later events',async({page})=>{
  const state=await fixture(page)
  state.pending.push(
    {id:901,queueNumber:12,destination:'اتاق شماره ۳',clips:clips(12,'doctor')},
    {id:902,queueNumber:51,destination:'بخش آزمایشگاه',clips:[]},
    {id:903,queueNumber:12,destination:'بخش رفرکشن',clips:clips(12,'refraction')},
  )
  await page.goto('/call-screen')
  await page.getByRole('button',{name:/برای فعال‌سازی صدا/}).click()
  await expect.poll(()=>state.reports.length,{timeout:25_000}).toBe(3)
  expect(state.reports.map(r=>[r.path,r.played])).toEqual([['/api/calls/901/result',true],['/api/calls/902/result',false],['/api/calls/903/result',true]])
  expect(state.reports[1].reason).toContain('شماره یا مقصد')
  expect(state.reports[1].reason.length).toBeLessThanOrEqual(200)
  await expect(page.getByRole('alert')).toContainText('شماره یا مقصد')
  await expect(page.getByRole('button',{name:'تلاش مجدد برای بارگذاری صدا'})).toBeVisible()
  expect(state.requests.filter(r=>r.path.startsWith('/api/calls/desk/'))).toEqual([])
})

test('a screen without authentication cannot read the display feed or claim audio',async({page})=>{
  const state=await fixture(page,false)
  await page.goto('/call-screen')
  await expect(page).toHaveURL(/\/login\?next=/)
  expect(state.requests.filter(r=>r.path.startsWith('/api/calls/'))).toEqual([])
})
