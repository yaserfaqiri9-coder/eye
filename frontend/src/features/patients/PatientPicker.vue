<script setup lang="ts">
import { api } from '@/api/http'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import { patientName, type PatientListItem } from './types'

const model = defineModel<number | null>()
defineProps<{ label?: string; required?: boolean; selectedLabel?: string | null; disabled?: boolean }>()
const emit = defineEmits<{ select: [option: ComboOption | null] }>()

/** One patient search for every form, so results read the same everywhere and only come from the server. */
async function search(query: string, signal: AbortSignal): Promise<ComboOption[]> {
  if (!query) return []
  const page = await api<{ items: PatientListItem[] }>(`/patients?pageSize=8&q=${encodeURIComponent(query)}`, { signal })
  return page.items.map((row) => ({
    value: row.id,
    label: `${row.fileNumber} — ${patientName(row)}`,
    hint: [row.fatherName ? `پدر: ${row.fatherName}` : null, row.phone].filter(Boolean).join(' · '),
  }))
}
</script>

<template>
  <UiComboBox
    v-model="model"
    :label="label ?? 'مریض'"
    :search="search"
    :required="required"
    :disabled="disabled"
    :selected-label="selectedLabel"
    chip
    placeholder="نام، شماره پرونده یا تماس را تایپ کنید"
    empty-text="نام، شماره پرونده یا تماس را تایپ کنید."
    @select="emit('select', $event as ComboOption | null)"
  />
</template>
