<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    /** Stable text such as a name or file number: the same person always gets the same picture. */
    seed: string
    gender?: string
    size?: number
    /** Drops the grey disc, as on the small header picture. */
    plain?: boolean
  }>(),
  { gender: 'Other', size: 40, plain: false },
)

const skins = ['#f5c9a6', '#eab78f', '#d9a074', '#c68a5e']
const hairs = ['#1d2a55', '#2b2230', '#4a2f27', '#27384d']
const tops = ['#f4635a', '#089bab', '#fc9e5b', '#3f7fd6', '#00b894']
const scarves = ['#3f7fd6', '#089bab', '#f4635a', '#7a5cc6']

const hash = computed(() => {
  let value = 7
  for (const char of props.seed) value = (value * 31 + char.charCodeAt(0)) >>> 0
  return value
})

function pick(list: string[], shift: number) {
  return list[(hash.value >>> shift) % list.length]
}

const skin = computed(() => pick(skins, 0))
const hair = computed(() => pick(hairs, 3))
const top = computed(() => pick(tops, 6))
const scarf = computed(() => pick(scarves, 9))
const variant = computed(() => (hash.value >>> 15) % 2)
</script>

<template>
  <svg class="avatar" :width="size" :height="size" viewBox="0 0 64 64" aria-hidden="true">
    <circle v-if="!plain" cx="32" cy="32" r="32" fill="#f4f4f4" />
    <clipPath :id="`avatar-${hash}`"><circle cx="32" cy="32" r="32" /></clipPath>
    <g :clip-path="`url(#avatar-${hash})`">
      <template v-if="gender === 'Female' && variant === 0">
        <path d="M17 36c0-14 7-23 15-23s15 9 15 23c0 8-4 14-15 14s-15-6-15-14z" :fill="scarf" />
        <path d="M8 66c1-13 11-20 24-20s23 7 24 20z" :fill="top" />
        <path d="M21 50c3-4 19-4 22 0l-11 9z" :fill="scarf" />
        <ellipse cx="32" cy="31" rx="10" ry="12" :fill="skin" />
      </template>
      <template v-else-if="gender === 'Female'">
        <path d="M16 34c0-13 7-21 16-21s16 8 16 21c0 7 1 12 3 15H13c2-3 3-8 3-15z" :fill="hair" />
        <path d="M8 66c1-13 11-19 24-19s23 6 24 19z" :fill="top" />
        <rect x="27.5" y="38" width="9" height="12" rx="4" :fill="skin" />
        <ellipse cx="32" cy="30" rx="10.5" ry="12" :fill="skin" />
        <path d="M21 28c1-8 6-12 12-12 5 0 9 4 10 12-6-1-11-3-14-7-1 3-4 6-8 7z" :fill="hair" />
      </template>
      <template v-else-if="gender === 'Male'">
        <path d="M8 66c1-13 11-19 24-19s23 6 24 19z" :fill="top" />
        <rect x="27.5" y="38" width="9" height="12" rx="4" :fill="skin" />
        <ellipse cx="32" cy="30" rx="10.5" ry="12.5" :fill="skin" />
        <path d="M21 28c-1-9 5-15 11-15s12 6 11 15c-1-4-3-6-6-7-4 1-10 1-13 0-2 1-3 3-3 7z" :fill="hair" />
        <path v-if="variant === 0" d="M21.5 31c1 9 5 13.500 10.500 13.500S41.500 40 42.500 31c-1 3-3 4-5 3.500-3-1-8-1-11 0-2 .5-4-.5-5-3.500z" :fill="hair" />
      </template>
      <template v-else>
        <path d="M8 66c1-13 11-19 24-19s23 6 24 19z" fill="#00b894" />
        <path d="M25 47l7 7 7-7z" fill="#fff" />
        <ellipse cx="32" cy="31" rx="10.500" ry="12" :fill="skin" />
        <path d="M21 28c0-9 5-14 11-14s11 5 11 14c-7-3-15-3-22 0z" fill="#089bab" />
        <path d="M22 33c6-2 14-2 20 0 0 6-4 10-10 10s-10-4-10-10z" fill="#e6f9ff" />
      </template>
    </g>
  </svg>
</template>

<style scoped>
.avatar {
  flex: none;
  border-radius: 50%;
}
</style>
