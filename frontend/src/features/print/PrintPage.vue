<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { formatDateTime } from '@/utils/date'

interface PrintField { label: string; value: string | null }
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
type PaperSize = 'receipt-80' | 'receipt-58' | 'a5' | 'a4'

const props = defineProps<{ kind: string; id: string }>()
const doc = ref<PrintDocument | null>(null)
const error = ref('')
const loading = ref(false)
const paper = ref<HTMLElement | null>(null)
const selectedPaper = ref<PaperSize>('a5')
const hasValue = (value: string | null | undefined) => value != null && value.trim() !== ''
const queue = computed(() => doc.value?.fields.find((field) => field.label === 'شماره نوبت' && hasValue(field.value)))
const fields = computed(() => doc.value?.fields.filter((field) => field !== queue.value && hasValue(field.value)) ?? [])
const totals = computed(() => doc.value?.totals?.filter((total) => hasValue(total.value)) ?? [])
const isReceipt = computed(() => doc.value?.format === 'receipt')
const isMedicinePrescription = computed(() => props.kind === 'rx')
const prescriptionItems = computed(() => doc.value?.rows?.map((row) => ({
  name: row[0],
  details: row.slice(1).map((value, index) => ({ label: doc.value?.headers?.[index + 1] ?? '', value })).filter((item) => hasValue(item.value)),
})) ?? [])
const paperOptions = computed(() => isReceipt.value
  ? [{ value: 'receipt-80' as const, label: 'رول حرارتی ۸۰ میلی‌متر' }, { value: 'receipt-58' as const, label: 'رول حرارتی ۵۸ میلی‌متر' }]
  : [{ value: 'a5' as const, label: 'A5 عمودی' }, { value: 'a4' as const, label: 'A4 عمودی' }])
const paperLabel = computed(() => paperOptions.value.find((option) => option.value === selectedPaper.value)?.label ?? '')
const logoSrc = '/print/alshefa-official-logo.png'
let request = 0
let pageStyle: HTMLStyleElement | undefined
let observer: ResizeObserver | undefined

