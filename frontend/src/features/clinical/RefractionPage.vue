<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import type { VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate } from '@/utils/date'
import PatientBanner from './PatientBanner.vue'
import RxGrid, { type RxRow } from './RxGrid.vue'
import LensPicker from './LensPicker.vue'
import { investigationEyeRows, normalizeDigits, parseRefractionExtras, serializeRefractionExtras, type InvestigationKey } from './examRecord'
import { refractionStageLabels, type Refraction, type RefractionStage } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const MEASUREMENTS_LIMIT = 1000

const props = defineProps<{ visitId: string }>()
const auth = useAuthStore()

const visit = ref<VisitRow | null>(null)
const previous = ref<Refraction | null>(null)
const version = ref<number | null>(null)
const completed = ref(false)
/** A completed refraction opens again only to record a correction with its reason. */
const amending = ref(false)
const amendReason = ref('')
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const notice = ref('')
/** Chosen by the refractionist when completing: where the patient goes next. */
const sendToOptical = ref(false)
const returnToDoctor = ref(true)
const opticalRxNumber = ref<string | null>(null)
const sending = ref(false)

async function sendNow() {
  sending.value = true
  error.value = ''
  try {
    await api(`/refractions/${props.visitId}/send-to-optical`, { method: 'POST' })
    opticalRxNumber.value = (await api<Refraction>(`/refractions/${props.visitId}`)).opticalRxNumber ?? null
    notice.value = 'نتیجه به بخش عینک فرستاده شد.'
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    sending.value = false
  }
}

const blankRow = (): RxRow => ({ sphere: '', cylinder: '', axis: '', add: '', va: '' })
const stages: RefractionStage[] = ['Current', 'Auto', 'Subjective', 'Final']

const form = reactive({
  unaidedVaOd: '',
  unaidedVaOs: '',
  pinholeVaOd: '',
  pinholeVaOs: '',
  nearVaOd: '',
  nearVaOs: '',
  pd: '',
  measurements: '',
  notes: '',
  investigations: {
    od: Object.fromEntries(investigationEyeRows.map((r) => [r.key, ''])) as Record<InvestigationKey, string>,
    os: Object.fromEntries(investigationEyeRows.map((r) => [r.key, ''])) as Record<InvestigationKey, string>,
  },
  rbs: '',
  jcc: '',
  lens: [] as string[],
  lines: {
    Current: { od: blankRow(), os: blankRow() },
    Auto: { od: blankRow(), os: blankRow() },
    Subjective: { od: blankRow(), os: blankRow() },
    Final: { od: blankRow(), os: blankRow() },
  } as Record<RefractionStage, { od: RxRow; os: RxRow }>,
})

const { dirty, markSaved, applying } = useUnsavedGuard(form)

const canEdit = computed(() => auth.can('preexam.edit') && (!completed.value || amending.value))

const acuity = [
  { code: 'OD', title: 'چشم راست', tone: 'xr-eye--od', unaided: 'unaidedVaOd', pinhole: 'pinholeVaOd', near: 'nearVaOd' },
  { code: 'OS', title: 'چشم چپ', tone: 'xr-eye--os', unaided: 'unaidedVaOs', pinhole: 'pinholeVaOs', near: 'nearVaOs' },
] as const

type AcuityKey = (typeof acuity)[number]['unaided' | 'pinhole' | 'near']

function tidy(key: AcuityKey) {
  form[key] = normalizeDigits(form[key])
}

function fill(row: Refraction) {
  version.value = row.version
  completed.value = !!row.completedAt
  opticalRxNumber.value = row.opticalRxNumber ?? null
  const extras = parseRefractionExtras(row.measurements)
  applying(() => {
    Object.assign(form, {
      unaidedVaOd: row.unaidedVaOd ?? '',
      unaidedVaOs: row.unaidedVaOs ?? '',
      pinholeVaOd: row.pinholeVaOd ?? '',
      pinholeVaOs: row.pinholeVaOs ?? '',
      nearVaOd: row.nearVaOd ?? '',
      nearVaOs: row.nearVaOs ?? '',
      pd: row.pd ?? '',
      measurements: extras.measurements,
      notes: row.notes ?? '',
      investigations: { od: extras.od, os: extras.os },
      rbs: extras.rbs,
      jcc: extras.jcc,
      lens: extras.lens,
    })
    for (const stage of stages) {
      for (const eye of ['OD', 'OS'] as const) {
        const line = row.lines.find((l) => l.stage === stage && l.eye === eye)
        form.lines[stage][eye === 'OD' ? 'od' : 'os'] = {
          sphere: line?.sphere ?? '',
          cylinder: line?.cylinder ?? '',
          axis: line?.axis ?? '',
          add: line?.add ?? '',
          va: line?.va ?? '',
        }
      }
    }
  })
}

