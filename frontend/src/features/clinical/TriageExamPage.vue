<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import type { VisitRow } from '@/features/visits/types'
import PatientBanner from './PatientBanner.vue'
import { blankSystemic, normalizeDigits, parseMeasurements, serializeMeasurements, systemicConditions, type SystemicCode } from './examRecord'
import type { PreExam } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const MEASUREMENTS_LIMIT = 1000

const props = defineProps<{ visitId: string }>()
const router = useRouter()

const visit = ref<VisitRow | null>(null)
const version = ref<number | null>(null)
const completed = ref(false)
/** A completed exam opens again only to record a correction with its reason. */
const amending = ref(false)
const amendReason = ref('')
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const notice = ref('')

const form = reactive({
  chiefComplaint: '',
  duration: '',
  eye: '',
  history: '',
  visualAcuityOd: '',
  visualAcuityOs: '',
  pinholeOd: '',
  pinholeOs: '',
  glassesOd: '',
  glassesOs: '',
  nearOd: '',
  nearOs: '',
  iopOd: '',
  iopOs: '',
  bp: '',
  syringingOd: '',
  syringingOs: '',
  systemic: blankSystemic(),
  allergy: '' as '' | 'Y' | 'N',
  allergicTo: '',
  measurements: '',
  notes: '',
})

const { dirty, markSaved, applying } = useUnsavedGuard(form)

const eyeOptions = [
  { value: 'OD', label: 'راست (OD)' },
  { value: 'OS', label: 'چپ (OS)' },
  { value: 'Both', label: 'هر دو' },
]

// GL and NV keep the paper's own codes: their exact meaning awaits the medical lead (WORKFLOWS.md M-10).
const eyes = computed(() => [
  { code: 'OD', title: 'چشم راست', tone: 'xr-eye--od', va: 'visualAcuityOd', ph: 'pinholeOd', gl: 'glassesOd', nv: 'nearOd', iop: 'iopOd', syringing: 'syringingOd' },
  { code: 'OS', title: 'چشم چپ', tone: 'xr-eye--os', va: 'visualAcuityOs', ph: 'pinholeOs', gl: 'glassesOs', nv: 'nearOs', iop: 'iopOs', syringing: 'syringingOs' },
] as const)

type TidyKey = (typeof eyes.value)[number]['va' | 'ph' | 'gl' | 'nv' | 'iop']

// Set once on load: tying it to the text would snap the section shut while someone clears a field.
const extrasOpen = ref(false)

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [loadedVisit, existing] = await Promise.all([
      api<VisitRow>(`/visits/${props.visitId}`),
      api<PreExam | undefined>(`/pre-examinations/${props.visitId}`),
    ])
    visit.value = loadedVisit
    if (existing) {
      const extras = parseMeasurements(existing.measurements)
      version.value = existing.version
      completed.value = !!existing.completedAt
      applying(() =>
        Object.assign(form, {
          chiefComplaint: existing.chiefComplaint ?? '',
          history: existing.history ?? '',
          visualAcuityOd: existing.visualAcuityOd ?? '',
          visualAcuityOs: existing.visualAcuityOs ?? '',
          iopOd: existing.iopOd ?? '',
          iopOs: existing.iopOs ?? '',
          notes: existing.notes ?? '',
          ...extras,
        }),
      )
      extrasOpen.value = !!(extras.measurements || existing.notes)
    }
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

/** Pressing the chosen answer again clears it: "not asked" must stay distinct from "no". */
function answer(code: SystemicCode, value: 'Y' | 'N') {
  const row = form.systemic[code]
  row.answer = row.answer === value ? '' : value
}

function tidy(key: TidyKey) {
  form[key] = normalizeDigits(form[key])
}

const measurementsText = computed(() =>
  serializeMeasurements({
    eye: form.eye,
    duration: form.duration,
    pinholeOd: form.pinholeOd,
    pinholeOs: form.pinholeOs,
    glassesOd: form.glassesOd,
    glassesOs: form.glassesOs,
    nearOd: form.nearOd,
    nearOs: form.nearOs,
    bp: form.bp,
    syringingOd: form.syringingOd,
    syringingOs: form.syringingOs,
    systemic: form.systemic,
    allergy: form.allergy,
    allergicTo: form.allergicTo,
    measurements: form.measurements,
  }),
)

