<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { formatDateTime } from '@/utils/date'

interface PrintField {
  label: string
  value: string | null
}

interface PrintDocument {
  title: string
  number: string
  date: string
  format: 'a4' | 'receipt'
  hospital: { hospitalName: string | null; address: string | null; phone: string | null; printFooter: string | null; logoAttachmentId: number | null }
  fields: PrintField[]
  headers: string[] | null
  rows: (string | null)[][] | null
  totals: PrintField[] | null
  note: string | null
}

const props = defineProps<{ kind: string; id: string }>()
const doc = ref<PrintDocument | null>(null)
const error = ref('')
const loading = ref(false)
const paper = ref<HTMLElement | null>(null)
const queue = computed(() => doc.value?.fields.find((f) => f.label === 'شماره نوبت' && f.value != null && f.value !== ''))
const fields = computed(() => doc.value?.fields.filter((f) => f !== queue.value) ?? [])
let request = 0
let pageStyle: HTMLStyleElement | undefined
let observer: ResizeObserver | undefined

function updatePageSize() {
  if (!pageStyle) return
  // Replacing @page while Chromium paginates can produce a spurious second page.
  if (window.matchMedia('print').matches) return
  // Measure at the actual printable roll width, not the responsive preview width.
  // Keeping this stable across beforeprint avoids a second page during pagination.
  let height = 40
  if (doc.value?.format === 'receipt' && paper.value) {
    const measurement = paper.value.cloneNode(true) as HTMLElement
    measurement.setAttribute('aria-hidden', 'true')
    measurement.style.cssText = 'position:absolute;inset:auto;left:-10000px;visibility:hidden;width:72mm;max-width:72mm;height:auto;min-height:0;margin:0;padding:0;border:0;box-shadow:none'
    paper.value.parentElement?.append(measurement)
    height = Math.ceil(measurement.scrollHeight * 25.4 / 96 + 8)
    measurement.remove()
  }
  pageStyle.textContent = doc.value?.format === 'receipt'
    ? `@page { size: 80mm ${Math.max(40, height)}mm; margin: 4mm; }`
    : '@page { size: A4; margin: 14mm; }'
}

async function load() {
  const current = ++request
  loading.value = true
  doc.value = null
  error.value = ''
  try {
    const result = await api<PrintDocument>(`/print/${props.kind}/${props.id}`)
    if (current !== request) return
    doc.value = result
    await nextTick()
    observer?.disconnect()
    if (paper.value) observer?.observe(paper.value)
    updatePageSize()
  } catch (cause) {
    if (current === request) error.value = errorMessage(cause)
  } finally {
    if (current === request) loading.value = false
  }
}

onMounted(() => {
  pageStyle = document.createElement('style')
  document.head.append(pageStyle)
  observer = new ResizeObserver(updatePageSize)
  window.addEventListener('beforeprint', updatePageSize)
  void load()
})
watch(() => [props.kind, props.id], load)
onBeforeUnmount(() => {
  request++
  observer?.disconnect()
  pageStyle?.remove()
  window.removeEventListener('beforeprint', updatePageSize)
})

async function print() {
  await document.fonts.ready
  await Promise.all(Array.from(paper.value?.querySelectorAll('img') ?? []).map((image) => image.decode().catch(() => undefined)))
  updatePageSize()
  window.print()
}
</script>

