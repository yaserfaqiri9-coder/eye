<script setup lang="ts">
import UiAvatar from '@/components/UiAvatar.vue'

defineProps<{ name: string; fileNumber?: string | null; to?: string; avatar?: boolean; gender?: string }>()
</script>

<template>
  <div class="xr-person">
    <UiAvatar v-if="avatar" :seed="fileNumber ?? name" :gender="gender" :size="38" />
    <div class="xr-person__text">
      <RouterLink v-if="to" :to="to" class="xr-person__name">{{ name }}</RouterLink>
      <span v-else class="xr-person__name">{{ name }}</span>
      <span v-if="fileNumber" class="xr-meta">پرونده <span class="ltr-num">{{ fileNumber }}</span></span>
    </div>
  </div>
</template>
