<script setup lang="ts">
defineProps<{ title: string }>()
defineEmits<{ close: [] }>()
</script>

<template>
  <Teleport to="body">
    <div class="xr-modal" role="dialog" aria-modal="true" :aria-label="title" @keydown.esc="$emit('close')">
      <div class="xr-modal__backdrop" @click="$emit('close')"></div>
      <div class="xr-modal__panel xr-card">
        <div class="xr-card__header">
          <h2 class="xr-card__title">{{ title }}</h2>
          <button type="button" class="btn-close m-0" aria-label="بستن" @click="$emit('close')"></button>
        </div>
        <div class="xr-card__body"><slot /></div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.xr-modal {
  position: fixed;
  inset: 0;
  z-index: 1050;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
}

.xr-modal__backdrop {
  position: absolute;
  inset: 0;
  background: rgba(0, 0, 0, 0.35);
}

.xr-modal__panel {
  position: relative;
  width: 100%;
  max-width: 520px;
  max-height: 100%;
  overflow-y: auto;
}
</style>