<template>
  <main class="print-screen" dir="rtl" :aria-busy="loading">
    <section class="print-actions" aria-label="ابزار سند">
      <div class="print-actions__intro"><span class="print-actions__icon" aria-hidden="true"><i class="ri-file-paper-2-line"></i></span><div><h1>پیش‌نمایش سند</h1><p>{{ doc ? (doc.format === 'receipt' ? 'رسید حرارتی · عرض ۸۰ میلی‌متر' : 'سند رسمی · کاغذ A4') : 'آماده‌سازی برای چاپ' }}</p></div></div>
      <button type="button" class="btn print-button" :disabled="!doc || loading" @click="print"><i class="ri-printer-line" aria-hidden="true"></i> چاپ / ذخیره PDF</button>
      <p class="print-actions__hint">برای ذخیره فایل، در پنجره چاپ گزینهٔ «Save as PDF» را انتخاب کنید.</p>
    </section>
    <div v-if="loading" class="print-state" role="status"><span class="spinner-border spinner-border-sm" aria-hidden="true"></span><p>سند در حال آماده‌سازی است…</p></div>
    <div v-else-if="error" class="print-state" role="alert"><h2>سند باز نشد</h2><p>{{ error }}</p><button type="button" class="btn btn-outline-primary" @click="load">تلاش دوباره</button></div>

    <article v-if="doc" ref="paper" class="print-doc" :class="`print-doc--${doc.format}`">
      <header class="print-doc__head">
        <img v-if="doc.hospital.logoAttachmentId" :src="`/api/attachments/${doc.hospital.logoAttachmentId}`" alt="لوگو" class="print-doc__logo" />
        <div class="print-doc__brand"><div class="print-doc__hospital">{{ doc.hospital.hospitalName || 'شفاخانه تخصصی چشم الشفا' }}</div>
        <div v-if="doc.hospital.address" class="print-doc__contact">{{ doc.hospital.address }}</div>
        <div v-if="doc.hospital.phone" class="print-doc__contact"><bdi dir="auto">{{ doc.hospital.phone }}</bdi></div></div>
      </header>

      <section class="print-doc__heading"><h2 class="print-doc__title">{{ doc.title }}</h2>
      <div class="print-doc__meta">
        <span>شماره سند <bdi dir="auto">{{ doc.number }}</bdi></span>
        <span>تاریخ صدور <bdi dir="auto">{{ formatDateTime(doc.date) }}</bdi></span>
      </div></section>
      <section v-if="queue" class="print-doc__queue"><span>{{ queue.label }}</span><strong><bdi>{{ queue.value }}</bdi></strong></section>

      <dl class="print-doc__fields">
        <div v-for="(field, index) in fields" :key="`${field.label}-${index}`" class="print-doc__field" :class="{ 'print-doc__field--identity': field.label === 'مریض' || field.label === 'نام' }"><dt>{{ field.label }}</dt><dd><bdi dir="auto">{{ field.value == null || field.value === '' ? '—' : field.value }}</bdi></dd></div>
      </dl>

      <table v-if="doc.headers && doc.rows" class="print-doc__table">
        <caption class="visually-hidden">{{ doc.title }} — جزئیات</caption>
        <thead><tr><th v-for="(h, index) in doc.headers" :key="index" scope="col">{{ h }}</th></tr></thead>
        <tbody><tr v-for="(row, i) in doc.rows" :key="i"><td v-for="(cell, j) in row" :key="j"><bdi dir="auto">{{ cell == null || cell === '' ? '—' : cell }}</bdi></td></tr></tbody>
      </table>

      <dl v-if="doc.totals?.length" class="print-doc__totals">
        <div v-for="(total, index) in doc.totals" :key="`${total.label}-${index}`"><dt>{{ total.label }}</dt><dd><bdi dir="auto">{{ total.value == null || total.value === '' ? '—' : total.value }}</bdi></dd></div>
      </dl>

      <p v-if="doc.note" class="print-doc__note">{{ doc.note }}</p>
      <footer v-if="doc.hospital.printFooter" class="print-doc__footer">{{ doc.hospital.printFooter }}</footer>
    </article>
  </main>
</template>

<style scoped>

