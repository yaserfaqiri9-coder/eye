<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { menu } from './menu'

defineProps<{ open: boolean; mini: boolean }>()
defineEmits<{ close: []; 'toggle-mini': [] }>()

const auth = useAuthStore()

const RAIL_HEIGHT = 88
const MIN_THUMB = 22

const scroller = ref<HTMLElement | null>(null)
const thumb = ref({ show: false, height: MIN_THUMB, top: 0 })
let observer: ResizeObserver | null = null

function measure() {
  const el = scroller.value
  if (!el) return
  const range = el.scrollHeight - el.clientHeight
  const height = Math.max(MIN_THUMB, Math.round((RAIL_HEIGHT * el.clientHeight) / el.scrollHeight))
  thumb.value = {
    show: range > 1,
    height,
    top: range > 1 ? Math.round(((RAIL_HEIGHT - height) * el.scrollTop) / range) : 0,
  }
}

/** Dragging the short rail moves the whole list, scaled by how much longer the list is than the rail. */
function startDrag(event: PointerEvent) {
  const el = scroller.value
  if (!el) return
  const target = event.currentTarget as HTMLElement
  target.setPointerCapture(event.pointerId)
  const startY = event.clientY
  const startScroll = el.scrollTop
  const scale = (el.scrollHeight - el.clientHeight) / (RAIL_HEIGHT - thumb.value.height)
  const move = (e: PointerEvent) => {
    el.scrollTop = startScroll + (e.clientY - startY) * scale
  }
  const stop = () => {
    target.removeEventListener('pointermove', move)
    target.removeEventListener('pointerup', stop)
    target.removeEventListener('pointercancel', stop)
  }
  target.addEventListener('pointermove', move)
  target.addEventListener('pointerup', stop)
  target.addEventListener('pointercancel', stop)
}

onMounted(() => {
  measure()
  observer = new ResizeObserver(measure)
  if (scroller.value) observer.observe(scroller.value)
})

onBeforeUnmount(() => observer?.disconnect())

const sections = computed(() =>
  menu
    .map((section) => ({
      ...section,
      items: section.items.filter((item) => !item.permission || auth.can(item.permission)),
    }))
    .filter((section) => section.items.length > 0),
)

watch(sections, () => nextTick(measure))
</script>

<template>
  <div v-if="open" class="sidebar-backdrop" @click="$emit('close')"></div>
  <aside class="sidebar" :class="{ 'sidebar--open': open, 'sidebar--mini': mini }">
    <div class="sidebar__brand">
      <i class="ri-eye-fill sidebar__logo" aria-hidden="true"></i>
      <span class="sidebar__name">الشفا</span>
      <button type="button" class="sidebar__toggle" aria-label="جمع کردن منو" title="جمع کردن منو" @click="$emit('toggle-mini')">
        <i class="ri-more-fill" aria-hidden="true"></i>
      </button>
    </div>
    <div ref="scroller" class="sidebar__scroll" @scroll.passive="measure">
      <nav>
        <template v-for="section in sections" :key="section.title">
          <div class="sidebar__section"><span>{{ section.title }}</span></div>
          <RouterLink
            v-for="item in section.items"
            :key="item.to"
            :to="item.to"
            class="sidebar__link"
            exact-active-class="sidebar__link--active"
            :title="mini ? item.label : undefined"
            @click="$emit('close')"
          >
            <i :class="item.icon" aria-hidden="true"></i>
            <span>{{ item.label }}</span>
          </RouterLink>
        </template>
      </nav>
    </div>
    <div v-if="thumb.show" class="sidebar__rail" aria-hidden="true">
      <div
        class="sidebar__thumb"
        :style="{ height: `${thumb.height}px`, transform: `translateY(${thumb.top}px)` }"
        @pointerdown.prevent="startDrag"
      ></div>
    </div>
  </aside>
</template>

