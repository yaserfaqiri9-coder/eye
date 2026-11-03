<script setup lang="ts">
import { computed } from 'vue'
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

const state = computed(() => {
  if (last.value?.outcome === 'Attended') return { label: 'حاضر شد', tone: 'green' }
  if (last.value?.outcome === 'NoShow') return { label: 'عدم حضور', tone: 'red' }
  return last.value ? statusLabels[last.value.status] : null
})
</script>

<template>
  <div v-if="row" class="call-controls">
    <span v-if="state" class="xr-badge" :class="`xr-badge--${state.tone}`" :title="last?.failureReason ?? last?.destination">{{ state.label }}</span>
    <span v-if="last" class="small text-muted ltr-num" title="زمان آخرین اعلان">{{ formatTime(last.finishedAt ?? last.requestedAt) }}</span>
    <button type="button" class="btn btn-primary btn-sm text-nowrap" :disabled="active || sending" :aria-label="`فراخوان نوبت ${number}`" @click="desk.call(row!, false)">فراخوان</button>
    <button type="button" class="btn btn-soft btn-sm text-nowrap" :disabled="!last || active || sending" :aria-label="`فراخوان مجدد نوبت ${number}`" @click="desk.call(row!, true)">فراخوان مجدد</button>
    <button type="button" class="btn btn-soft btn-sm text-nowrap" :disabled="!last || sending" :aria-label="`نوبت ${number} حاضر شد`" @click="desk.mark(row!, 'Attended')">مریض حاضر شد</button>
    <button type="button" class="btn btn-soft-danger btn-sm text-nowrap" :disabled="!last || sending" :aria-label="`عدم حضور نوبت ${number}`" @click="desk.mark(row!, 'NoShow')">عدم حضور</button>
  </div>
</template>

<style scoped>
.call-controls {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
</style>
