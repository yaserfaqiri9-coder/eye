<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { connectRealtime, disconnectRealtime, liveConnected } from '@/api/realtime'
import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'

const sidebarOpen = ref(false)
const mounted = ref(false)

onMounted(() => {
  connectRealtime()
  // Give the first connection a moment before warning about it.
  setTimeout(() => (mounted.value = true), 3000)
})

onBeforeUnmount(disconnectRealtime)
</script>

<template>
  <div class="layout">
    <AppSidebar :open="sidebarOpen" @close="sidebarOpen = false" />
    <div class="layout__main">
      <AppHeader @toggle-sidebar="sidebarOpen = !sidebarOpen" />
      <main class="layout__content">
        <div v-if="mounted && !liveConnected" class="alert alert-warning py-2" role="status">
          اتصال زنده قطع است. اطلاعات این صفحه ممکن است تازه نباشد؛ سیستم خودکار دوباره وصل می‌شود.
        </div>
        <RouterView />
      </main>
    </div>
  </div>
</template>

<style scoped>
.layout__main {
  min-height: 100vh;
  margin-inline-start: var(--xr-sidebar-width);
  padding: 20px var(--xr-gap) var(--xr-gap);
  background: var(--xr-page-bg);
  border-start-start-radius: var(--xr-radius-card);
}

.layout__content {
  margin-top: 25px;
}

@media print {
  .layout__main {
    margin: 0;
    padding: 0;
    background: #fff;
  }

  .layout__content {
    margin: 0;
  }
}

@media (max-width: 991.98px) {
  .layout__main {
    margin-inline-start: 0;
    padding: 12px 16px 16px;
    border-radius: 0;
  }
}
</style>
