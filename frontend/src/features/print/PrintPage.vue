<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { faDigits } from '@/utils/date'
import { shamsiPaper } from './paper'

interface PrintField { label: string; value: string | null }
interface PrintDocument {
  title: string
  number: string
  date: string
  format: 'a4' | 'receipt'
  hospital: { hospitalName: string | null }
  fields: PrintField[]
  headers: string[] | null
  rows: (string | null)[][] | null
  totals: PrintField[] | null
  note: string | null
  subtitle: string | null
  numberLabel: string | null
  highlight: PrintField | null
  signer: string | null
  signerTitle: string | null
}
type PaperSize = 'receipt-80' | 'receipt-58' | 'a5' | 'a4'

/**
 * Every receipt, invoice and prescription follows the approved reference (alshefa_print_bundle/ALSHEFA_PRINT_STYLE_REFERENCE.png).
 * The reference card is 348 px wide; each size is drawn in that card's units (--u), so a roll or an A5 sheet keeps the same
 * proportions, spacing and type scale as the card it copies.
 */
const PAPER: Record<PaperSize, { width: number; label: string }> = {
  'receipt-80': { width: 80, label: 'رول حرارتی ۸۰ میلی‌متر' },
  'receipt-58': { width: 58, label: 'رول حرارتی ۵۸ میلی‌متر' },
  a5: { width: 148, label: 'A5 عمودی' },
  a4: { width: 210, label: 'A4 عمودی' },
}

const props = defineProps<{ kind: string; id: string }>()
const doc = ref<PrintDocument | null>(null)
const error = ref('')
const loading = ref(false)
const paper = ref<HTMLElement | null>(null)
const selectedPaper = ref<PaperSize>('a5')

const filled = (value: string | null | undefined): value is string => value != null && value.trim() !== ''
/** Amounts, counts and dates read in Dari digits as on the reference; document numbers and names keep their own letters. */
const show = (value: string) => (/[A-Za-z]/.test(value) ? value : faDigits(value))

const isReceipt = computed(() => doc.value?.format === 'receipt')
const layout = computed(() => (props.kind === 'rx' ? 'rx' : doc.value?.headers?.length && doc.value.rows?.length ? 'table' : 'card'))
const hospitalName = computed(() => doc.value?.hospital.hospitalName?.trim() || 'شفاخانه تخصصی چشم الشفا')
const dateText = computed(() => faDigits(shamsiPaper(doc.value?.date)))
const numberLabel = computed(() => doc.value?.numberLabel ?? 'شماره سند')
const fields = computed(() => doc.value?.fields.filter((field) => filled(field.value)) ?? [])
const totals = computed(() => doc.value?.totals?.filter((total) => filled(total.value)) ?? [])
const band = computed(() => totals.value[0])
const highlight = computed(() => (filled(doc.value?.highlight?.value) ? doc.value!.highlight : null))

/** The facts a card prints in its rows: number and date first, as on the reference, then the document's own. */
const cardRows = computed(() => [
  { label: numberLabel.value, value: doc.value?.number ?? '' },
  { label: 'تاریخ', value: dateText.value },
  ...fields.value.map((field) => ({ label: field.label, value: show(field.value!) })),
])

const columnClass = (header: string) =>
  header === '#' ? 'pd-col-index' : header === 'مبلغ' ? 'pd-col-amount' : header === 'مقدار' ? 'pd-col-qty' : 'pd-col-text'
const cell = (header: string, value: string | null) => (!filled(value) ? '—' : header === '#' ? value : show(value))

/** Each line of a prescription: the drug, then dose, route and instructions over the course, exactly as recorded. */
const medicines = computed(() =>
  (doc.value?.rows ?? []).map(([name, dose, route, duration, instructions, quantity]) => {
    let detail = [dose, route, instructions].filter(filled).join(', ')
    if (filled(duration)) detail += `${detail ? ' × ' : ''}${duration}`
    if (filled(quantity) && quantity !== '1') detail += `${detail ? ' · ' : ''}Qty ${quantity}`
    return { name: name ?? '', detail }
  }),
)
const signer = computed(() => {
  const name = doc.value?.signer?.trim()
  if (!name) return null
  return /^(دکتر|داکتر|dr\.?)\s/i.test(name) ? name : `دکتر ${name}`
})

const paperOptions = computed(() =>
  (isReceipt.value ? (['receipt-80', 'receipt-58'] as const) : (['a5', 'a4'] as const)).map((value) => ({ value, label: PAPER[value].label })),
)
const paperStyle = computed(() => ({ '--paper-w': `${PAPER[selectedPaper.value].width}mm` }))
const logoSrc = '/print/alshefa-official-logo.png'