function updatePageSize() {
  if (!pageStyle || window.matchMedia('print').matches) return
  if (!isReceipt.value) {
    pageStyle.textContent = selectedPaper.value === 'a4' ? '@page { size: A4 portrait; margin: 14mm; }' : '@page { size: A5 portrait; margin: 10mm; }'
    return
  }
  const roll = selectedPaper.value === 'receipt-58' ? 58 : 80
  const width = roll - 8
  let height = 40
  if (paper.value) {
    const measurement = paper.value.cloneNode(true) as HTMLElement
    measurement.setAttribute('aria-hidden', 'true')
    measurement.style.cssText = `position:absolute;left:-10000px;visibility:hidden;width:${width}mm;max-width:${width}mm;height:auto;min-height:0;margin:0;padding:0;border:0;box-shadow:none`
    paper.value.parentElement?.append(measurement)
    height = Math.ceil(measurement.scrollHeight * 25.4 / 96 + 8)
    measurement.remove()
  }
  pageStyle.textContent = `@page { size: ${roll}mm ${Math.max(40, height)}mm; margin: 4mm; }`
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
    selectedPaper.value = result.format === 'receipt' ? 'receipt-80' : 'a5'
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
watch(selectedPaper, async () => { await nextTick(); updatePageSize() })
onBeforeUnmount(() => {
  request++
  observer?.disconnect()
  pageStyle?.remove()
  window.removeEventListener('beforeprint', updatePageSize)
})

async function openPrintDialog() {
  await document.fonts.ready
  await Promise.all(Array.from(paper.value?.querySelectorAll('img') ?? []).map((image) => image.decode().catch(() => undefined)))
  updatePageSize()
  window.print()
}
</script>

<template>
  <main class="print-screen" dir="rtl" :aria-busy="loading">
    <section class="print-actions" aria-label="ابزار سند">
      <div class="print-actions__intro">
        <span class="print-actions__icon" aria-hidden="true"><i class="ri-file-paper-2-line"></i></span>
        <div><h1>پیش‌نمایش سند</h1><p>{{ doc ? paperLabel : 'آماده‌سازی برای چاپ' }}</p></div>
      </div>
      <label v-if="doc" class="paper-picker"><span>اندازه کاغذ</span><select v-model="selectedPaper" class="form-select form-select-sm"><option v-for="option in paperOptions" :key="option.value" :value="option.value">{{ option.label }}</option></select></label>
      <div class="print-actions__buttons">
        <button type="button" class="btn print-button" :disabled="!doc || loading" @click="openPrintDialog"><i class="ri-printer-line" aria-hidden="true"></i> چاپ</button>
        <button type="button" class="btn pdf-button" :disabled="!doc || loading" @click="openPrintDialog"><i class="ri-file-pdf-2-line" aria-hidden="true"></i> ذخیره به PDF</button>
      </div>
      <p class="print-actions__hint">برای PDF، در پنجره چاپ «Save as PDF» را انتخاب کنید. سربرگ و پابرگ مرورگر نیز از همان پنجره خاموش می‌شود.</p>
    </section>

    <div v-if="loading" class="print-state" role="status"><span class="spinner-border spinner-border-sm" aria-hidden="true"></span><p>سند در حال آماده‌سازی است…</p></div>
    <div v-else-if="error" class="print-state" role="alert"><h2>سند باز نشد</h2><p>{{ error }}</p><button type="button" class="btn btn-outline-primary" @click="load">تلاش دوباره</button></div>

    <article v-if="doc" ref="paper" class="print-doc" :class="[isReceipt ? 'print-doc--receipt' : 'print-doc--sheet', `print-doc--${selectedPaper}`]">
      <header class="print-doc__head">
        <img :src="logoSrc" alt="لوگوی شفاخانه تخصصی چشم الشفا" class="print-doc__logo" />
        <div v-if="doc.hospital.hospitalName || doc.hospital.address || doc.hospital.phone" class="print-doc__brand">
          <div v-if="doc.hospital.hospitalName" class="print-doc__hospital">{{ doc.hospital.hospitalName }}</div>
          <div v-if="doc.hospital.address" class="print-doc__contact">{{ doc.hospital.address }}</div>
          <div v-if="doc.hospital.phone" class="print-doc__contact"><bdi dir="auto">{{ doc.hospital.phone }}</bdi></div>
        </div>
      </header>

      <section class="print-doc__heading">
        <h2 class="print-doc__title">{{ doc.title }}</h2>
        <div class="print-doc__meta"><span><span>شماره سند</span><bdi dir="auto">{{ doc.number }}</bdi></span><span><span>تاریخ صدور</span><bdi dir="auto">{{ formatDateTime(doc.date) }}</bdi></span></div>
      </section>
      <section v-if="queue" class="print-doc__queue"><span>{{ queue.label }}</span><strong><bdi dir="auto">{{ queue.value }}</bdi></strong></section>

      <dl v-if="fields.length" class="print-doc__fields">
        <div v-for="(field, index) in fields" :key="`${field.label}-${index}`" class="print-doc__field" :class="{ 'print-doc__field--identity': field.label === 'مریض' || field.label === 'نام' }"><dt>{{ field.label }}</dt><dd><bdi dir="auto">{{ field.value }}</bdi></dd></div>
      </dl>

      <ol v-if="isMedicinePrescription && prescriptionItems.length" class="print-doc__medicines">
        <li v-for="(item, index) in prescriptionItems" :key="index">
          <strong><bdi dir="auto">{{ item.name }}</bdi></strong>
          <div v-for="detail in item.details" :key="detail.label" class="print-doc__medicine-detail"><span>{{ detail.label }}</span><bdi dir="auto">{{ detail.value }}</bdi></div>
        </li>
      </ol>
      <table v-else-if="doc.headers?.length && doc.rows?.length" class="print-doc__table">
        <caption class="visually-hidden">{{ doc.title }} — جزئیات</caption>
        <thead><tr><th v-for="(header, index) in doc.headers" :key="index" scope="col">{{ header }}</th></tr></thead>
        <tbody><tr v-for="(row, rowIndex) in doc.rows" :key="rowIndex"><td v-for="(cell, cellIndex) in row" :key="cellIndex" :data-label="doc.headers[cellIndex]"><bdi dir="auto">{{ hasValue(cell) ? cell : '—' }}</bdi></td></tr></tbody>
      </table>

      <dl v-if="totals.length" class="print-doc__totals">
        <div v-for="(total, index) in totals" :key="`${total.label}-${index}`" :class="{ 'print-doc__total--emphasis': index === 0 }"><dt>{{ total.label }}</dt><dd><bdi dir="auto">{{ total.value }}</bdi></dd></div>
      </dl>
      <p v-if="doc.note" class="print-doc__note">{{ doc.note }}</p>
    </article>
  </main>
</template>

<style scoped>
.print-screen { --paper-ink:#0a294d; --paper-muted:#52677d; --paper-rule:#c8deeb; --paper-band:#edf8fc; min-height:100vh; padding:32px 24px 56px; background:#eef2f4; color:var(--paper-ink); }
.print-actions { max-width:794px; margin:0 auto 24px; display:flex; flex-wrap:wrap; gap:12px 18px; align-items:end; justify-content:space-between; }
.print-actions__intro { display:flex; gap:12px; align-items:center; }
.print-actions__icon { width:42px; height:46px; display:grid; place-items:center; background:#fff; color:#147dac; border:1px solid #d8e2e8; border-radius:8px; font-size:22px; }
.print-actions h1 { margin:0 0 3px; font-size:18px; font-weight:700; color:#173550; }
.print-actions p { margin:0; font-size:12px; color:#5d6c78; }
.paper-picker { display:grid; gap:5px; min-width:190px; color:#4c6070; font-size:11px; }
.paper-picker .form-select { min-height:38px; border-color:#ccd9e0; color:#173550; }
.print-actions__buttons { display:flex; gap:8px; }
.print-button,.pdf-button { display:inline-flex; align-items:center; justify-content:center; gap:7px; min-height:40px; padding:9px 15px; border-radius:7px; font-weight:600; }
.print-button { background:#0a6f9d; border:1px solid #0a6f9d; color:#fff; }
.print-button:hover:not(:disabled) { background:#085b81; border-color:#085b81; color:#fff; }
.pdf-button { background:#fff; border:1px solid #aabdc8; color:#173550; }
.pdf-button:hover:not(:disabled) { background:#f6fafc; border-color:#6f8b9c; color:#0a294d; }
.print-button:focus-visible,.pdf-button:focus-visible,.paper-picker select:focus-visible { outline:3px solid #87c9e6; outline-offset:2px; box-shadow:none; }
.print-button:disabled,.pdf-button:disabled { opacity:.5; }
.print-actions__hint { flex-basis:100%; }
.print-state { max-width:500px; margin:64px auto; padding:32px; border:1px solid #dce5ea; border-radius:8px; background:#fff; text-align:center; }
.print-state p { margin:14px 0; overflow-wrap:anywhere; }
.print-state h2 { font-size:18px; }

.print-doc { box-sizing:border-box; width:100%; margin:0 auto; padding:13mm 12mm 11mm; border:1px solid #dbe5ea; background:#fff; box-shadow:0 10px 28px rgb(31 54 72 / 8%); color:var(--paper-ink); font-family:'Vazirmatn','Poppins',sans-serif; font-size:12.5px; line-height:1.75; overflow-wrap:anywhere; }
.print-doc--a5 { max-width:148mm; min-height:210mm; }
.print-doc--a4 { max-width:210mm; min-height:297mm; }
.print-doc__head { display:flex; gap:12px; align-items:center; min-height:21mm; padding-bottom:5mm; border-bottom:.3mm solid var(--paper-rule); text-align:start; }
.print-doc__logo { width:19mm; height:19mm; object-fit:contain; flex:none; }
.print-doc__brand { min-width:0; }
.print-doc__hospital { font-size:17px; font-weight:750; line-height:1.55; }
.print-doc__contact { margin-top:2px; color:var(--paper-muted); font-size:10px; white-space:pre-line; }
.print-doc__heading { padding:7mm 0 6mm; text-align:center; }
.print-doc__title { margin:0 0 4mm; color:var(--paper-ink); font-size:22px; font-weight:750; line-height:1.5; text-align:center; }
.print-doc__meta { display:grid; gap:4px; color:var(--paper-muted); font-size:10.5px; }
.print-doc__meta>span { display:flex; justify-content:space-between; gap:12px; }
.print-doc__meta bdi { color:var(--paper-ink); text-align:end; font-variant-numeric:tabular-nums; }
.print-doc dt { color:var(--paper-muted); font-weight:400; }
.print-doc dd { min-width:0; margin:0; }
.print-doc__fields { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:11px 24px; margin:0 0 7mm; padding:4mm 0; border-block:.25mm solid var(--paper-rule); }
.print-doc__field dt { margin-bottom:2px; font-size:10px; }
.print-doc__field dd { font-size:12.5px; font-weight:600; }
.print-doc__field--identity dd { font-size:14px; }
.print-doc__queue { margin:0 0 5mm; padding:0 0 5mm; border-bottom:.25mm solid var(--paper-rule); text-align:center; }
.print-doc__queue>span { color:var(--paper-muted); font-size:10px; }
.print-doc__queue strong { display:block; font-size:34px; font-weight:750; line-height:1.35; letter-spacing:1px; }
.print-doc__table { width:100%; margin:0 0 6mm; border-collapse:collapse; table-layout:fixed; font-size:11.5px; }
.print-doc__table th { padding:8px 6px; border-block:.25mm solid var(--paper-rule); background:transparent; font-size:10.5px; font-weight:650; text-align:start; }
.print-doc__table td { padding:10px 6px; border-bottom:.25mm solid #dfebf1; vertical-align:top; text-align:start; }
.print-doc__table th:first-child:not(:only-child) { width:36%; }
.print-doc__medicines { margin:0 0 6mm; padding-inline-start:7mm; }
.print-doc__medicines li { padding:3mm 1mm; border-bottom:.25mm solid var(--paper-rule); break-inside:avoid; }
.print-doc__medicines strong { display:block; font-size:13px; font-weight:650; }
.print-doc__medicine-detail { display:flex; gap:4mm; align-items:baseline; padding:1mm 0; font-size:11.5px; }
.print-doc__medicine-detail span { min-width:22mm; color:var(--paper-muted); }
.print-doc__medicine-detail bdi { min-width:0; }
.print-doc__totals { width:min(100%,75mm); margin:2mm 0 7mm; margin-inline-start:auto; font-variant-numeric:tabular-nums; }
.print-doc__totals>div { display:flex; gap:14px; justify-content:space-between; padding:7px 10px; }
.print-doc__totals dd { font-weight:650; text-align:end; }
.print-doc__totals .print-doc__total--emphasis { margin-bottom:4px; border-block:.25mm solid #d5e9f2; background:var(--paper-band); font-size:14px; }
.print-doc__totals .print-doc__total--emphasis dt { color:var(--paper-ink); font-weight:650; }
.print-doc__note { padding:4mm 0; border-top:.25mm solid var(--paper-rule); white-space:pre-wrap; }
.print-doc__footer { margin-top:7mm; padding-top:4mm; border-top:.25mm solid var(--paper-rule); color:var(--paper-muted); font-size:10px; text-align:center; white-space:pre-wrap; }

.print-doc--receipt { width:80mm; max-width:100%; min-height:0; padding:6mm 5mm 5mm; font-size:11px; }
.print-doc--receipt-58 { width:58mm; padding-inline:4mm; }
.print-doc--receipt .print-doc__head { justify-content:center; min-height:0; padding-bottom:4mm; }
.print-doc--receipt .print-doc__logo { width:16mm; height:16mm; }
.print-doc--receipt .print-doc__brand { flex:1; }
.print-doc--receipt .print-doc__hospital { font-size:13px; }
.print-doc--receipt .print-doc__contact { font-size:8.5px; }
.print-doc--receipt .print-doc__heading { display:block; padding:5mm 0 4mm; }
.print-doc--receipt .print-doc__title { margin-bottom:3mm; font-size:17px; text-align:center; }
.print-doc--receipt .print-doc__meta { min-width:0; gap:2px; font-size:9px; }
.print-doc--receipt .print-doc__fields { display:block; margin-bottom:4mm; padding:3mm 0; }
.print-doc--receipt .print-doc__field { display:flex; align-items:baseline; justify-content:space-between; gap:8px; padding:3px 0; }
.print-doc--receipt .print-doc__field dt { flex:none; margin:0; font-size:9.5px; }
.print-doc--receipt .print-doc__field dd { font-size:11px; text-align:end; }
.print-doc--receipt .print-doc__field--identity dd { font-size:12.5px; }
.print-doc--receipt .print-doc__queue { padding-bottom:3mm; margin-bottom:3mm; }
.print-doc--receipt .print-doc__queue strong { font-size:30px; }
.print-doc--receipt .print-doc__table { margin-bottom:4mm; font-size:9.5px; }
.print-doc--receipt .print-doc__table th,.print-doc--receipt .print-doc__table td { padding:6px 3px; }
.print-doc--receipt .print-doc__table th:first-child { width:44%; }
.print-doc--receipt .print-doc__totals { width:100%; margin-bottom:4mm; }
.print-doc--receipt .print-doc__totals>div { padding:5px 7px; }
.print-doc--receipt .print-doc__totals .print-doc__total--emphasis { font-size:13px; }
.print-doc--receipt .print-doc__footer { margin-top:4mm; padding-top:3mm; font-size:8.5px; }
.print-doc--receipt-58 .print-doc__head { display:block; text-align:center; }
.print-doc--receipt-58 .print-doc__logo { width:14mm; height:14mm; }
.print-doc--receipt-58 .print-doc__brand { margin-top:2mm; }
.print-doc--receipt-58 .print-doc__hospital { font-size:11px; }
.print-doc--receipt-58 .print-doc__title { font-size:15px; }
.print-doc--receipt-58 .print-doc__table,.print-doc--receipt-58 .print-doc__table tbody,.print-doc--receipt-58 .print-doc__table tr,.print-doc--receipt-58 .print-doc__table td { display:block; width:100%; }
.print-doc--receipt-58 .print-doc__table thead { display:none; }
.print-doc--receipt-58 .print-doc__table tr { padding:2mm 0; border-bottom:.25mm solid var(--paper-rule); }
.print-doc--receipt-58 .print-doc__table td { display:grid; grid-template-columns:minmax(16mm,auto) 1fr; gap:5px; padding:2px 0; border:0; }
.print-doc--receipt-58 .print-doc__table td::before { content:attr(data-label); color:var(--paper-muted); font-size:8.5px; }
.print-doc--receipt-58 .print-doc__table td bdi { min-width:0; text-align:end; }

@media (max-width:650px) {
  .print-screen { padding:18px 12px 32px; }
  .print-actions { align-items:stretch; margin-bottom:20px; }
  .print-actions__intro,.paper-picker,.print-actions__buttons { width:100%; }
  .print-actions__buttons>button { flex:1; }
  .print-doc--a5,.print-doc--a4 { min-height:0; padding:22px 16px; }
  .print-doc:not(.print-doc--receipt) .print-doc__head { gap:8px; }
  .print-doc:not(.print-doc--receipt) .print-doc__logo { width:52px; height:52px; }
  .print-doc:not(.print-doc--receipt) .print-doc__hospital { font-size:14px; }
  .print-doc:not(.print-doc--receipt) .print-doc__heading { display:block; padding:18px 0; }
  .print-doc:not(.print-doc--receipt) .print-doc__title { margin-bottom:10px; font-size:20px; }
  .print-doc__fields { grid-template-columns:1fr; }
  .print-doc__totals { width:100%; }
  .print-doc__table { font-size:10px; }
  .print-doc__table th,.print-doc__table td { padding-inline:3px; }
}
@media print {
  .print-screen { min-height:0; padding:0; background:#fff; color:#000; }
  .print-actions,.print-state { display:none!important; }
  .print-doc { max-width:none; min-height:0; margin:0; padding:0; border:0; box-shadow:none; }
  .print-doc--receipt-80 { width:72mm; max-width:72mm; margin:0 auto; }
  .print-doc--receipt-58 { width:50mm; max-width:50mm; margin:0 auto; }
  .print-doc__head,.print-doc__heading,.print-doc__queue,.print-doc__field,.print-doc__totals,.print-doc__footer { break-inside:avoid; }
  .print-doc__table thead { display:table-header-group; }
  .print-doc__table tr { break-inside:avoid; }
  .print-doc--receipt-58 .print-doc__table thead { display:none; }
  .print-doc__note { orphans:3; widows:3; }
  .print-doc__totals .print-doc__total--emphasis { border:.25mm solid #777; background:#f2f2f2!important; print-color-adjust:exact; }
}
</style>