.print-screen { --paper-ink: #233c43; --paper-muted: #52656a; --paper-rule: #dce6e8; min-height: 100vh; padding: 32px 24px 56px; background: #edf3f4; color: var(--paper-ink); }
.print-actions { max-width: 794px; margin: 0 auto 28px; display: flex; flex-wrap: wrap; gap: 12px 20px; align-items: center; justify-content: space-between; }
.print-actions__intro { display: flex; gap: 12px; align-items: center; }
.print-actions__icon { width: 44px; height: 48px; display: grid; place-items: center; background: #fff; color: #087f89; border: 1px solid #d7e5e7; border-radius: 12px; font-size: 23px; }
.print-actions h1 { margin: 0 0 5px; font-size: 18px; font-weight: 700; color: #233c43; }
.print-actions p { margin: 0; font-size: 12px; color: #52656a; }
.print-actions__hint { flex-basis: 100%; }
.print-button { display: inline-flex; align-items: center; justify-content: center; gap: 8px; padding: 11px 18px; background: #087f89; border: 1px solid #087f89; border-radius: 9px; font-weight: 600; color: #fff; }
.print-button:hover:not(:disabled) { background: #06656d; color: #fff; }
.print-button:focus-visible { outline: 3px solid #83cbd0; outline-offset: 3px; }
.print-button:disabled { opacity: .5; color: #fff; }
.print-state { max-width: 500px; margin: 64px auto; text-align: center; padding: 32px; background: #fff; border: 1px solid #dce6e8; border-radius: 12px; }
.print-state p { margin: 14px 0; overflow-wrap: anywhere; }
.print-state h2 { font-size: 18px; }
.print-doc { box-sizing: border-box; width: 100%; margin: 0 auto; background: #fff; padding: 38px 42px 30px; border: 1px solid #e1e9eb; border-top: 3px solid #07828c; border-radius: 3px; box-shadow: 0 12px 40px #254d5910, 0 2px 4px #254d5908; color: var(--paper-ink); font-size: 13px; line-height: 1.8; overflow-wrap: anywhere; }
.print-doc--a4 { max-width: 794px; min-height: 900px; }
.print-doc__head { display: flex; gap: 16px; align-items: center; padding-bottom: 24px; border-bottom: 1px solid var(--paper-rule); text-align: start; }
.print-doc__logo { width: 60px; max-height: 70px; object-fit: contain; flex-shrink: 0; }
.print-doc__brand { min-width: 0; }
.print-doc__hospital { font-size: 20px; font-weight: 750; line-height: 1.65; }
.print-doc__contact { margin-top: 3px; font-size: 11px; color: var(--paper-muted); white-space: pre-line; }
.print-doc__heading { padding: 25px 0 22px; display: flex; gap: 16px; align-items: flex-start; justify-content: space-between; }
.print-doc__title { margin: 0; color: var(--paper-ink); font-size: 26px; font-weight: 750; line-height: 1.6; text-align: start; }
.print-doc__meta { margin: 0; display: grid; gap: 5px; font-size: 11px; min-width: 190px; color: var(--paper-muted); }
.print-doc__meta > span { display: flex; justify-content: space-between; gap: 14px; }
.print-doc__meta bdi { color: var(--paper-ink); text-align: end; font-variant-numeric: tabular-nums; }
.print-doc dt { font-weight: 400; color: var(--paper-muted); }
.print-doc dd { margin: 0; min-width: 0; }
.print-doc__fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); margin: 0 0 26px; padding: 18px 20px; gap: 14px 24px; background: #f5f9f9; border-radius: 6px; }
.print-doc__field dt { margin-bottom: 4px; font-size: 11px; }
.print-doc__field dd { font-size: 13px; font-weight: 600; }
.print-doc__field--identity dd { font-size: 16px; }
.print-doc__queue { padding: 12px 0 18px; margin: 0 0 18px; text-align: center; border-bottom: 1px dashed var(--paper-rule); }
.print-doc__queue > span { font-size: 11px; color: var(--paper-muted); }
.print-doc__queue strong { display: block; font-size: 42px; font-weight: 750; line-height: 1.4; letter-spacing: 1px; }
.print-doc__table { width: 100%; border-collapse: collapse; table-layout: fixed; margin: 0 0 24px; font-size: 12px; }
.print-doc__table th { padding: 10px 6px; border: 0; border-block: 1px solid var(--paper-rule); background: #f8fafa; font-weight: 600; font-size: 11px; text-align: start; }
.print-doc__table td { padding: 13px 6px; border: 0; border-bottom: 1px solid #e8eef0; vertical-align: top; text-align: start; }
.print-doc__table th:first-child:not(:only-child) { width: 36%; }
.print-doc__totals { width: 46%; margin: 10px 0 26px; margin-inline-start: auto; font-variant-numeric: tabular-nums; }
.print-doc__totals > div { display: flex; gap: 14px; justify-content: space-between; padding: 7px 0; }
.print-doc__totals dd { font-weight: 650; text-align: end; }
.print-doc__totals > div:last-child { border-top: 1px solid #9eb4ba; margin-top: 7px; padding-top: 12px; font-size: 15px; }
.print-doc__totals > div:last-child dt { color: var(--paper-ink); font-weight: 650; }
.print-doc__note { padding: 15px 0; border-top: 1px solid var(--paper-rule); white-space: pre-wrap; }
.print-doc__footer { padding-top: 16px; margin-top: 26px; border-top: 1px solid var(--paper-rule); color: var(--paper-muted); font-size: 11px; white-space: pre-wrap; text-align: center; }
.print-doc--receipt { width: 80mm; max-width: 100%; padding: 6mm 4mm; font-size: 12px; }
.print-doc--receipt .print-doc__head { flex-direction: column; gap: 6px; padding-bottom: 15px; text-align: center; }
.print-doc--receipt .print-doc__logo { width: 42px; max-height: 48px; }
.print-doc--receipt .print-doc__hospital { font-size: 16px; }
.print-doc--receipt .print-doc__contact { font-size: 10px; }
.print-doc--receipt .print-doc__heading { display: block; padding: 16px 0; }
.print-doc--receipt .print-doc__title { margin-bottom: 12px; text-align: center; font-size: 18px; }
.print-doc--receipt .print-doc__meta { min-width: 0; font-size: 10px; }
.print-doc--receipt .print-doc__fields { display: block; background: none; border-radius: 0; padding: 0; margin-bottom: 18px; }
.print-doc--receipt .print-doc__field { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; padding: 5px 0; }
.print-doc--receipt .print-doc__field dt { flex-shrink: 0; margin: 0; font-size: 11px; }
.print-doc--receipt .print-doc__field dd { text-align: end; font-size: 12px; }
.print-doc--receipt .print-doc__field--identity dd { font-size: 14px; }
.print-doc--receipt .print-doc__table { font-size: 11px; margin-bottom: 16px; }
.print-doc--receipt .print-doc__table th, .print-doc--receipt .print-doc__table td { padding: 8px 3px; }
.print-doc--receipt .print-doc__table th:first-child { width: 45%; }
.print-doc--receipt .print-doc__totals { width: 100%; margin-bottom: 14px; padding-top: 10px; border-top: 1px dashed #9eb4ba; }
.print-doc--receipt .print-doc__totals > div { gap: 10px; }
.print-doc--receipt .print-doc__totals > div:first-child dd { font-size: 16px; }
.print-doc--receipt .print-doc__totals > div:last-child { font-size: 12px; }
.print-doc--receipt .print-doc__footer { font-size: 10px; margin-top: 18px; padding-top: 12px; }
@media (max-width: 600px) {
  .print-screen { padding: 20px 14px 32px; }
  .print-actions { gap: 14px; margin-bottom: 22px; }
  .print-button { width: 100%; }
  .print-actions__hint { font-size: 11px !important; }
  .print-doc--a4 { padding: 24px 18px; min-height: 0; }
  .print-doc--a4 .print-doc__head { gap: 10px; padding-bottom: 18px; }
  .print-doc--a4 .print-doc__hospital { font-size: 16px; }
  .print-doc--a4 .print-doc__logo { width: 44px; }
  .print-doc--a4 .print-doc__heading { display: block; padding: 18px 0; }
  .print-doc--a4 .print-doc__title { font-size: 23px; margin-bottom: 12px; }
  .print-doc__fields { padding: 14px; gap: 14px; }
  .print-doc__totals { width: 100%; }
  .print-doc__table { font-size: 11px; }
  .print-doc__table th, .print-doc__table td { padding-inline: 4px; }
}
@media print {
  .print-screen {
    min-height: 0;
    padding: 0;
    background: #fff;
    color: #000;
    --paper-ink: #000;
    --paper-muted: #333;
    --paper-rule: #aaa;
  }

  .print-actions, .print-state {
    display: none !important;
  }

  .print-doc {
    max-width: none;
    margin: 0;
    padding: 0;
    border: 0;
    border-radius: 0;
    box-shadow: none;
    color: #000;
  }
  .print-doc--a4 { width: 100%; min-height: 0; }
  .print-doc--receipt { width: 72mm; max-width: 72mm; margin: 0 auto; padding: 0; }
  .print-doc__fields, .print-doc__table th { background: transparent; }
  .print-doc__head, .print-doc__heading, .print-doc__queue, .print-doc__field, .print-doc__totals, .print-doc__footer { break-inside: avoid; }
  .print-doc__table thead { display: table-header-group; }
  .print-doc__table tr { break-inside: avoid; }
  .print-doc__note { orphans: 3; widows: 3; }
}
</style>