<style scoped>
.sidebar {
  position: fixed;
  inset-block: 0;
  inset-inline-start: 0;
  z-index: 1030;
  display: flex;
  flex-direction: column;
  width: var(--xr-sidebar-width);
  overflow: hidden;
  color: #fff;
  box-shadow: var(--xr-shadow-bar);
  transition: width 0.3s ease-in-out;
}

.sidebar__scroll {
  flex: 1;
  min-height: 0;
  margin-top: 15px;
  padding: 0 15px 20px;
  overflow-x: hidden;
  overflow-y: auto;
  scrollbar-width: none;
}

.sidebar__scroll::-webkit-scrollbar {
  display: none;
}

/* A short rail centred on the sidebar edge replaces the full-height native scrollbar. */
.sidebar__rail {
  position: absolute;
  inset-inline-end: 4px;
  top: 50%;
  width: 3px;
  height: 88px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.18);
  opacity: 0;
  transform: translateY(-50%);
  transition: opacity 0.2s;
}

/* At rest the template shows no scrollbar; the rail appears once the pointer is over the menu. */
.sidebar:hover .sidebar__rail {
  opacity: 1;
}

.sidebar__thumb {
  position: relative;
  width: 100%;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.75);
  cursor: grab;
  touch-action: none;
}

/* Wider invisible grab area: the visible thumb alone is too thin to hit with a mouse. */
.sidebar__thumb::before {
  content: '';
  position: absolute;
  inset: -4px -6px;
}

.sidebar__thumb:hover,
.sidebar__thumb:active {
  background: #fff;
}

.sidebar__thumb:active {
  cursor: grabbing;
}

.sidebar__brand {
  display: flex;
  flex: none;
  align-items: center;
  height: 60px;
  padding-block-start: 15px;
  padding-inline-start: 20px;
}

.sidebar__logo {
  flex: none;
  width: 40px;
  font-size: 38px;
  line-height: 45px;
  text-align: center;
}

.sidebar__name {
  margin-inline-start: 10px;
  font-size: 28px;
  line-height: 45px;
  white-space: nowrap;
}

.sidebar__toggle {
  flex: none;
  width: 44px;
  height: 41px;
  margin-inline: auto 15px;
  padding: 0;
  border: 0;
  border-radius: 12px;
  background: transparent;
  color: #fff;
  font-size: 30px;
  line-height: 41px;
}

.sidebar__section {
  padding: 15px;
  font-size: 15px;
  font-weight: 500;
  line-height: 18px;
  white-space: nowrap;
}

.sidebar__link {
  display: flex;
  align-items: center;
  min-height: 47px;
  padding: 15px;
  border-radius: var(--xr-radius-link);
  color: #fff;
  font-size: 14px;
  line-height: 17px;
  white-space: nowrap;
}

.sidebar__link i {
  flex: none;
  margin-inline-end: 10px;
  font-size: 20px;
  line-height: 17px;
}

.sidebar__link:hover {
  color: #fff;
}

.sidebar__link--active,
.sidebar__link--active:hover {
  margin-bottom: 10px;
  border-radius: var(--xr-radius-card);
  background: #fff;
  color: var(--xr-primary);
  box-shadow: var(--xr-shadow-active);
}

/* Collapsed: icons only, and the full menu slides back out while the pointer is over it. */
@media (min-width: 992px) {
  .sidebar--mini:not(:hover) {
    width: var(--xr-sidebar-mini);
  }

  .sidebar--mini:not(:hover) .sidebar__name,
  .sidebar--mini:not(:hover) .sidebar__toggle,
  .sidebar--mini:not(:hover) .sidebar__link span,
  .sidebar--mini:not(:hover) .sidebar__section span {
    display: none;
  }

  .sidebar--mini:not(:hover) .sidebar__section {
    min-height: 48px;
  }
}

.sidebar-backdrop {
  display: none;
}

@media (max-width: 991.98px) {
  .sidebar {
    background: var(--xr-gradient-primary);
    transform: translateX(100%);
    transition: transform 0.15s;
  }

  .sidebar__toggle {
    display: none;
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
