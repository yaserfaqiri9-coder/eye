<script setup lang="ts">
import { bootError } from '@/router'

function retry() {
  window.location.reload()
}
</script>

<template>
  <div v-if="bootError" class="boot-error" role="alert">
    <i class="bi bi-exclamation-triangle-fill boot-error__icon" aria-hidden="true"></i>
    <p class="boot-error__message">{{ bootError }}</p>
    <button type="button" class="btn btn-primary" @click="retry">تلاش دوباره</button>
  </div>
  <RouterView v-else />
</template>

<style scoped>
.boot-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 16px;
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
  text-align: center;
}

.boot-error__icon {
  color: var(--xr-orange);
  font-size: 48px;
}

.boot-error__message {
  margin: 0;
  font-size: 18px;
}
</style>
