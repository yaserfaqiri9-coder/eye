<script setup lang="ts">
import { computed } from 'vue'
import { formatDate, formatDateTime } from '@/utils/date'
import type { VisitRow } from '@/features/visits/types'
import { parseMeasurements, systemicConditions } from './examRecord'
import type { PreExam } from './types'

const props = defineProps<{ preExam: PreExam; visit: VisitRow }>()
const extras = computed(() => parseMeasurements(props.preExam.measurements))
const value = (text: string | null | undefined) => text?.trim() || 'ثبت نشده'
const answer = (text: string) => text === 'Y' ? 'بله' : text === 'N' ? 'خیر' : 'ثبت نشده'
const eyeName = (text: string) => ({ OD: 'چشم راست · OD', OS: 'چشم چپ · OS', OU: 'هر دو چشم · OU', Both: 'هر دو چشم' }[text] || value(text))
const eyeRows = computed(() => [
  { label: 'حدت بینایی · VA', od: props.preExam.visualAcuityOd, os: props.preExam.visualAcuityOs },
  { label: 'دید با سوراخ ریز · PH', od: extras.value.pinholeOd, os: extras.value.pinholeOs },
  { label: 'فشار چشم · IOP (mmHg)', od: props.preExam.iopOd, os: props.preExam.iopOs },
  { label: 'GL', od: extras.value.glassesOd, os: extras.value.glassesOs },
  { label: 'NV', od: extras.value.nearOd, os: extras.value.nearOs },
  { label: 'Syringing', od: extras.value.syringingOd, os: extras.value.syringingOs },
])
</script>

<template>
  <article class="pre-document" aria-label="سند معاینه ابتدایی" dir="rtl">
    <header class="pre-document__head">
      <div>
        <p class="pre-document__eyebrow">پرونده بالینی · معاینه ابتدایی</p>
        <h2>{{ visit.patientName }}</h2>
        <p class="pre-document__subtitle">اطلاعات ثبت‌شده پیش از معاینه داکتر</p>
      </div>
      <span class="pre-document__status" :class="{ 'pre-document__status--complete': preExam.completedAt }">
        <i class="ri-checkbox-circle-line" aria-hidden="true"></i>
        {{ preExam.completedAt ? 'تکمیل شده' : 'ثبت شده · تکمیل نشده' }}
      </span>
    </header>

    <dl class="pre-document__identity">
      <div><dt>شماره پرونده</dt><dd><bdi dir="ltr">{{ visit.patientFileNumber }}</bdi></dd></div>
      <div><dt>شماره مراجعه</dt><dd><bdi dir="ltr">{{ visit.visitNumber }}</bdi></dd></div>
      <div><dt>تاریخ مراجعه</dt><dd>{{ formatDate(visit.visitDate) }}</dd></div>
      <div><dt>زمان ثبت معاینه</dt><dd>{{ formatDateTime(preExam.recordedAt) }}</dd></div>
      <div v-if="preExam.completedAt"><dt>زمان تکمیل</dt><dd>{{ formatDateTime(preExam.completedAt) }}</dd></div>
    </dl>

    <section class="pre-document__section" aria-labelledby="pre-complaint-title">
      <h3 id="pre-complaint-title"><span>۰۱</span> شکایت اصلی</h3>
      <p class="pre-document__prose">{{ value(preExam.chiefComplaint) }}</p>
      <dl class="pre-document__inline">
        <div><dt>چشم</dt><dd>{{ eyeName(extras.eye) }}</dd></div>
        <div><dt>مدت شکایت</dt><dd>{{ value(extras.duration) }}</dd></div>
      </dl>
    </section>

    <section class="pre-document__section" aria-labelledby="pre-eyes-title">
      <h3 id="pre-eyes-title"><span>۰۲</span> بررسی چشم‌ها</h3>
      <table class="pre-document__eyes">
        <caption class="visually-hidden">نتایج ثبت‌شده چشم راست و چشم چپ</caption>
        <thead><tr><th scope="col">اندازه‌گیری</th><th scope="col">چشم راست <bdi dir="ltr">OD</bdi></th><th scope="col">چشم چپ <bdi dir="ltr">OS</bdi></th></tr></thead>
        <tbody><tr v-for="row in eyeRows" :key="row.label"><th scope="row">{{ row.label }}</th><td><bdi dir="auto">{{ value(row.od) }}</bdi></td><td><bdi dir="auto">{{ value(row.os) }}</bdi></td></tr></tbody>
      </table>
      <dl class="pre-document__inline"><div><dt>فشار خون · BP (mmHg)</dt><dd><bdi dir="auto">{{ value(extras.bp) }}</bdi></dd></div></dl>
    </section>

    <section class="pre-document__section" aria-labelledby="pre-history-title">
      <h3 id="pre-history-title"><span>۰۳</span> سوابق و هشدارها</h3>
      <p class="pre-document__prose" :class="{ 'pre-document__warning': preExam.history }">{{ value(preExam.history) }}</p>
      <dl class="pre-document__history">
        <div v-for="condition in systemicConditions" :key="condition.code" :class="{ 'pre-document__positive': extras.systemic[condition.code].answer === 'Y' }">
          <dt>{{ condition.label }}</dt>
          <dd>{{ answer(extras.systemic[condition.code].answer) }}<span v-if="extras.systemic[condition.code].years"> · مدت ثبت‌شده: <bdi dir="auto">{{ extras.systemic[condition.code].years }}</bdi> سال</span></dd>
        </div>
        <div :class="{ 'pre-document__positive': extras.allergy === 'Y' }"><dt>حساسیت</dt><dd>{{ answer(extras.allergy) }}<span v-if="extras.allergicTo"> · {{ extras.allergicTo }}</span></dd></div>
      </dl>
    </section>

    <section class="pre-document__section" aria-labelledby="pre-notes-title">
      <h3 id="pre-notes-title"><span>۰۴</span> جزئیات و یادداشت‌ها</h3>
      <dl class="pre-document__notes">
        <div><dt>اندازه‌گیری‌ها و اطلاعات تکمیلی</dt><dd>{{ value(extras.measurements) }}</dd></div>
        <div><dt>یادداشت معاینه ابتدایی</dt><dd>{{ value(preExam.notes) }}</dd></div>
      </dl>
    </section>
    <footer class="pre-document__foot">«ثبت نشده» به معنی نبود اطلاعات ثبت‌شده است و نتیجه طبیعی معاینه را نشان نمی‌دهد.</footer>
  </article>
