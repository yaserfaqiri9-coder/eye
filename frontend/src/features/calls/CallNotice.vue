<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import type { CallDeskHandle } from './useCallDesk'

const auth = useAuthStore()
const props = defineProps<{ desks: CallDeskHandle[] }>()

const offline = computed(() => props.desks.some((desk) => desk.enabled && !desk.stationOnline.value))
const errors = computed(() => [...new Set(props.desks.map((desk) => desk.error.value).filter(Boolean))])
</script>

<template>
  <div v-if="offline" class="alert alert-info" role="status">
    مرکز پخش صوت فعال نیست؛ فراخوان از بلندگوی همین رایانه پخش می‌شود.
    <RouterLink v-if="auth.can('calls.manage')" :to="{ name: 'audio-station' }" class="alert-link">فعال‌سازی پخش مرکزی</RouterLink>
  </div>
  <div v-for="message in errors" :key="message" class="alert alert-danger" role="alert">{{ message }}</div>
</template>
