<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { isMenuParent, menu, type MenuEntry, type MenuItem, type MenuParent } from './menu'

const props = defineProps<{ open: boolean; mini: boolean }>()
const emit = defineEmits<{ close: []; 'toggle-mini': []; 'collapse-change': [collapsed: boolean] }>()

const auth = useAuthStore()
const route = useRoute()

const hovered = ref(false)
const focused = ref(false)
const collapsed = computed(() => props.mini && !hovered.value && !focused.value)

/** The page makes room for the slid-out menu instead of sitting under it. */
watch(collapsed, (value) => emit('collapse-change', value))

/** A finger has no hover: its enter and leave would only flash the full menu open. */
function onPointerEnter(event: PointerEvent) {
  if (event.pointerType !== 'touch') hovered.value = true
}

/** Only keyboard focus opens the menu, so a clicked link does not hold it open after the pointer leaves. */
function onFocusIn(event: FocusEvent) {
  focused.value = (event.target as HTMLElement).matches(':focus-visible')
}

function onFocusOut(event: FocusEvent) {
  const inside = (event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)
  if (!inside) focused.value = false
}

/** The pointer is still over the menu after this click; without clearing it the menu would stay open over the page. */
function toggleMini() {
  hovered.value = false
  focused.value = false
  emit('toggle-mini')
}

const RAIL_HEIGHT = 88
const MIN_THUMB = 22

const scroller = ref<HTMLElement | null>(null)
const list = ref<HTMLElement | null>(null)
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
  // A submenu sliding open changes the list's height, not the scroller's.
  if (list.value) observer.observe(list.value)
})

onBeforeUnmount(() => observer?.disconnect())

const allowed = (item: MenuItem) =>
  (!item.permission || auth.can(item.permission)) && !auth.user?.hiddenMenu?.includes(item.to)

const sections = computed(() =>
  menu
    .map((section) => ({
      ...section,
      items: section.items.flatMap((entry): MenuEntry[] => {
        if (!isMenuParent(entry)) return allowed(entry) ? [entry] : []
        const children = entry.children.filter(allowed)
        // A single page left for this role needs no submenu to open first.
        return children.length > 1 ? [{ ...entry, children }] : children
      }),
    }))
    .filter((section) => section.items.length > 0),
)

function isItemActive(item: MenuItem) {
  if (item.activeOn?.includes(String(route.name ?? ''))) return true
  if (item.to === '/') return route.path === '/'
  return route.path === item.to || route.path.startsWith(`${item.to}/`)
}

function isParentActive(parent: MenuParent) {
  return parent.children.some(isItemActive)
}

const openParents = ref(new Set<string>())

function toggleParent(label: string) {
  const next = new Set(openParents.value)
  if (next.has(label)) next.delete(label)
  else next.add(label)
  openParents.value = next
}

/** Like the template after a page load: arriving on a page leaves only its own submenu open. */
watch(
  [sections, () => route.path],
  () => {
    const parents = sections.value.flatMap((section) => section.items.filter(isMenuParent))
    openParents.value = new Set(parents.filter(isParentActive).map((parent) => parent.label))
  },
  { immediate: true },
)
</script>

<template>
  <div v-if="open" class="sidebar-backdrop" @click="$emit('close')"></div>
  <aside
    class="sidebar"
    :class="{ 'sidebar--open': open, 'sidebar--mini': collapsed, 'sidebar--dash-titles': mini }"
    @pointerenter="onPointerEnter"
    @pointerleave="hovered = false"
    @focusin="onFocusIn"
    @focusout="onFocusOut"
  >
    <div class="sidebar__brand">
      <i class="ri-eye-fill sidebar__logo" aria-hidden="true"></i>
      <span class="sidebar__name">الشفا</span>
      <button
        type="button"
        class="sidebar__toggle"
        :aria-label="mini ? 'باز نگه داشتن منو' : 'جمع کردن منو'"
        :title="mini ? 'باز نگه داشتن منو' : 'جمع کردن منو'"
        @click="toggleMini"
      >
        <i :class="mini ? 'ri-more-2-fill' : 'ri-more-fill'" aria-hidden="true"></i>
      </button>
    </div>
    <div ref="scroller" class="sidebar__scroll" @scroll.passive="measure">
      <nav ref="list" aria-label="منوی اصلی">
        <ul class="sidebar__menu">
          <template v-for="section in sections" :key="section.title">
            <li class="sidebar__title" :title="mini ? section.title : undefined">
              <i class="ri-subtract-line" aria-hidden="true"></i>
              <span>{{ section.title }}</span>
            </li>
            <template v-for="entry in section.items" :key="entry.label">
              <li
                v-if="isMenuParent(entry)"
                class="sidebar__item"
                :class="{
                  'sidebar__item--active': isParentActive(entry),
                  'sidebar__item--open': openParents.has(entry.label),
                }"
              >
                <button
                  type="button"
                  class="sidebar__link"
                  :aria-expanded="openParents.has(entry.label)"
                  :aria-controls="`sidebar-submenu-${entry.label}`"
                  :title="collapsed ? entry.label : undefined"
                  @click="toggleParent(entry.label)"
                >
                  <i :class="entry.icon" aria-hidden="true"></i>
                  <span>{{ entry.label }}</span>
                  <i class="ri-arrow-left-s-line sidebar__arrow" aria-hidden="true"></i>
                </button>
                <div :id="`sidebar-submenu-${entry.label}`" class="sidebar__submenu">
                  <ul class="sidebar__submenu-list">
                    <li
                      v-for="child in entry.children"
                      :key="child.to"
                      class="sidebar__child"
                      :class="{ 'sidebar__child--active': isItemActive(child) }"
                    >
                      <RouterLink
                        :to="child.to"
                        class="sidebar__link"
                        :aria-current="isItemActive(child) ? 'page' : undefined"
                        :title="collapsed ? child.label : undefined"
                        @click="$emit('close')"
                      >
                        <i :class="child.icon" aria-hidden="true"></i>
                        <span>{{ child.label }}</span>
                      </RouterLink>
                    </li>
                  </ul>
                </div>
              </li>
              <li v-else class="sidebar__item" :class="{ 'sidebar__item--active': isItemActive(entry) }">
                <RouterLink
                  :to="entry.to"
                  class="sidebar__link"
                  :aria-current="isItemActive(entry) ? 'page' : undefined"
                  :title="collapsed ? entry.label : undefined"
                  @click="$emit('close')"
                >
                  <i :class="entry.icon" aria-hidden="true"></i>
                  <span>{{ entry.label }}</span>
                </RouterLink>
              </li>
            </template>
          </template>
        </ul>
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
  /* Fixed so it lines up with the layout's gradient; the page's rounded corners pass under the menu as both animate. */
  background: var(--xr-gradient-shell) fixed;
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