</template>

<style scoped>
.pre-document { color: var(--xr-text); background: #fff; padding: 12px 16px 8px; font-size: 13px; line-height: 1.9; overflow-wrap: anywhere; }
.pre-document__head { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding-bottom: 24px; border-bottom: 2px solid var(--xr-primary); }
.pre-document__eyebrow { color: #27636a; font-size: 11px; margin: 0 0 8px; }
.pre-document h2 { font-size: 24px; line-height: 1.7; margin: 0; font-weight: 650; }
.pre-document__subtitle { color: #59666c; margin: 5px 0 0; font-size: 12px; }
.pre-document__status { flex-shrink: 0; display: inline-flex; align-items: center; gap: 6px; padding: 5px 9px; background: #f1f4f5; color: #43555d; font-size: 11px; border-radius: 5px; }
.pre-document__status--complete { background: #eaf6f3; color: #246356; }
.pre-document dl { margin-bottom: 0; }
.pre-document dt { color: #59666c; font-weight: 400; font-size: 11px; }
.pre-document dd { margin: 2px 0 0; white-space: pre-wrap; }
.pre-document__identity { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px 24px; padding: 20px 0; border-bottom: 1px solid #e4eaec; }
.pre-document__section { padding: 22px 0; border-bottom: 1px solid #e4eaec; }
.pre-document h3 { display: flex; align-items: center; gap: 10px; font-size: 14px; font-weight: 600; margin: 0 0 14px; }
.pre-document h3 > span { color: #277079; font-size: 11px; font-weight: 400; }
.pre-document__prose { white-space: pre-wrap; margin: 0; }
.pre-document__inline { display: flex; flex-wrap: wrap; gap: 16px 40px; padding-top: 14px; }
.pre-document__eyes { width: 100%; border-collapse: collapse; table-layout: fixed; font-size: 12px; }
.pre-document__eyes th, .pre-document__eyes td { padding: 10px 12px; border-bottom: 1px solid #e8edef; vertical-align: top; }
.pre-document__eyes th:first-child { width: 42%; }
.pre-document__eyes thead th { background: #f0f7f7; color: #245c63; font-weight: 500; }
.pre-document__eyes tbody th { font-weight: 400; color: #4c5c63; }
.pre-document__eyes td { text-align: center; }
.pre-document__history { margin-top: 14px; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0 24px; }
.pre-document__history > div { padding: 10px 0; border-bottom: 1px solid #edf0f1; }
.pre-document__positive dd { color: #a33636; font-weight: 500; }
.pre-document__warning { padding: 10px 14px; border-inline-start: 3px solid #b44b45; background: #fff7f5; color: #943b35; }
.pre-document__notes { display: grid; gap: 16px; }
.pre-document__foot { color: #59666c; font-size: 11px; padding-top: 18px; }
@media (max-width: 575px) {
  .pre-document { padding: 4px 0; }
  .pre-document__head { flex-direction: column; gap: 12px; padding-bottom: 18px; }
  .pre-document h2 { font-size: 21px; }
  .pre-document__identity { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
  .pre-document__history { grid-template-columns: minmax(0, 1fr); }
  .pre-document__eyes th, .pre-document__eyes td { padding: 10px 6px; }
}
</style>
