<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { api } from '@/api/http'
import { parseRefractionExtras } from '@/features/clinical/examRecord'
import type { Refraction } from '@/features/clinical/types'
import type { VisitRow } from '@/features/visits/types'
import RxSlip, { type SlipEye } from './RxSlip.vue'
import { shamsiPaper, useHospital, usePaperPage } from './paper'

const props = defineProps<{ visitId: string }>()
const { hospital, load: loadHospital } = useHospital()

const page = usePaperPage(async () => {
  const [visit, refraction] = await Promise.all([api<VisitRow>(`/visits/${props.visitId}`), api<Refraction | undefined>(`/refractions/${props.visitId}`)])
  return { visit, refraction: refraction ?? null }
})

const refraction = computed(() => page.data.value?.refraction ?? null)
const visit = computed(() => page.data.value?.visit ?? null)
const hasFinal = computed(() => !!refraction.value?.lines.some((l) => l.stage === 'Final'))

function eye(code: 'OD' | 'OS'): SlipEye {
  const line = refraction.value?.lines.find((l) => l.stage === 'Final' && l.eye === code)
  return {
    sphere: line?.sphere ?? null,
    cylinder: line?.cylinder ?? null,
    axis: line?.axis ?? null,
    add: line?.add ?? null,
    vision: line?.va ?? null,
    unaided: (code === 'OD' ? refraction.value?.unaidedVaOd : refraction.value?.unaidedVaOs) ?? null,
  }
}

onMounted(() => Promise.all([page.load(), loadHospital()]))
</script>

<template>
  <div v-if="page.error.value" class="alert alert-danger m-3" role="alert">{{ page.error.value }}</div>
  <p v-else-if="page.loading.value" class="xr-state" role="status">در حال آماده‌سازی نسخه...</p>
  <div v-else-if="!hasFinal" class="alert alert-warning m-3" role="alert">برای این مراجعه نسخه نهایی بینایی‌سنجی ثبت نشده است.</div>
  <RxSlip
    v-else-if="visit && refraction"
    title="نسخه بینایی‌سنجی (Refraction)"
    :hospital="hospital"
    :patient-name="visit.patientName"
    :file-number="visit.patientFileNumber"
    :date="shamsiPaper(refraction.recordedAt)"
    :number="visit.visitNumber"
    :od="eye('OD')"
    :os="eye('OS')"
    :pd="refraction.pd"
    :lens="parseRefractionExtras(refraction.measurements).lens"
    :notes="refraction.notes"
  />
</template>
