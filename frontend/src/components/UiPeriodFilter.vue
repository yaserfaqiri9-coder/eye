<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { todayIso } from '@/utils/date'
import { kindOf, periodChoices, quickRange, type DateRange, type PeriodKind } from '@/utils/period'
import ShamsiDateInput from './ShamsiDateInput.vue'

/**
 * The days a page shows, as one row of choices: امروز, دیروز, این هفته, این ماه, ماه گذشته and دوره دلخواه,
 * with «همه روزها» first on a list that may show every day. null means every day.
 */
const props = withDefaults(defineProps<{ allowAll?: boolean; label?: string }>(), { label: 'دوره' })
const range = defineModel<DateRange | null>({ required: true })

const today = todayIso()
const customOpen = ref(false)
const draft = reactive<{ from: string | null; to: string | null }>({ from: null, to: null })

const active = computed<PeriodKind | 'all'>(() => {
  if (customOpen.value) return 'custom'
  if (!range.value) return 'all'
  return kindOf(range.value, today)
})

const choices = computed(() => [...(props.allowAll ? [{ kind: 'all' as const, label: 'همه روزها' }] : []), ...periodChoices])

// A custom range that arrives from outside, such as from the address, shows its two dates.
watch(
  () => range.value,
  (value) => {
    if (value && kindOf(value, today) === 'custom') {
      customOpen.value = true
      Object.assign(draft, value)
    }
  },
  { immediate: true },
)

function choose(kind: PeriodKind | 'all') {
  if (kind === 'custom') {
    customOpen.value = true
    Object.assign(draft, range.value ?? { from: today, to: today })
    return
  }
  customOpen.value = false
  range.value = kind === 'all' ? null : quickRange(kind, today)
}

function showCustom() {
  if (!draft.from || !draft.to) return
  range.value = { from: draft.from, to: draft.to }
}
</script>

<template>
  <div class="period">
    <div class="period__choices" role="group" :aria-label="label">
      <button
        v-for="choice in choices"
        :key="choice.kind"
        type="button"
        class="period__choice"
        :class="{ 'period__choice--active': active === choice.kind }"
        :aria-pressed="active === choice.kind"
        @click="choose(choice.kind)"
      >{{ choice.label }}</button>
    </div>
    <form v-if="customOpen" class="period__custom" @submit.prevent="showCustom">
      <ShamsiDateInput v-model="draft.from" label="از تاریخ" required :max="draft.to ?? today" />
      <ShamsiDateInput v-model="draft.to" label="تا تاریخ" required :min="draft.from ?? undefined" :max="today" />
      <button type="submit" class="btn btn-primary period__show">نمایش</button>
    </form>
  </div>
</template>

<style scoped>
.period {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 8px 12px;
}

.period__choices {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  padding: 3px;
  border-radius: 10px;
  background: #f2f5f9;
}

.period__choice {
  min-height: 30px;
  padding: 2px 12px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: #3a4256;
  font-size: 13px;
  font-weight: 500;
  white-space: nowrap;
  transition: background-color 0.15s ease, color 0.15s ease, box-shadow 0.15s ease;
}

.period__choice:hover {
  color: var(--xr-primary);
}

.period__choice--active,
.period__choice--active:hover {
  background: var(--xr-surface);
  box-shadow: 0 1px 3px rgba(31, 42, 68, 0.12);
  color: var(--xr-tone-primary-text);
  font-weight: 700;
}

.period__choice:focus-visible {
  outline: 2px solid var(--xr-primary);
  outline-offset: 1px;
}

.period__custom {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 8px;
}

.period__custom > :deep(*:not(.btn)) {
  width: 150px;
}

.period__custom :deep(.form-label) {
  margin-bottom: 2px;
  font-size: 12px;
}

.period__custom :deep(.form-control) {
  min-height: 34px;
  padding-block: 3px;
}

/* Filter bars give native controls an automatic width; keep each date inside its own field. */
.period__custom :deep(.form-control.xr-date__input) {
  width: 100%;
  min-width: 0;
}

.period__show {
  min-height: 34px;
}

@media (max-width: 575.98px) {
  .period__choices {
    width: 100%;
  }

  .period__choice {
    flex: 1 1 calc(33.333% - 4px);
    padding-inline: 4px;
  }

  .period__custom > :deep(*:not(.btn)) {
    flex: 1 1 130px;
    width: auto;
  }
}
</style>
