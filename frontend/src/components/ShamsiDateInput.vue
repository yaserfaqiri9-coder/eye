<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { afghanMonths, shamsiMonthLength, shamsiToIso } from '@/utils/date'

const model = defineModel<string | null>()
const props = defineProps<{ label: string; initial?: { year: number; month: number; day: number } }>()

const year = ref<number | ''>(props.initial?.year ?? '')
const month = ref<number | ''>(props.initial?.month ?? '')
const day = ref<number | ''>(props.initial?.day ?? '')

const dayCount = computed(() => (year.value && month.value ? shamsiMonthLength(year.value, month.value) : 31))

watch(
  [year, month, day],
  () => {
    if (day.value && day.value > dayCount.value) day.value = dayCount.value
    model.value = year.value && month.value && day.value ? shamsiToIso(year.value, month.value, day.value) : null
  },
  { immediate: true },
)
</script>

<template>
  <fieldset>
    <legend class="form-label">{{ label }}</legend>
    <div class="row g-2">
      <div class="col-3">
        <input v-model.number="day" class="form-control" type="number" min="1" :max="dayCount" placeholder="روز" :aria-label="`${label}: روز`" />
      </div>
      <div class="col-5">
        <select v-model.number="month" class="form-select" :aria-label="`${label}: ماه`">
          <option value="">ماه</option>
          <option v-for="(name, index) in afghanMonths" :key="name" :value="index + 1">{{ name }}</option>
        </select>
      </div>
      <div class="col-4">
        <input v-model.number="year" class="form-control" type="number" min="1300" max="1500" placeholder="سال" :aria-label="`${label}: سال`" />
      </div>
    </div>
  </fieldset>
</template>