let request = 0
let pageStyle: HTMLStyleElement | undefined
let observer: ResizeObserver | undefined

/** Margins are part of the design, so the page itself has none; a roll is cut to the length of what it carries. */
function updatePageSize() {
  if (!pageStyle || window.matchMedia('print').matches) return
  if (!isReceipt.value) {
    pageStyle.textContent = `@page { size: ${selectedPaper.value === 'a4' ? 'A4' : 'A5'} portrait; margin: 0; }`
    return
  }
  const width = PAPER[selectedPaper.value].width
  const height = paper.value ? Math.ceil((paper.value.scrollHeight * 25.4) / 96) + 2 : 100
  pageStyle.textContent = `@page { size: ${width}mm ${Math.max(60, height)}mm; margin: 0; }`
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
watch(selectedPaper, async () => {
  await nextTick()
  updatePageSize()
})
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
        <div><h1>پیش‌نمایش سند</h1><p>{{ doc ? PAPER[selectedPaper].label : 'آماده‌سازی برای چاپ' }}</p></div>
      </div>
      <label v-if="doc" class="paper-picker"><span>اندازه کاغذ</span><select v-model="selectedPaper" class="form-select form-select-sm"><option v-for="option in paperOptions" :key="option.value" :value="option.value">{{ option.label }}</option></select></label>
      <div class="print-actions__buttons">
        <button type="button" class="btn print-button" :disabled="!doc || loading" @click="openPrintDialog"><i class="ri-printer-line" aria-hidden="true"></i> چاپ</button>
        <button type="button" class="btn pdf-button" :disabled="!doc || loading" @click="openPrintDialog"><i class="ri-file-pdf-2-line" aria-hidden="true"></i> ذخیره به PDF</button>
      </div>
      <p class="print-actions__hint">برای PDF، در پنجره چاپ «Save as PDF» را انتخاب کنید. «Margins» را روی «None» و «Background graphics» را روشن بگذارید.</p>
    </section>

    <div v-if="loading" class="print-state" role="status"><span class="spinner-border spinner-border-sm" aria-hidden="true"></span><p>سند در حال آماده‌سازی است…</p></div>
    <div v-else-if="error" class="print-state" role="alert"><h2>سند باز نشد</h2><p>{{ error }}</p><button type="button" class="btn btn-outline-primary" @click="load">تلاش دوباره</button></div>

    <article v-if="doc" ref="paper" class="pd" :class="[`pd--${selectedPaper}`, `pd--${layout}`, { 'pd--roll': isReceipt }]" :style="paperStyle">
      <header class="pd-head">
        <div class="pd-brand">
          <div class="pd-brand__fa">{{ hospitalName }}</div>
          <div class="pd-brand__en" dir="ltr">AL-SHEFA Specialist Eye Hospital</div>
        </div>
        <img :src="logoSrc" alt="لوگوی شفاخانه تخصصی چشم الشفا" class="pd-logo" />
      </header>
      <hr class="pd-rule pd-rule--head" />

      <h2 class="pd-title">{{ doc.title }}</h2>
      <p v-if="doc.subtitle" class="pd-subtitle"><bdi>{{ doc.subtitle }}</bdi></p>

      <!-- Prescription: name and date in a shaded box, then the recorded medicines under the Rx mark. -->
      <template v-if="layout === 'rx'">
        <div class="pd-box">
          <div v-for="field in fields" :key="field.label" class="pd-inline"><span>{{ field.label }}:</span><bdi>{{ field.value }}</bdi></div>
          <div class="pd-inline"><span>تاریخ:</span><bdi>{{ dateText }}</bdi></div>
        </div>
        <div class="pd-rx" dir="ltr" aria-label="Rx"><span>R</span><span class="pd-rx__x">x</span></div>
        <hr class="pd-rule pd-rule--rx" />
        <ol class="pd-medicines" dir="ltr">
          <li v-for="(item, index) in medicines" :key="index">
            <div class="pd-medicines__name"><bdi>{{ item.name }}</bdi></div>
            <div v-if="item.detail" class="pd-medicines__detail"><bdi>{{ item.detail }}</bdi></div>
          </li>
        </ol>
        <p v-if="doc.note" class="pd-note">{{ doc.note }}</p>
        <footer v-if="signer" class="pd-signer">
          <div class="pd-signer__name">{{ signer }}</div>
          <div v-if="doc.signerTitle" class="pd-signer__title">{{ doc.signerTitle }}</div>
        </footer>
      </template>

      <!-- Invoice: the lines in a ruled table, the total band, then number, date and anything still owed. -->
      <template v-else-if="layout === 'table'">
        <div v-if="highlight" class="pd-box pd-box--single"><div class="pd-inline"><span>{{ highlight.label }}:</span><bdi>{{ highlight.value }}</bdi></div></div>
        <table class="pd-table">
          <caption class="visually-hidden">{{ doc.title }}</caption>
          <thead><tr><th v-for="header in doc.headers" :key="header" scope="col" :class="columnClass(header)">{{ header }}</th></tr></thead>
          <tbody>
            <tr v-for="(row, rowIndex) in doc.rows" :key="rowIndex">
              <td v-for="(value, cellIndex) in row" :key="cellIndex" :class="columnClass(doc.headers![cellIndex])" :data-label="doc.headers![cellIndex]"><bdi>{{ cell(doc.headers![cellIndex], value) }}</bdi></td>
            </tr>
          </tbody>
        </table>
        <div v-if="band" class="pd-band pd-band--table"><span class="pd-band__label">{{ band.label }}</span><bdi class="pd-band__value">{{ show(band.value!) }}</bdi></div>
        <dl class="pd-foot">
          <div v-for="row in cardRows" :key="row.label"><dt>{{ row.label }}:</dt><dd><bdi>{{ row.value }}</bdi></dd></div>
          <div v-for="total in totals.slice(1)" :key="total.label"><dt>{{ total.label }}:</dt><dd><bdi>{{ show(total.value!) }}</bdi></dd></div>
        </dl>
      </template>

      <!-- Receipt card: number, date and patient, a rule, and the amount band. -->
      <template v-else>
        <div v-if="highlight" class="pd-box pd-box--single"><div class="pd-inline"><span>{{ highlight.label }}:</span><bdi>{{ highlight.value }}</bdi></div></div>
        <dl class="pd-rows">
          <div v-for="row in cardRows" :key="row.label"><dt>{{ row.label }}:</dt><dd><bdi>{{ row.value }}</bdi></dd></div>
        </dl>
        <p v-if="doc.note" class="pd-note">{{ doc.note }}</p>
        <template v-if="band">
          <hr class="pd-rule pd-rule--foot" />
          <div class="pd-band"><span class="pd-band__label">{{ band.label }}:</span><bdi class="pd-band__value">{{ show(band.value!) }}</bdi></div>
          <dl v-if="totals.length > 1" class="pd-rows pd-rows--after">
            <div v-for="total in totals.slice(1)" :key="total.label"><dt>{{ total.label }}:</dt><dd><bdi>{{ show(total.value!) }}</bdi></dd></div>
          </dl>
        </template>
      </template>
    </article>
  </main>
</template>

<style scoped>
.print-screen { min-height:100vh; padding:32px 24px 56px; background:#eef2f6; color:#1b2a56; }
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

/*
 * The document. Every length is a multiple of --u, one pixel of the 348 px reference card, measured from it:
 * header 52, rule at 103, title 25 bold, subtitle 17, rows 16 on a 33.5 pitch, band 44 high with a 4 radius.
 */
.pd {
  --u: calc(var(--paper-w) / 348);
  --ink: #1b2a56;
  --text: #27324f;
  --rule: #c5d8e8;
  --band: #d8e7f3;
  --shade: #f0f4f8;
  --grid: #c8d5e2;
  --head: #eaf0f6;
  box-sizing: border-box;
  width: var(--paper-w);
  max-width: 100%;
  margin: 0 auto;
  padding: calc(var(--u) * 26) calc(var(--u) * 27) calc(var(--u) * 32);
  display: flex;
  flex-direction: column;
  background: #fff;
  color: var(--text);
  font-family: 'Vazirmatn', Arial, sans-serif;
  font-size: calc(var(--u) * 16);
  line-height: 1.5;
  overflow-wrap: anywhere;
  box-shadow: 0 6px 22px rgb(27 42 86 / 9%);
  border-radius: calc(var(--u) * 3);
}
.pd--a5 { min-height: 210mm; }
.pd--a4 { --u: calc(148mm / 348 * 1.15); min-height: 297mm; }
.pd * { box-sizing: border-box; }
.pd bdi { unicode-bidi: isolate; }

.pd-head { display: flex; align-items: center; justify-content: center; gap: calc(var(--u) * 13); min-height: calc(var(--u) * 52); }
.pd-brand { min-width: 0; text-align: right; }
.pd-brand__fa { color: var(--ink); font-size: calc(var(--u) * 15.5); font-weight: 700; line-height: 1.35; }
.pd-brand__en { margin-top: calc(var(--u) * 2); color: var(--ink); font-family: Arial, 'Vazirmatn', sans-serif; font-size: calc(var(--u) * 11.5); line-height: 1.3; text-align: right; }
.pd-logo { flex: none; width: calc(var(--u) * 52); height: calc(var(--u) * 52); object-fit: contain; }

.pd-rule { flex: none; width: 100%; height: 0; margin: 0; border: 0; border-top: max(1px, calc(var(--u) * 1)) solid var(--rule); opacity: 1; }
.pd-rule--head { margin-top: calc(var(--u) * 24); }
.pd-rule--foot { margin-top: calc(var(--u) * 17); }
.pd-rule--rx { margin-top: calc(var(--u) * 2); }

.pd-title { margin: calc(var(--u) * 11) 0 0; color: var(--ink); font-size: calc(var(--u) * 25); font-weight: 800; line-height: 1.3; text-align: center; }
.pd-subtitle { margin: calc(var(--u) * 4) 0 0; color: var(--text); font-size: calc(var(--u) * 17); line-height: 1.45; text-align: center; }

.pd-rows { margin: calc(var(--u) * 22) 0 0; padding: 0 calc(var(--u) * 8) 0 calc(var(--u) * 15); }
.pd-rows > div { display: flex; align-items: baseline; justify-content: space-between; gap: calc(var(--u) * 12); min-height: calc(var(--u) * 33.5); }
.pd-rows dt,.pd-foot dt { flex: none; margin: 0; color: var(--text); font-weight: 400; }
.pd-rows dd,.pd-foot dd { min-width: 0; margin: 0; color: var(--text); text-align: left; }
.pd-rows--after { margin-top: calc(var(--u) * 10); }

.pd-box { margin: calc(var(--u) * 19) calc(var(--u) * -2) 0 calc(var(--u) * -7); padding: calc(var(--u) * 3) calc(var(--u) * 11); background: var(--shade); border-radius: calc(var(--u) * 2); font-size: calc(var(--u) * 15); }
.pd-box--single { padding-block: calc(var(--u) * 3); }
.pd-inline { display: flex; align-items: baseline; gap: calc(var(--u) * 30); min-height: calc(var(--u) * 28.5); line-height: calc(var(--u) * 28.5); }
.pd-box--single .pd-inline { min-height: calc(var(--u) * 32); line-height: calc(var(--u) * 32); }
.pd-inline > span { flex: none; color: var(--text); }
.pd-inline > bdi { color: var(--text); }
.pd--card .pd-box + .pd-rows { margin-top: calc(var(--u) * 12); }

.pd-band { display: flex; align-items: center; justify-content: space-between; gap: calc(var(--u) * 12); min-height: calc(var(--u) * 44); margin: calc(var(--u) * 44) calc(var(--u) * -4) 0 calc(var(--u) * 6); padding: calc(var(--u) * 4) calc(var(--u) * 15) calc(var(--u) * 4) calc(var(--u) * 34); background: var(--band); border-radius: calc(var(--u) * 4); break-inside: avoid; print-color-adjust: exact; -webkit-print-color-adjust: exact; }
.pd-band__label { color: var(--text); font-size: calc(var(--u) * 17); }
.pd-band__value { color: var(--ink); font-size: calc(var(--u) * 20); font-weight: 800; }

.pd-table { width: calc(100% + var(--u) * 14); margin: calc(var(--u) * 24) calc(var(--u) * -8) 0 calc(var(--u) * -6); border-collapse: separate; border-spacing: 0; border: max(1px, var(--u)) solid var(--grid); border-radius: calc(var(--u) * 3); table-layout: fixed; font-size: calc(var(--u) * 15); }
.pd-table th,.pd-table td { padding: calc(var(--u) * 6) calc(var(--u) * 6); border-inline-start: max(1px, var(--u)) solid var(--grid); border-top: max(1px, var(--u)) solid var(--grid); color: var(--text); text-align: center; vertical-align: middle; }
.pd-table tr > :first-child { border-inline-start: 0; }
.pd-table thead th { height: calc(var(--u) * 37); border-top: 0; background: var(--head); color: var(--ink); font-weight: 700; font-size: calc(var(--u) * 14.5); print-color-adjust: exact; -webkit-print-color-adjust: exact; }
.pd-table tbody td { height: calc(var(--u) * 42); }
.pd-table .pd-col-index { width: calc(var(--u) * 38); }
.pd-table .pd-col-qty { width: calc(var(--u) * 57); }
.pd-table .pd-col-amount { width: calc(var(--u) * 87); }
.pd-table td.pd-col-text { text-align: right; padding-inline-start: calc(var(--u) * 12); font-size: calc(var(--u) * 14.5); }
.pd-band--table { margin: calc(var(--u) * 13) calc(var(--u) * -6) 0 calc(var(--u) * -4); }
.pd-band--table .pd-band__label { font-weight: 600; }

.pd-foot { display: grid; grid-template-columns: 1fr 1fr; margin: calc(var(--u) * 25) 0 0; }
.pd-foot > div { display: contents; }
.pd-foot dt { padding-inline-start: calc(var(--u) * 10); line-height: calc(var(--u) * 32); }
.pd-foot dd { padding-inline-start: 0; text-align: right; line-height: calc(var(--u) * 32); }

.pd-rx { margin: calc(var(--u) * 12) 0 0 calc(var(--u) * 1); color: var(--ink); font-family: 'Times New Roman', Georgia, serif; font-size: calc(var(--u) * 30); font-weight: 700; line-height: 1; }
.pd-rx__x { font-size: .62em; margin-inline-start: -.08em; vertical-align: -.22em; }
.pd-medicines { margin: calc(var(--u) * 12) 0 0; padding: 0 0 0 calc(var(--u) * 26); color: var(--text); font-size: calc(var(--u) * 14); }
.pd-medicines li { padding: 0 0 calc(var(--u) * 12) calc(var(--u) * 2); margin-bottom: calc(var(--u) * 12); border-bottom: max(1px, var(--u)) solid #dbe6ef; break-inside: avoid; }
.pd-medicines li:last-child { border-bottom: 0; margin-bottom: 0; }
.pd-medicines li::marker { color: var(--text); }
.pd-medicines__name { line-height: 1.45; }
.pd-medicines__detail { margin-top: calc(var(--u) * 4); padding-inline-start: calc(var(--u) * 6); color: #3c4763; font-size: calc(var(--u) * 13); }
.pd-note { margin: calc(var(--u) * 16) 0 0; padding: calc(var(--u) * 8) 0 0; border-top: max(1px, var(--u)) solid var(--rule); white-space: pre-wrap; font-size: calc(var(--u) * 14); }
.pd-signer { margin-top: auto; padding: calc(var(--u) * 28) calc(var(--u) * 7) 0 0; text-align: right; break-inside: avoid; }
.pd-signer__name { color: var(--ink); font-size: calc(var(--u) * 17); font-weight: 600; }
.pd-signer__title { margin-top: calc(var(--u) * 3); color: var(--text); font-size: calc(var(--u) * 14); }

/* A 58 mm roll keeps the card but lays each table line out as label and value, rather than shrinking columns. */
.pd--receipt-58 .pd-table,.pd--receipt-58 .pd-table tbody,.pd--receipt-58 .pd-table tr,.pd--receipt-58 .pd-table td { display: block; width: 100%; }
.pd--receipt-58 .pd-table { width: 100%; margin-inline: 0; }
.pd--receipt-58 .pd-table thead { display: none; }
.pd--receipt-58 .pd-table tr { padding: calc(var(--u) * 6) calc(var(--u) * 10); border-top: max(1px, var(--u)) solid var(--grid); }
.pd--receipt-58 .pd-table tr:first-child { border-top: 0; }
.pd--receipt-58 .pd-table td { display: flex; justify-content: space-between; gap: calc(var(--u) * 10); height: auto; padding: calc(var(--u) * 2) 0; border: 0; text-align: left; }
.pd--receipt-58 .pd-table td::before { content: attr(data-label); color: #55617d; }
.pd--receipt-58 .pd-table td.pd-col-index { display: none; }

@media (max-width: 650px) {
  .print-screen { padding: 18px 12px 32px; }
  .print-actions { align-items: stretch; margin-bottom: 20px; }
  .print-actions__intro,.paper-picker,.print-actions__buttons { width: 100%; }
  .print-actions__buttons > button { flex: 1; }
  .pd--a5,.pd--a4 { --paper-w: calc(100vw - 24px) !important; min-height: 0; }
}

@media print {
  .print-screen { min-height: 0; padding: 0; background: #fff; }
  .print-actions,.print-state { display: none !important; }
  .pd { max-width: none; margin: 0; box-shadow: none; border-radius: 0; }
  .pd-head,.pd-title,.pd-subtitle,.pd-box,.pd-rows > div,.pd-foot,.pd-signer { break-inside: avoid; }
  .pd-table thead { display: table-header-group; }
  .pd-table tr { break-inside: avoid; }
  .pd--receipt-58 .pd-table thead { display: none; }
}
</style>
