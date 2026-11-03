<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'

defineProps<{
  title: string
  backTo: RouteLocationRaw
  backLabel: string
  /** Shown beside the actions as "unsaved changes". */
  dirty?: boolean
  saving?: boolean
  error?: string
  notice?: string
  /** Hides the action bar, for example once the record has been saved. */
  done?: boolean
}>()
defineEmits<{ submit: [] }>()
</script>

<template>
  <div class="d-grid gap-4">
    <slot name="summary" />

    <form class="xr-card" @submit.prevent="$emit('submit')">
      <div class="xr-card__header">
        <h1 class="xr-card__title">{{ title }}</h1>
        <RouterLink :to="backTo" class="btn btn-soft"><i class="ri-arrow-right-line" aria-hidden="true"></i> {{ backLabel }}</RouterLink>
      </div>

      <div class="xr-card__body">
        <slot />
        <div v-if="error" class="alert alert-danger mt-3 mb-0" role="alert">{{ error }}</div>
      </div>

      <div v-if="!done" class="xr-action-bar">
        <slot name="actions" />
        <span class="xr-action-bar__status" role="status">
          <template v-if="saving">در حال ذخیره...</template>
          <template v-else-if="dirty">تغییرات ذخیره نشده</template>
          <template v-else-if="notice">{{ notice }}</template>
        </span>
      </div>
    </form>
  </div>
</template>
