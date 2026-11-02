<script setup lang="ts">
defineProps<{ title: string }>()
defineEmits<{ close: [] }>()
</script>

<template>
  <Teleport to="body">
    <div class="xr-modal" role="dialog" aria-modal="true" :aria-label="title" @keydown.esc="$emit('close')">
      <div class="xr-modal__backdrop" @click="$emit('close')"></div>
      <div class="xr-modal__panel">
        <div class="xr-modal__header">
          <h2 class="xr-modal__title">{{ title }}</h2>
          <button type="button" class="btn-close m-0" aria-label="بستن" @click="$emit('close')"></button>
        </div>
        <div class="xr-modal__body"><slot /></div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.xr-modal {
  position: fixed;
  inset: 0;
  z-index: 1050;
  overflow-y: auto;
  padding: 0 16px;
}

.xr-modal__backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
}

.xr-modal__panel {
  position: relative;
  width: 100%;
  max-width: 500px;
  margin: 28px auto;
  border: 1px solid rgba(0, 0, 0, 0.176);
  border-radius: 8px;
  background: var(--xr-surface);
}

.xr-modal__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px;
  border-bottom: 1px solid var(--xr-border-table);
}

.xr-modal__title {
  margin: 0;
  font-size: 16.8px;
  line-height: 25.2px;
}

.xr-modal__body {
  padding: 16px;
}
</style>
