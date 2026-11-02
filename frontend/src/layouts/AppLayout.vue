<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { connectRealtime, disconnectRealtime, liveConnected } from '@/api/realtime'
import AppFooter from './AppFooter.vue'
import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'

const MINI_KEY = 'alshifa.sidebar.mini'

const sidebarOpen = ref(false)
const sidebarMini = ref(readMini())
const sidebarCollapsed = ref(sidebarMini.value)
const mounted = ref(false)

function readMini() {
  try {
    return localStorage.getItem(MINI_KEY) === '1'
  } catch {
    return false
  }
}

watch(sidebarMini, (value) => {
  try {
    localStorage.setItem(MINI_KEY, value ? '1' : '0')
  } catch {
    // The choice simply is not remembered when the browser blocks storage.
  }
})

onMounted(() => {
  connectRealtime()
  // Give the first connection a moment before warning about it.
  setTimeout(() => (mounted.value = true), 3000)
})

onBeforeUnmount(disconnectRealtime)
</script>

<template>
  <div class="layout" :class="{ 'layout--mini': sidebarCollapsed }">
    <AppSidebar
      :open="sidebarOpen"
      :mini="sidebarMini"
      @close="sidebarOpen = false"
      @toggle-mini="sidebarMini = !sidebarMini"
      @collapse-change="sidebarCollapsed = $event"
    />
    <div class="layout__main">
      <AppHeader @toggle-sidebar="sidebarOpen = !sidebarOpen" />
      <main class="layout__content">
        <div v-if="mounted && !liveConnected" class="alert alert-warning" role="status">
          اتصال زنده قطع است. اطلاعات این صفحه ممکن است تازه نباشد؛ سیستم خودکار دوباره وصل می‌شود.
        </div>
        <RouterView />
      </main>
      <AppFooter />
    </div>
  </div>
</template>

<style scoped>
.layout {
  --xr-sidebar-current: var(--xr-sidebar-width);
  min-height: 100vh;
  background: var(--xr-gradient-shell);
}

/* Collapsing needs hover to slide the labels back out, so touch screens keep the full menu. */
@media (hover: hover) {
  .layout--mini {
    --xr-sidebar-current: var(--xr-sidebar-mini);
  }
}

.layout__main {
  display: flex;
  flex-direction: column;
  min-height: 100vh;
  margin-inline-start: var(--xr-sidebar-current);
  padding: 120px 27px 0;
  background: var(--xr-page-bg);
  border-start-start-radius: var(--xr-radius-card);
  border-end-start-radius: var(--xr-radius-card);
  transition: margin 0.3s ease-in-out;
}

.layout__content {
  flex: 1;
}

@media print {
  .layout {
    background: #fff;
  }

  .layout__main {
    margin: 0;
    padding: 0;
    background: #fff;
  }
}

@media (max-width: 991.98px) {
  .layout__main {
    margin-inline-start: 0;
    padding: 12px 16px 0;
    border-radius: 0;
  }

  .layout__content {
    margin-top: 16px;
  }
}
</style>
