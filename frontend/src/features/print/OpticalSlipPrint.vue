<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { api } from '@/api/http'
import { parseOpticalNotes } from '@/features/clinical/examRecord'
import RxSlip, { type SlipEye } from './RxSlip.vue'
import { shamsiPaper, usePaperPage, type Hospital } from './paper'

interface OpticalDocument {
  title: string
  number: string
  date: string
  hospital: Hospital
  fields: { label: string; value: string | null }[]
  rows: (string | null)[][] | null
  note: string | null
}

const props = defineProps<{ id: string }>()

// This document carries its own permission on the server, so the optical desk can print it without opening the medical record.
const page = usePaperPage(() => api<OpticalDocument>(`/print/optical/${props.id}`))
const doc = computed(() => page.data.value)
const field = (label: string) => doc.value?.fields.find((f) => f.label === label)?.value ?? null
const notes = computed(() => parseOpticalNotes(doc.value?.note))

function eye(row: number, code: 'OD' | 'OS'): SlipEye {
  const [, sphere, cylinder, axis, add] = doc.value?.rows?.[row] ?? []
  return {
    sphere: sphere ?? null,
    cylinder: cylinder ?? null,
    axis: axis ?? null,
    add: add ?? null,
    vision: (code === 'OD' ? notes.value.vaOd : notes.value.vaOs) || null,
    unaided: (code === 'OD' ? notes.value.uvaOd : notes.value.uvaOs) || null,
  }
}

onMounted(page.load)
</script>

<template>
  <div v-if="page.error.value" class="alert alert-danger m-3" role="alert">{{ page.error.value }}</div>
  <p v-else-if="page.loading.value" class="xr-state" role="status">در حال آماده‌سازی نسخه...</p>
  <RxSlip
    v-else-if="doc"
    :title="doc.title"
    :hospital="doc.hospital"
    :patient-name="field('مریض') ?? ''"
    :file-number="field('شماره پرونده') ?? ''"
    :date="shamsiPaper(doc.date)"
    :number="doc.number"
    :od="eye(0, 'OD')"
    :os="eye(1, 'OS')"
    :pd="field('PD')"
    :lens="notes.lens"
    :notes="notes.notes"
    :signed-by="field('داکتر')"
  />
</template>
