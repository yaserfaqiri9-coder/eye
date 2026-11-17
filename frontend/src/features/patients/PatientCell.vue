<script setup lang="ts">
import UiAvatar from '@/components/UiAvatar.vue'

/** `note` sits on the file-number line (for example the age), so the cell stays two lines. */
defineProps<{ name: string; fileNumber?: string | null; to?: string; avatar?: boolean; gender?: string; note?: string }>()
</script>

<template>
  <div class="xr-person">
    <UiAvatar v-if="avatar" :seed="fileNumber ?? name" :gender="gender" :size="38" />
    <div class="xr-person__text">
      <RouterLink v-if="to" :to="to" class="xr-person__name">{{ name }}</RouterLink>
      <span v-else class="xr-person__name">{{ name }}</span>
      <span v-if="fileNumber || note" class="xr-meta">
        <template v-if="fileNumber">پرونده <span class="ltr-num">{{ fileNumber }}</span></template>
        <template v-if="fileNumber && note"> · </template>
        <template v-if="note">{{ note }}</template>
      </span>
    </div>
  </div>
</template>
