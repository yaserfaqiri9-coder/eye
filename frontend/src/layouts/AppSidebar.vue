<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { menu } from './menu'

defineProps<{ open: boolean }>()
defineEmits<{ close: [] }>()

const auth = useAuthStore()

const sections = computed(() =>
  menu
    .map((section) => ({
      ...section,
      items: section.items.filter((item) => !item.permission || auth.can(item.permission)),
    }))
    .filter((section) => section.items.length > 0),
)
</script>

<template>
  <div v-if="open" class="sidebar-backdrop" @click="$emit('close')"></div>
  <aside class="sidebar" :class="{ 'sidebar--open': open }">
    <div class="sidebar__brand">
      <i class="bi bi-eye-fill" aria-hidden="true"></i>
      <span>الشفا</span>
    </div>
    <nav>
      <template v-for="section in sections" :key="section.title">
        <div class="sidebar__section">{{ section.title }}</div>
        <RouterLink
          v-for="item in section.items"
          :key="item.to"
          :to="item.to"
          class="sidebar__link"
          exact-active-class="sidebar__link--active"
          @click="$emit('close')"
        >
          <i class="bi" :class="item.icon" aria-hidden="true"></i>
          <span>{{ item.label }}</span>
        </RouterLink>
      </template>
    </nav>
  </aside>
</template>

<style scoped>
.sidebar {
  position: fixed;
  inset-block: 0;
  inset-inline-start: 0;
  z-index: 1030;
  width: var(--xr-sidebar-width);
  padding: 0 15px 20px;
  overflow-y: auto;
  background: var(--xr-primary);
  color: #fff;
}

.sidebar__brand {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 75px;
  padding-inline: 5px;
  font-size: 26px;
}

.sidebar__section {
  padding: 12px 15px;
  font-size: 15px;
  font-weight: 700;
}

.sidebar__link {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 47px;
  margin-bottom: 2px;
  padding-inline: 15px;
  border-radius: var(--xr-radius-pill);
  color: #fff;
  font-size: 14px;
  font-weight: 500;
  text-decoration: none;
}

.sidebar__link .bi {
  font-size: 17px;
}

.sidebar__link:hover {
  background: rgba(255, 255, 255, 0.12);
}

.sidebar__link--active,
.sidebar__link--active:hover {
  background: #fff;
  color: var(--xr-primary);
}

.sidebar-backdrop {
  display: none;
}

@media (max-width: 991.98px) {
  .sidebar {
    transform: translateX(100%);
    transition: transform 0.15s;
  }

  .sidebar--open {
    transform: none;
  }

  .sidebar-backdrop {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 1029;
    background: rgba(0, 0, 0, 0.35);
  }
}
</style>
