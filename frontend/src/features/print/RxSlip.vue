<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { lensOptions } from '@/features/clinical/examRecord'
import { printPaper, sheetOverflows, type Hospital } from './paper'

export interface SlipEye {
  sphere: string | null
  cylinder: string | null
  axis: string | null
  add: string | null
  vision: string | null
  unaided: string | null
}

/** One slip for both the refraction result and the doctor's glasses prescription, always filled from a saved record. */
defineProps<{
  title: string
  hospital: Hospital | null
  patientName: string
  fileNumber: string
  date: string
  number?: string | null
  od: SlipEye
  os: SlipEye
  pd: string | null
  lens: string[]
  notes: string | null
  /** The person responsible for this document, when the record names one. */
  signedBy?: string | null
}>()

const overflow = ref(false)
onMounted(async () => {
  overflow.value = await sheetOverflows('.slip')
})
</script>

<template>
  <div class="slip-screen">
    <div class="slip-tools">
      <button type="button" class="btn btn-primary" :disabled="overflow" @click="printPaper">چاپ</button>
      <span class="slip-tools__hint">اندازه ورق: A5 افقی</span>
    </div>
    <div v-if="overflow" class="alert alert-danger slip-tools" role="alert">متن یادداشت در این ورق جا نمی‌شود و بخش پایین نسخه چاپ نمی‌شد. یادداشت را در پرونده کوتاه کنید.</div>

    <article class="slip" dir="ltr" :aria-label="title">
      <header class="slip__head">
        <img v-if="hospital?.logoAttachmentId" :src="`/api/attachments/${hospital.logoAttachmentId}`" alt="" class="slip__logo" />
        <div class="slip__brand">
          <div class="slip__clinic" dir="auto">{{ hospital?.hospitalName }}</div>
          <h1 class="slip__title" dir="auto">{{ title }}</h1>
        </div>
        <div class="slip__ids">
          <div><b>Date:</b> {{ date }}</div>
          <div v-if="number"><b>No:</b> {{ number }}</div>
          <div><b>File:</b> {{ fileNumber }}</div>
        </div>
      </header>

      <div class="slip__person"><b>Name:</b> <span dir="auto">{{ patientName }}</span></div>

      <table class="slip__table">
        <thead>
          <tr><th>EYE</th><th>VISION</th><th>SPHERE</th><th>CYLINDER</th><th>AXIS</th><th>ADD</th><th>VA with Rx</th></tr>
        </thead>
        <tbody>
          <tr>
            <th>RE (OD)</th>
            <td>{{ od.unaided }}</td>
            <td class="strong">{{ od.sphere }}</td>
            <td class="strong">{{ od.cylinder }}</td>
            <td class="strong">{{ od.axis }}</td>
            <td class="strong">{{ od.add }}</td>
            <td>{{ od.vision }}</td>
          </tr>
          <tr>
            <th>LE (OS)</th>
            <td>{{ os.unaided }}</td>
            <td class="strong">{{ os.sphere }}</td>
            <td class="strong">{{ os.cylinder }}</td>
            <td class="strong">{{ os.axis }}</td>
            <td class="strong">{{ os.add }}</td>
            <td>{{ os.vision }}</td>
          </tr>
        </tbody>
      </table>

      <div class="slip__row">
        <div class="slip__pd"><b>PD:</b> {{ pd }} <span v-if="pd">mm</span></div>
        <div class="slip__lens">
          <span v-for="option in lensOptions" :key="option" class="slip__lens-item"><i class="tick" :class="{ 'tick--on': lens.includes(option) }"></i>{{ option }}</span>
        </div>
      </div>

      <p v-if="notes" class="slip__notes" dir="auto">{{ notes }}</p>

      <footer class="slip__foot">
        <div class="slip__contact" dir="auto">
          <span v-if="hospital?.address">{{ hospital.address }}</span>
          <span v-if="hospital?.phone" dir="ltr">{{ hospital.phone }}</span>
        </div>
        <div class="slip__sign"><b>Signature:</b> <span class="slip__sign-line"></span><small v-if="signedBy" dir="auto">{{ signedBy }}</small></div>
      </footer>
    </article>
  </div>
</template>

<style scoped>
.slip-screen {
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
}

.slip-tools {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 210mm;
  max-width: 100%;
  margin: 0 auto 12px;
}

.slip-tools__hint {
  color: var(--xr-text);
  font-size: 12.25px;
}

.slip {
  display: flex;
  flex-direction: column;
  gap: 3mm;
  width: 210mm;
  height: 148mm;
  margin: 0 auto;
  padding: 9mm 11mm;
  overflow: hidden;
  background: #fff;
  color: #000;
  font-family: 'Poppins', 'Vazirmatn', sans-serif;
  font-size: 10pt;
  line-height: 1.35;
}

.slip * {
  box-sizing: border-box;
}

.slip b {
  font-weight: 600;
}

.slip__head {
  display: flex;
  align-items: center;
  gap: 5mm;
  padding-bottom: 2.5mm;
  border-bottom: 0.75pt solid #000;
}

.slip__logo {
  max-height: 16mm;
}

.slip__brand {
  flex: 1;
}

.slip__clinic {
  font-size: 12pt;
  font-weight: 700;
}

.slip__title {
  margin: 0;
  color: #000;
  font-size: 10.5pt;
  font-weight: 500;
}

.slip__ids {
  font-size: 9pt;
  text-align: end;
}

.slip__person {
  font-size: 11pt;
}

.slip__table {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.slip__table th,
.slip__table td {
  height: 11mm;
  padding: 0 1.5mm;
  border: 0.5pt solid #000;
  font-weight: 400;
  text-align: center;
}

.slip__table thead th {
  height: 7mm;
  font-size: 8.5pt;
  font-weight: 600;
}

.slip__table tbody th {
  font-weight: 600;
}

.strong {
  font-size: 12pt;
  font-weight: 600;
}

.slip__row {
  display: flex;
  align-items: flex-start;
  gap: 6mm;
}

.slip__pd {
  min-width: 30mm;
  font-size: 11pt;
}

.slip__lens {
  display: grid;
  flex: 1;
  grid-template-columns: repeat(3, 1fr);
  gap: 1mm 4mm;
  font-size: 9pt;
}

.slip__lens-item {
  display: inline-flex;
  align-items: center;
  gap: 1.4mm;
}

.tick {
  display: inline-block;
  width: 3mm;
  height: 3mm;
  border: 0.5pt solid #000;
}

.tick--on {
  box-shadow: inset 0 0 0 3mm #000;
  print-color-adjust: exact;
  -webkit-print-color-adjust: exact;
}

.slip__notes {
  margin: 0;
  font-size: 9pt;
  white-space: pre-line;
}

.slip__foot {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 6mm;
  margin-top: auto;
}

.slip__contact {
  display: flex;
  flex-direction: column;
  font-size: 8pt;
}

.slip__sign {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: flex-end;
  gap: 2mm;
  min-width: 70mm;
}

.slip__sign-line {
  flex: 1;
  min-width: 40mm;
  border-bottom: 0.5pt solid #000;
}

.slip__sign small {
  width: 100%;
  font-size: 8pt;
  text-align: end;
}

@media print {
  .slip-screen {
    padding: 0;
    background: #fff;
  }

  .slip-tools {
    display: none;
  }

  .slip {
    margin: 0;
  }
}

@media screen {
  .slip {
    box-shadow: 0 2px 12px rgba(0, 0, 0, 0.15);
  }
}
</style>

<style>
@page {
  size: A5 landscape;
  margin: 0;
}
</style>