async function save(complete: boolean) {
  if (measurementsText.value.length > MEASUREMENTS_LIMIT) {
    error.value = 'متن اندازه‌گیری‌ها و سوابق از حد مجاز بیشتر است. لطفاً «اندازه‌گیری‌های دیگر» را کوتاه کنید.'
    return
  }
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const saved = await api<PreExam>(`/pre-examinations/${props.visitId}`, {
      method: 'PUT',
      body: {
        chiefComplaint: form.chiefComplaint,
        history: form.history,
        visualAcuityOd: form.visualAcuityOd,
        visualAcuityOs: form.visualAcuityOs,
        iopOd: form.iopOd,
        iopOs: form.iopOs,
        measurements: measurementsText.value,
        notes: form.notes,
        version: version.value,
        amendReason: amending.value ? amendReason.value : undefined,
      },
    })
    version.value = saved.version
    markSaved()
    if (amending.value) {
      amending.value = false
      amendReason.value = ''
      notice.value = 'اصلاحیه ثبت شد.'
      return
    }
    if (complete) {
      await api(`/pre-examinations/${props.visitId}/complete`, { method: 'POST' })
      await router.push({ name: 'triage' })
      return
    }
    notice.value = 'ذخیره شد.'
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="d-grid gap-4">
    <PatientBanner v-if="visit" :visit="visit" />
    <section v-else-if="loading" class="xr-card banner-placeholder" aria-hidden="true">
      <span class="xr-skeleton xr-skeleton--title"></span>
      <span class="xr-skeleton xr-skeleton--wide"></span>
    </section>

    <div v-if="error && !visit" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <!-- The form paints at once and stays locked until this visit's record has arrived, so nothing typed can land on the wrong data. -->
    <form v-if="visit || loading" class="xr-card" @submit.prevent="save(false)">
      <div class="xr-card__header">
        <h2 class="xr-card__title">معاینه ابتدایی</h2>
        <RouterLink :to="{ name: 'triage' }" class="btn btn-soft">بازگشت به صف</RouterLink>
      </div>

      <fieldset :disabled="(completed && !amending) || loading" class="xr-card__body">
        <div v-if="completed && !amending" class="alert alert-primary" role="status">این معاینه تکمیل و برای داکتر ارسال شده است. برای تغییر، اصلاحیه با دلیل ثبت کنید.</div>
        <div v-if="amending" class="alert alert-warning" role="status">مقدار قبلی و دلیل اصلاح در پرونده می‌ماند و داکتر آن را می‌بیند.</div>
        <div v-if="notice && completed && !amending" class="alert alert-success" role="status">{{ notice }}</div>

        <div class="xr-group">
          <h3 class="xr-group__title">شکایت</h3>
          <div class="row g-3">
            <div class="col-lg-6">
              <label class="form-label" for="pe-complaint">شکایت اصلی</label>
              <input id="pe-complaint" v-model="form.chiefComplaint" class="form-control" maxlength="500" />
            </div>
            <div class="col-lg-3 col-sm-6">
              <label class="form-label" for="pe-duration">مدت (Duration)</label>
              <input id="pe-duration" v-model="form.duration" class="form-control" maxlength="40" />
            </div>
            <div class="col-lg-3 col-sm-6">
              <label class="form-label" for="pe-eye">چشم مربوط</label>
              <select id="pe-eye" v-model="form.eye" class="form-select">
                <option value="">انتخاب نشده</option>
                <option v-for="option in eyeOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
              </select>
            </div>
          </div>
        </div>

        <div class="xr-group">
          <h3 class="xr-group__title">دید و فشار چشم</h3>
          <div class="xr-table-wrap">
            <table class="xr-clinical-table">
              <thead>
                <tr>
                  <th scope="col">چشم</th>
                  <th scope="col">سنجش دید (VA)</th>
                  <th scope="col">با Pinhole (PH)</th>
                  <th scope="col">GL</th>
                  <th scope="col">NV</th>
                  <th scope="col">فشار چشم (IOP، mmHg)</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="eye in eyes" :key="eye.code">
                  <th scope="row" class="fw-normal text-nowrap"><span class="xr-eye" :class="eye.tone">{{ eye.code }}</span> {{ eye.title }}</th>
                  <td><input v-model="form[eye.va]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`دید ${eye.title} (VA ${eye.code})`" @blur="tidy(eye.va)" /></td>
                  <td><input v-model="form[eye.ph]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`Pinhole ${eye.title} (PH ${eye.code})`" @blur="tidy(eye.ph)" /></td>
                  <td><input v-model="form[eye.gl]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`GL ${eye.code}`" @blur="tidy(eye.gl)" /></td>
                  <td><input v-model="form[eye.nv]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`NV ${eye.code}`" @blur="tidy(eye.nv)" /></td>
                  <td><input v-model="form[eye.iop]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`فشار ${eye.title} (IOP ${eye.code})`" @blur="tidy(eye.iop)" /></td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="row g-3 mt-1">
            <div class="col-sm-4">
              <label class="form-label" for="pe-bp">BP (mmHg)</label>
              <input id="pe-bp" v-model="form.bp" class="form-control" dir="ltr" maxlength="20" autocomplete="off" />
            </div>
            <div class="col-sm-4">
              <label class="form-label" for="pe-syr-od"><span class="xr-eye xr-eye--od">OD</span> Syringing</label>
              <input id="pe-syr-od" v-model="form.syringingOd" class="form-control" dir="ltr" maxlength="40" autocomplete="off" />
            </div>
            <div class="col-sm-4">
              <label class="form-label" for="pe-syr-os"><span class="xr-eye xr-eye--os">OS</span> Syringing</label>
              <input id="pe-syr-os" v-model="form.syringingOs" class="form-control" dir="ltr" maxlength="40" autocomplete="off" />
            </div>
          </div>
        </div>

        <div class="xr-group">
          <h3 class="xr-group__title">سوابق (Hx)</h3>
          <div class="mb-3">
            <label class="form-label" for="pe-history">سوابق ضروری</label>
            <input id="pe-history" v-model="form.history" class="form-control" maxlength="1000" placeholder="بیماری‌ها، حساسیت‌ها، عملیات قبلی چشم" />
          </div>
          <div class="hx-grid">
            <div v-for="condition in systemicConditions" :key="condition.code" class="hx-row">
              <span class="hx-row__name" dir="ltr">{{ condition.label }}</span>
              <span class="xr-yesno" role="group" :aria-label="condition.label">
                <button v-for="value in (['Y', 'N'] as const)" :key="value" type="button" class="xr-yesno__btn" :aria-pressed="form.systemic[condition.code].answer === value" @click="answer(condition.code, value)">{{ value }}</button>
              </span>
              <input v-if="form.systemic[condition.code].answer === 'Y'" v-model="form.systemic[condition.code].years" class="form-control form-control-sm hx-row__detail" dir="ltr" maxlength="15" autocomplete="off" placeholder="Years using Rx" :aria-label="`Years using Rx ${condition.label}`" />
            </div>
            <div class="hx-row hx-row--wide">
              <span class="hx-row__name" dir="ltr">Allergies</span>
              <span class="xr-yesno" role="group" aria-label="Allergies">
                <button v-for="value in (['Y', 'N'] as const)" :key="value" type="button" class="xr-yesno__btn" :aria-pressed="form.allergy === value" @click="form.allergy = form.allergy === value ? '' : value">{{ value }}</button>
              </span>
              <input v-if="form.allergy === 'Y'" v-model="form.allergicTo" class="form-control form-control-sm hx-row__detail" maxlength="120" autocomplete="off" aria-label="Allergic to" placeholder="Allergic to" />
            </div>
          </div>
          <p class="form-hint mb-0">پرسیده‌نشده خالی می‌ماند. برای برداشتن پاسخ، دوباره روی همان دکمه بزنید.</p>
        </div>

        <details class="xr-section" :open="extrasOpen">
          <summary>اندازه‌گیری‌های دیگر و یادداشت <span class="xr-section__hint">اختیاری</span></summary>
          <div class="xr-section__body">
            <div class="mb-3">
              <label class="form-label" for="pe-measure">اندازه‌گیری‌های دیگر</label>
              <input id="pe-measure" v-model="form.measurements" class="form-control" maxlength="300" />
            </div>
            <label class="form-label" for="pe-notes">یادداشت</label>
            <textarea id="pe-notes" v-model="form.notes" class="form-control" rows="2" maxlength="2000"></textarea>
          </div>
        </details>

        <div v-if="error" class="alert alert-danger mt-3 mb-0" role="alert">{{ error }}</div>
      </fieldset>

      <div v-if="completed && !amending" class="xr-action-bar">
        <button type="button" class="btn btn-soft" :disabled="loading" @click="amending = true">ثبت اصلاحیه</button>
      </div>
      <div v-if="amending" class="xr-action-bar">
        <input v-model="amendReason" class="form-control" minlength="3" maxlength="500" required placeholder="دلیل اصلاح" aria-label="دلیل اصلاح" />
        <button type="submit" class="btn btn-primary text-nowrap" :disabled="saving || loading">ذخیره اصلاحیه</button>
        <button type="button" class="btn btn-soft-danger text-nowrap" :disabled="saving" @click="amending = false; load()">انصراف</button>
      </div>
      <div v-if="!completed" class="xr-action-bar">
        <button type="button" class="btn btn-primary" :disabled="saving || loading" @click="save(true)">ذخیره و ارسال به داکتر</button>
        <button type="submit" class="btn btn-soft" :disabled="saving || loading">ذخیره</button>
        <span class="xr-action-bar__status" role="status">
          <template v-if="notice && !dirty">{{ notice }}</template>
          <template v-else-if="dirty">تغییرات ذخیره نشده</template>
        </span>
      </div>
    </form>
  </div>
</template>

<style scoped>
.banner-placeholder {
  display: grid;
  gap: 12px;
  padding: 22px 20px;
}

.hx-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: 8px 32px;
}

.hx-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 34px;
}

.hx-row--wide {
  grid-column: 1 / -1;
}

.hx-row__name {
  width: 80px;
  color: var(--xr-text);
  text-align: start;
}

.hx-row__detail {
  flex: 1;
  min-width: 0;
}
</style>
