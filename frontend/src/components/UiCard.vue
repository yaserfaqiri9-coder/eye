<script setup lang="ts">
defineProps<{ title?: string }>()
</script>

<template>
  <section class="xr-card">
    <div v-if="title || $slots.actions" class="xr-card__header">
      <h2 class="xr-card__title">{{ title }}</h2>
      <slot name="actions" />
    </div>
    <div class="xr-card__body"><slot /></div>
  </section>
</template>