.sidebar__menu,
.sidebar__submenu-list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.sidebar__title {
  padding: 15px;
  color: #fff;
  font-size: 15px;
  font-weight: 500;
  line-height: 18px;
  white-space: nowrap;
}

.sidebar__title i {
  display: none;
  font-size: 20px;
}

/* A white card holds the active row, or an open row together with its whole submenu. */
.sidebar__item {
  overflow: hidden;
  border-radius: var(--xr-radius-card);
  transition: background-color 0.5s ease-out, box-shadow 0.5s ease-out;
}

.sidebar__item--active,
.sidebar__item--open {
  margin-bottom: 10px;
  background: #fff;
  box-shadow: var(--xr-shadow-active);
}

.sidebar__link {
  display: flex;
  align-items: center;
  width: 100%;
  min-height: 47px;
  padding: 15px;
  border: 0;
  border-radius: var(--xr-radius-link);
  background: transparent;
  color: #fff;
  font-family: var(--xr-font);
  font-size: 14px;
  line-height: 17px;
  text-align: start;
  white-space: nowrap;
  cursor: pointer;
  transition: color 0.5s ease-out;
}

.sidebar__link:hover {
  color: #fff;
}

.sidebar__link i {
  flex: none;
  width: 20px;
  margin-inline-end: 10px;
  font-size: 20px;
  line-height: 17px;
}

.sidebar__item--active > .sidebar__link,
.sidebar__item--open > .sidebar__link {
  color: var(--xr-primary);
}

.sidebar__link .sidebar__arrow {
  margin-inline: auto 0;
  transition: transform 0.3s ease-in-out;
}

.sidebar__item--open .sidebar__arrow {
  transform: rotate(-90deg);
}

.sidebar__submenu {
  display: grid;
  grid-template-rows: 0fr;
  visibility: hidden;
  transition: grid-template-rows 0.3s ease-in-out, visibility 0.3s;
}

.sidebar__item--open > .sidebar__submenu {
  grid-template-rows: 1fr;
  visibility: visible;
}

.sidebar__submenu-list {
  min-height: 0;
  overflow: hidden;
}

.sidebar__child .sidebar__link {
  min-height: 41px;
  padding: 12px 15px;
  color: var(--xr-sidebar-child);
}

.sidebar__child .sidebar__link i {
  font-size: 16px;
}

.sidebar__child .sidebar__link:hover {
  color: var(--xr-primary);
}

.sidebar__child--active .sidebar__link,
.sidebar__child--active .sidebar__link:hover {
  border-radius: 0;
  background: var(--xr-primary-light);
  color: var(--xr-primary);
}

.sidebar__link:focus-visible,
.sidebar__toggle:focus-visible {
  outline: 3px solid #fff;
  outline-offset: -3px;
}

/* White would disappear on the white card. */
.sidebar__item--active .sidebar__link:focus-visible,
.sidebar__item--open .sidebar__link:focus-visible {
  outline-color: var(--xr-primary);
}

/* Collapsed: icons only, and the full menu slides back out while the pointer or keyboard focus is in it. */
@media (min-width: 992px) and (hover: hover) {
  .sidebar--mini {
    width: var(--xr-sidebar-mini);
  }

  .sidebar--mini .sidebar__name,
  .sidebar--mini .sidebar__toggle,
  .sidebar--mini .sidebar__link span,
  .sidebar--mini .sidebar__arrow {
    display: none;
  }

  .sidebar--mini .sidebar__link i {
    margin-inline: 0;
  }

  /* The dash stays while the pointer slides the menu out; titles return only when the menu is kept open. */
  .sidebar--dash-titles .sidebar__title span {
    display: none;
  }

  .sidebar--dash-titles .sidebar__title i {
    display: block;
  }
}

@media (hover: none) {
  .sidebar__toggle {
    display: none;
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

@media (prefers-reduced-motion: reduce) {
  .sidebar__submenu,
  .sidebar__arrow,
  .sidebar__item,
  .sidebar__link {
    transition: none;
  }
}
</style>
