<script setup lang="ts">
import { computed } from 'vue'
import type { CallDeskHandle } from './useCallDesk'

const props = defineProps<{ desks: CallDeskHandle[] }>()

const offline = computed(() => props.desks.some((desk) => desk.enabled && !desk.stationOnline.value))
const errors = computed(() => [...new Set(props.desks.map((desk) => desk.error.value).filter(Boolean))])
</script>

<template>
  <div v-if="offline" class="alert alert-warning" role="status">مرکز پخش صوت فعال نیست؛ فراخوان تا فعال‌شدن آن پخش نمی‌شود.</div>
  <div v-for="message in errors" :key="message" class="alert alert-danger" role="alert">{{ message }}</div>
</template>