// Set once on load: tying it to the values would snap the section shut while someone clears a field.
const currentOpen = ref(false)
const investigationsOpen = ref(false)

const previousFinal = computed(() => {
  const lines = previous.value?.lines.filter((l) => l.stage === 'Final') ?? []
  return lines.length ? lines : null
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [loadedVisit, existing] = await Promise.all([
      api<VisitRow>(`/visits/${props.visitId}`),
      api<Refraction | undefined>(`/refractions/${props.visitId}`),
    ])
    visit.value = loadedVisit
    if (existing) {
      fill(existing)
      currentOpen.value = existing.lines.some((l) => l.stage === 'Current')
      investigationsOpen.value = investigationEyeRows.some((r) => form.investigations.od[r.key] || form.investigations.os[r.key]) || !!(form.rbs || form.jcc || form.measurements)
    }
    loading.value = false
    // Earlier visits are extra context: the form is usable before they arrive, and works without them.
    api<Refraction[]>(`/clinical/patients/${loadedVisit.patientId}/refractions`)
      .then((history) => (previous.value = history.find((r) => r.visitId !== Number(props.visitId)) ?? null))
      .catch(() => {})
  } catch (cause) {
    error.value = errorMessage(cause)
    loading.value = false
  }
}

async function save(complete: boolean) {
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const { lines, investigations, rbs, jcc, lens, measurements, ...values } = form
    const measurementsText = serializeRefractionExtras({ od: investigations.od, os: investigations.os, rbs, jcc, lens, measurements })
    if (measurementsText.length > MEASUREMENTS_LIMIT) {
      error.value = 'متن اندازه‌گیری‌ها از حد مجاز بیشتر است. لطفاً «اندازه‌گیری‌های تکمیلی» را کوتاه کنید.'
      return
    }
    const saved = await api<Refraction>(`/refractions/${props.visitId}`, {
      method: 'PUT',
      body: {
        ...values,
        measurements: measurementsText,
        lines: stages.flatMap((stage) => [
          { stage, eye: 'OD', ...lines[stage].od },
          { stage, eye: 'OS', ...lines[stage].os },
        ]),
        version: version.value,
        amendReason: amending.value ? amendReason.value : undefined,
      },
    })
    // Kept straight away: if completing fails, the next attempt must not be refused for the version just saved.
    version.value = saved.version
    if (amending.value) {
      amending.value = false
      amendReason.value = ''
      notice.value = 'اصلاحیه ثبت شد.'
      fill(saved)
      markSaved()
      return
    }
    if (complete) {
      await api(`/refractions/${props.visitId}/complete`, {
        method: 'POST',
        body: { returnToDoctor: returnToDoctor.value, sendToOptical: sendToOptical.value },
      })
      completed.value = true
      const sent = sendToOptical.value ? ' نتیجه به بخش عینک فرستاده شد.' : ''
      notice.value = returnToDoctor.value ? `رفرکشن تکمیل شد و مریض به صف داکتر برگشت.${sent}` : `رفرکشن تکمیل شد.${sent}`
    } else {
      notice.value = 'ذخیره شد.'
    }
    // After completing, the stored record also names the optical prescription it produced.
    fill(complete ? await api<Refraction>(`/refractions/${props.visitId}`) : { ...saved, completedAt: null })
    markSaved()
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
    <div v-else-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <!-- The form paints at once and stays locked until this visit's record has arrived. -->
    <form v-if="visit || loading" class="xr-card" @submit.prevent="save(false)">
      <div class="xr-card__header">
        <h2 class="xr-card__title">بینایی‌سنجی</h2>
        <div class="d-flex gap-2">
          <RouterLink v-if="version !== null && auth.can('preexam.view')" :to="`/print/refraction/${visitId}`" target="_blank" class="btn btn-soft">چاپ نسخه رفرکشن</RouterLink>
          <RouterLink v-if="auth.can('preexam.edit')" :to="{ name: 'refraction-queue' }" class="btn btn-soft">بازگشت به فهرست</RouterLink>
          <RouterLink v-else-if="auth.can('consultation.view')" :to="`/chart/${visitId}`" class="btn btn-soft">بازگشت به پرونده</RouterLink>
        </div>
      </div>

      <fieldset :disabled="!canEdit || loading" class="xr-card__body">
        <div v-if="completed && !amending" class="alert alert-primary" role="status">این بینایی‌سنجی تکمیل شده است. برای تغییر، اصلاحیه با دلیل ثبت کنید.</div>
        <div v-if="amending" class="alert alert-warning" role="status">مقدار قبلی و دلیل اصلاح در پرونده می‌ماند و داکتر آن را می‌بیند. اگر نتیجه به بخش عینک فرستاده شده و هنوز سفارش عینک روی آن ثبت نشده، نسخه بخش عینک هم اصلاح می‌شود. نسخه‌ای که داکتر نوشته تغییر نمی‌کند.</div>
        <div v-if="previousFinal" class="alert alert-primary" role="note">
          نسخه نهایی مراجعه قبلی ({{ formatDate(previous!.visitDate) }}):
          <span v-for="line in previousFinal" :key="line.eye" class="previous">
            <span class="xr-eye" :class="line.eye === 'OD' ? 'xr-eye--od' : 'xr-eye--os'">{{ line.eye }}</span>
            <span class="ltr-num">{{ line.sphere ?? '—' }} / {{ line.cylinder ?? '—' }} × {{ line.axis ?? '—' }} ADD {{ line.add ?? '—' }}</span>
          </span>
        </div>

        <h3 class="section-title">Visual Acuity (دید بدون عینک)</h3>
        <div class="xr-table-wrap mb-4">
          <table class="xr-clinical-table">
            <thead>
              <tr>
                <th scope="col">چشم</th>
                <th scope="col">دید دور بدون اصلاح</th>
                <th scope="col">با Pinhole</th>
                <th scope="col">دید نزدیک</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="eye in acuity" :key="eye.code">
                <th scope="row" class="fw-normal text-nowrap"><span class="xr-eye" :class="eye.tone">{{ eye.code }}</span> {{ eye.title }}</th>
                <td><input v-model="form[eye.unaided]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`دید بدون اصلاح ${eye.code}`" @blur="tidy(eye.unaided)" /></td>
                <td><input v-model="form[eye.pinhole]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`Pinhole ${eye.code}`" @blur="tidy(eye.pinhole)" /></td>
                <td><input v-model="form[eye.near]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`دید نزدیک ${eye.code}`" @blur="tidy(eye.near)" /></td>
              </tr>
            </tbody>
          </table>
        </div>

        <details class="xr-section" :open="currentOpen" @toggle="currentOpen = ($event.target as HTMLDetailsElement).open">
          <summary>{{ refractionStageLabels.Current }} <span class="xr-section__hint">در صورت داشتن عینک</span></summary>
          <!-- A closed section is not built until opened, so the page is ready for typing sooner. -->
          <div v-if="currentOpen" class="xr-section__body">
            <RxGrid :od="form.lines.Current.od" :os="form.lines.Current.os" label="عینک فعلی" show-add show-va :readonly="!canEdit" />
          </div>
        </details>

        <details class="xr-section" open>
          <summary>{{ refractionStageLabels.Auto }}</summary>
          <div class="xr-section__body">
            <RxGrid :od="form.lines.Auto.od" :os="form.lines.Auto.os" label="Auto Refraction" :readonly="!canEdit" />
          </div>
        </details>

        <details class="xr-section" open>
          <summary>{{ refractionStageLabels.Subjective }}</summary>
          <div class="xr-section__body">
            <RxGrid :od="form.lines.Subjective.od" :os="form.lines.Subjective.os" label="Subjective Refraction" show-add show-va :readonly="!canEdit" />
          </div>
        </details>

        <section class="final mt-3" aria-label="نسخه نهایی">
          <h3 class="section-title final__title"><i class="ri-checkbox-circle-fill" aria-hidden="true"></i> {{ refractionStageLabels.Final }}</h3>
          <RxGrid :od="form.lines.Final.od" :os="form.lines.Final.os" label="Final Refraction" show-add show-va :readonly="!canEdit" />
          <div class="row g-3 mt-1">
            <div class="col-md-3">
              <label class="form-label" for="rf-pd">PD (فاصله مردمک‌ها، mm)</label>
              <input id="rf-pd" v-model="form.pd" class="form-control" dir="ltr" maxlength="10" autocomplete="off" />
            </div>
            <div class="col-md-9"><LensPicker v-model="form.lens" /></div>
          </div>
          <p class="form-hint mb-0">نسخه عینک رسمی را داکتر صادر می‌کند؛ این مقادیر در پرونده برای انتقال به نسخه عینک در دسترس او هستند.</p>
        </section>

        <details class="xr-section mt-3" :open="investigationsOpen" @toggle="investigationsOpen = ($event.target as HTMLDetailsElement).open">
          <summary>Retinoscopy و اندازه‌گیری‌های تکمیلی <span class="xr-section__hint">در صورت انجام</span></summary>
          <div v-if="investigationsOpen" class="xr-section__body">
            <div class="xr-table-wrap mb-3">
              <table class="xr-clinical-table">
                <thead>
                  <tr>
                    <th scope="col">اندازه‌گیری</th>
                    <th scope="col"><span class="xr-eye xr-eye--od">OD</span></th>
                    <th scope="col"><span class="xr-eye xr-eye--os">OS</span></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="row in investigationEyeRows" :key="row.key">
                    <th scope="row" class="fw-normal" dir="ltr">{{ row.label }}</th>
                    <td><input v-model="form.investigations.od[row.key]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`${row.label} OD`" /></td>
                    <td><input v-model="form.investigations.os[row.key]" class="form-control text-center" dir="ltr" maxlength="30" autocomplete="off" :aria-label="`${row.label} OS`" /></td>
                  </tr>
                </tbody>
              </table>
            </div>
            <div class="row g-3">
              <div class="col-md-3">
                <label class="form-label" for="rf-rbs">RBS (mg/dl)</label>
                <input id="rf-rbs" v-model="form.rbs" class="form-control" dir="ltr" maxlength="15" autocomplete="off" />
              </div>
              <div class="col-md-3">
                <label class="form-label" for="rf-jcc">JCC</label>
                <input id="rf-jcc" v-model="form.jcc" class="form-control" dir="ltr" maxlength="30" autocomplete="off" />
              </div>
              <div class="col-md-6">
                <label class="form-label" for="rf-measure">اندازه‌گیری‌های تکمیلی</label>
                <input id="rf-measure" v-model="form.measurements" class="form-control" maxlength="300" />
              </div>
            </div>
          </div>
        </details>

        <div class="row g-3 mt-2">
          <div class="col-12">
            <label class="form-label" for="rf-notes">یادداشت‌ها</label>
            <textarea id="rf-notes" v-model="form.notes" class="form-control" rows="2" maxlength="2000"></textarea>
          </div>
        </div>

        <div v-if="error" class="alert alert-danger mt-3 mb-0" role="alert">{{ error }}</div>
      </fieldset>

      <div v-if="amending" class="xr-action-bar">
        <input v-model="amendReason" class="form-control" minlength="3" maxlength="500" required placeholder="دلیل اصلاح" aria-label="دلیل اصلاح" />
        <button type="submit" class="btn btn-primary text-nowrap" :disabled="saving || loading">ذخیره اصلاحیه</button>
        <button type="button" class="btn btn-soft-danger text-nowrap" :disabled="saving" @click="amending = false; load()">انصراف</button>
      </div>
      <div v-else-if="canEdit" class="xr-action-bar">
        <label class="form-check-label text-nowrap"><input v-model="sendToOptical" class="form-check-input ms-2" type="checkbox" />ارسال نتیجه به بخش عینک</label>
        <label class="form-check-label text-nowrap"><input v-model="returnToDoctor" class="form-check-input ms-2" type="checkbox" />برگشت مریض به صف داکتر</label>
        <button type="button" class="btn btn-primary" :disabled="saving || loading" @click="save(true)">ذخیره و تکمیل</button>
        <button type="submit" class="btn btn-soft" :disabled="saving || loading">ذخیره</button>
        <span class="xr-action-bar__status" role="status">
          <template v-if="dirty">تغییرات ذخیره نشده</template>
          <template v-else-if="notice">{{ notice }}</template>
        </span>
      </div>
      <div v-else class="m-3">
        <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <p v-if="completed && opticalRxNumber" class="mb-3">
          نسخه بخش عینک: <span class="ltr-num">{{ opticalRxNumber }}</span>
        </p>
        <button v-if="completed && auth.can('preexam.edit')" type="button" class="btn btn-soft" :disabled="loading" @click="amending = true">ثبت اصلاحیه</button>
        <button
          v-if="completed && !opticalRxNumber && auth.can('preexam.edit')"
          type="button"
          class="btn btn-soft ms-2"
          :disabled="loading || sending"
          @click="sendNow"
        >
          ارسال به بخش عینک
        </button>
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

.section-title {
  margin-bottom: 8px;
  color: var(--xr-text);
  font-size: var(--xr-fs-body);
  font-weight: 500;
}

.previous {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-inline-start: 12px;
}

/* The final values are what leave this page, so they stand apart from device and examiner rows. */
.final {
  padding: 16px;
  border: 1px solid var(--xr-primary);
  border-radius: var(--xr-radius-control);
}

.final__title {
  color: var(--xr-primary);
}
</style>
