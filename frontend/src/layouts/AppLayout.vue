<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api/http'
import { connectRealtime, disconnectRealtime, liveConnected } from '@/api/realtime'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import AppFooter from './AppFooter.vue'
import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'

const MINI_KEY = 'alshifa.sidebar.mini'
const BACKUP_CHECK_MS = 10 * 60_000

const auth = useAuthStore()
const sidebarOpen = ref(false)
const sidebarMini = ref(readMini())
const mounted = ref(false)
const backupProblem = ref<{ lastSuccessAt: string | null } | null>(null)
let backupTimer: ReturnType<typeof setInterval> | undefined

async function checkBackup() {
  try {
    const status = await api<{ needsAttention: boolean; lastSuccessAt: string | null }>('/settings/backup-status')
    backupProblem.value = status.needsAttention ? status : null
  } catch {
    // A failed check says nothing about the backup; the connection warning already covers an unreachable server.
  }
}

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
  if (auth.can('settings.view')) {
    checkBackup()
    backupTimer = setInterval(checkBackup, BACKUP_CHECK_MS)
  }
})

onBeforeUnmount(() => {
  disconnectRealtime()
  clearInterval(backupTimer)
})
</script>

<template>
  <div class="layout" :class="{ 'layout--mini': sidebarMini }">
    <AppSidebar :open="sidebarOpen" :mini="sidebarMini" @close="sidebarOpen = false" @toggle-mini="sidebarMini = !sidebarMini" />
    <div class="layout__main">
      <AppHeader @toggle-sidebar="sidebarOpen = !sidebarOpen" />
      <main class="layout__content">
        <div v-if="mounted && !liveConnected" class="alert alert-warning" role="status">
          اتصال زنده قطع است. اطلاعات این صفحه ممکن است تازه نباشد؛ سیستم خودکار دوباره وصل می‌شود.
        </div>
        <div v-if="backupProblem" class="alert alert-danger" role="alert">
          پشتیبان‌گیری خودکار مشکل دارد. آخرین پشتیبان موفق: {{ formatDateTime(backupProblem.lastSuccessAt) }}. به مسئول سیستم اطلاع دهید.
        </div>
        <RouterView />
      </main>
      <AppFooter />
    </div>
  </div>
</template>

<style scoped>
/* The template's gradient runs from the sidebar across the page; mirrored here so the sidebar keeps its darker end. */
.layout {
  --xr-sidebar-current: var(--xr-sidebar-width);
  min-height: 100vh;
  background: linear-gradient(to left, var(--xr-primary) 0%, var(--xr-primary-end) 100%);
}

.layout--mini {
  --xr-sidebar-current: var(--xr-sidebar-mini);
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
