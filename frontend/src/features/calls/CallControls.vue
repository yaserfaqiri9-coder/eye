<script setup lang="ts">
import { computed } from 'vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import { formatTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import type { CallDeskHandle } from './useCallDesk'

const props = defineProps<{ desk: CallDeskHandle; visitId?: number | null; tokenId?: number | null }>()

const statusLabels: Record<string, { label: string; tone: string }> = {
  Queued: { label: 'در نوبت پخش', tone: 'orange' },
  Playing: { label: 'در حال پخش', tone: 'cyan' },
  Played: { label: 'اعلان شد', tone: 'primary' },
  Failed: { label: 'پخش نشد', tone: 'red' },
  Cancelled: { label: 'پخش لغو شد', tone: 'orange' },
}

const row = computed(() => props.desk.entry(props.visitId, props.tokenId))
const last = computed(() => row.value?.lastCall ?? null)
const active = computed(() => last.value?.status === 'Queued' || last.value?.status === 'Playing')
const number = computed(() => quantity(row.value?.queueNumber ?? 0))
const sending = computed(() => (row.value ? props.desk.isSending(row.value) : false))
const attended = computed(() => last.value?.outcome === 'Attended')

const state = computed(() => {
  if (attended.value) return { label: 'حاضر شد', tone: 'green' }
  if (last.value?.outcome === 'NoShow') return { label: 'عدم حضور', tone: 'red' }
  return last.value ? statusLabels[last.value.status] : null
})

// Only the next likely step stays on the row; the rest waits in the menu until a patient has been called.
const menu = computed(() => {
  if (!last.value || !row.value) return []
  const entry = row.value
  const items = []
  if (attended.value) {
    items.push({ label: 'فراخوان مجدد', ariaLabel: `فراخوان مجدد نوبت ${number.value}`, icon: 'ri-volume-up-line', disabled: active.value || sending.value, onSelect: () => void props.desk.call(entry, true) })
  } else {
    items.push({ label: 'مریض حاضر شد', ariaLabel: `نوبت ${number.value} حاضر شد`, icon: 'ri-check-line', disabled: sending.value, onSelect: () => void props.desk.mark(entry, 'Attended') })
  }
  if (last.value.outcome !== 'NoShow') {
    items.push({ label: 'عدم حضور', ariaLabel: `عدم حضور نوبت ${number.value}`, icon: 'ri-close-line', danger: true, disabled: sending.value, onSelect: () => void props.desk.mark(entry, 'NoShow') })
  }
  return items
})
</script>

<template>
  <div v-if="row" class="call-controls" role="group" :aria-label="`فراخوان نوبت ${number}`">
    <div class="call-controls__actions">
      <button
        v-if="!attended"
        type="button"
        class="btn btn-quiet btn-sm text-nowrap"
        :disabled="active || sending"
        :aria-label="`${last ? 'فراخوان مجدد' : 'فراخوان'} نوبت ${number}`"
        @click="desk.call(row!, !!last)"
      >
        <i class="ri-volume-up-line" aria-hidden="true"></i>
        {{ last ? 'فراخوان مجدد' : 'فراخوان' }}
      </button>
      <UiRowMenu v-if="menu.length" :label="`گزینه‌های بیشتر نوبت ${number}`" :items="menu" />
    </div>
    <div v-if="state" class="xr-note text-nowrap" :class="`xr-note--${state.tone}`" :title="last?.failureReason ?? last?.destination">
      <span>{{ state.label }}</span>
      <span class="ltr-num" title="زمان آخرین اعلان">{{ formatTime(last!.finishedAt ?? last!.requestedAt) }}</span>
    </div>
  </div>
</template>

<style scoped>
.call-controls {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
}

.call-controls__actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
</style>
