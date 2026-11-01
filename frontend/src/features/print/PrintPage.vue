<script setup lang="ts">
import { onMounted, ref } from 'vue'
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

onMounted(async () => {
  try {
    doc.value = await api<PrintDocument>(`/print/${props.kind}/${props.id}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
})

function print() {
  window.print()
}
</script>

<template>
  <div class="print-screen">
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="doc" class="print-actions">
      <button type="button" class="btn btn-primary" @click="print">چاپ</button>
    </div>

    <article v-if="doc" class="print-doc" :class="`print-doc--${doc.format}`">
      <header class="print-doc__head">
        <img v-if="doc.hospital.logoAttachmentId" :src="`/api/attachments/${doc.hospital.logoAttachmentId}`" alt="لوگو" class="print-doc__logo" />
        <div class="print-doc__hospital">{{ doc.hospital.hospitalName || 'شفاخانه تخصصی چشم الشفا' }}</div>
        <div v-if="doc.hospital.address || doc.hospital.phone" class="print-doc__contact">{{ doc.hospital.address }} <span class="ltr-num">{{ doc.hospital.phone }}</span></div>
      </header>

      <h1 class="print-doc__title">{{ doc.title }}</h1>
      <div class="print-doc__meta">
        <span>شماره: <span class="ltr-num">{{ doc.number }}</span></span>
        <span>تاریخ: {{ formatDateTime(doc.date) }}</span>
      </div>

      <dl class="print-doc__fields">
        <template v-for="field in doc.fields.filter((f) => f.value)" :key="field.label">
          <dt>{{ field.label }}</dt>
          <dd>{{ field.value }}</dd>
        </template>
      </dl>

      <table v-if="doc.headers && doc.rows" class="print-doc__table">
        <thead><tr><th v-for="h in doc.headers" :key="h">{{ h }}</th></tr></thead>
        <tbody><tr v-for="(row, i) in doc.rows" :key="i"><td v-for="(cell, j) in row" :key="j">{{ cell ?? '—' }}</td></tr></tbody>
      </table>

      <dl v-if="doc.totals" class="print-doc__fields print-doc__totals">
        <template v-for="total in doc.totals" :key="total.label">
          <dt>{{ total.label }}</dt>
          <dd>{{ total.value }}</dd>
        </template>
      </dl>

      <p v-if="doc.note" class="print-doc__note">{{ doc.note }}</p>
      <footer v-if="doc.hospital.printFooter" class="print-doc__footer">{{ doc.hospital.printFooter }}</footer>
    </article>
  </div>
</template>

<style scoped>
.print-screen {
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
}

.print-actions {
  max-width: 800px;
  margin: 0 auto 12px;
}

.print-doc {
  margin: 0 auto;
  padding: 24px;
  background: #fff;
  color: #000;
}

.print-doc--a4 {
  max-width: 800px;
}

.print-doc--receipt {
  max-width: 320px;
  padding: 12px;
  font-size: 12px;
}

.print-doc__head {
  padding-bottom: 8px;
  border-bottom: 1px solid #000;
  text-align: center;
}

.print-doc__logo {
  max-height: 60px;
}

.print-doc__hospital {
  font-size: 1.3em;
  font-weight: 700;
}

.print-doc__contact {
  font-size: 0.9em;
}

.print-doc__title {
  margin: 12px 0 8px;
  font-size: 1.2em;
  font-weight: 700;
  text-align: center;
}

.print-doc__meta {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 12px;
}

.print-doc__fields {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 12px;
  margin-bottom: 12px;
}

.print-doc__fields dt {
  font-weight: 700;
}

.print-doc__fields dd {
  margin: 0;
}

.print-doc__table {
  width: 100%;
  margin-bottom: 12px;
  border-collapse: collapse;
}

.print-doc__table th,
.print-doc__table td {
  padding: 4px 6px;
  border: 1px solid #000;
  text-align: start;
}

.print-doc__totals {
  padding-top: 6px;
  border-top: 1px solid #000;
}

.print-doc__note {
  white-space: pre-wrap;
}

.print-doc__footer {
  margin-top: 16px;
  padding-top: 6px;
  border-top: 1px solid #000;
  font-size: 0.85em;
  text-align: center;
}

@media print {
  .print-screen {
    padding: 0;
    background: #fff;
  }

  .print-actions {
    display: none;
  }

  .print-doc {
    max-width: none;
    margin: 0;
    padding: 0;
  }
}
</style>
