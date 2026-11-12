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
    :class="{ 'sidebar--open': open, 'sidebar--mini': collapsed }"
    @pointerenter="onPointerEnter"
    @pointerleave="hovered = false"
    @focusin="onFocusIn"
    @focusout="onFocusOut"
  >
    <div ref="scroller" class="sidebar__scroll" @scroll.passive="measure">
      <nav ref="list" aria-label="منوی اصلی">
        <ul class="sidebar__menu">
          <template v-for="section in sections" :key="section.title">
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
    <div class="sidebar__footer">
      <button type="button" class="sidebar__toggle" :aria-label="mini ? 'باز نگه داشتن منو' : 'جمع کردن منو'" :title="mini ? 'باز نگه داشتن منو' : 'جمع کردن منو'" @click="toggleMini">
        <i class="ri-side-bar-fill" aria-hidden="true"></i><span>{{ mini ? 'باز نگه داشتن منو' : 'جمع کردن منو' }}</span>
      </button>
      <button type="button" class="sidebar__close" aria-label="بستن منو" @click="$emit('close')"><i class="ri-close-line" aria-hidden="true"></i><span>بستن منو</span></button>
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
  background: var(--xr-gradient-shell) fixed;
  color: #fff;
  box-shadow: var(--xr-shadow-bar);
  transition: width .3s ease-in-out;
}
.sidebar__scroll { flex: 1; min-height: 0; margin-top: 24px; padding: 0 12px 12px; overflow-x: hidden; overflow-y: auto; scrollbar-width: none; }
.sidebar__scroll::-webkit-scrollbar { display: none; }
.sidebar__menu, .sidebar__submenu-list { margin: 0; padding: 0; list-style: none; }
.sidebar__item { margin-bottom: 12px; overflow: hidden; border-radius: 25px; background: transparent; box-shadow: none; }
.sidebar__link { display: flex; align-items: center; gap: 14px; width: 100%; min-height: 60px; padding: 17px 16px; border: 0; border-radius: 5px; background: transparent; color: #fff; font-family: var(--xr-font); font-size: 17px; font-weight: 500; line-height: 26px; text-align: start; white-space: nowrap; cursor: pointer; transition: background-color .15s; }
.sidebar__link i { flex: 0 0 24px; width: 24px; font-size: 24px; line-height: 26px; text-align: center; }
.sidebar__link span { min-width: 0; }
.sidebar__link:hover { color: #fff; background: rgba(255,255,255,.12); }
.sidebar__item--active, .sidebar__item--open { background: #fff; box-shadow: var(--xr-shadow-active); }
.sidebar__item--active > .sidebar__link, .sidebar__item--open > .sidebar__link,
.sidebar__item--active > .sidebar__link:hover, .sidebar__item--open > .sidebar__link:hover { background: #fff; color: var(--xr-primary); }
.sidebar__link .sidebar__arrow { margin-inline-start: auto; flex: 0 0 16px; font-size: 16px; transition: transform .15s; }
.sidebar__item--open .sidebar__arrow { transform: rotate(-90deg); }
.sidebar__submenu { display: grid; grid-template-rows: 0fr; visibility: hidden; transition: grid-template-rows .2s ease-in-out, visibility .2s; }
.sidebar__item--open > .sidebar__submenu { grid-template-rows: 1fr; visibility: visible; }
.sidebar__submenu-list { min-height: 0; overflow: hidden; padding-inline-start: 0; }
.sidebar__child { margin-top: 0; }
.sidebar__child .sidebar__link { min-height: 52px; padding: 13px 16px; border-radius: 0; color: var(--xr-sidebar-child); font-size: 15px; }
.sidebar__child .sidebar__link i { font-size: 22px; }
.sidebar__child .sidebar__link:hover { background: var(--xr-page-bg); color: var(--xr-primary); }
.sidebar__child--active > .sidebar__link, .sidebar__child--active > .sidebar__link:hover { background: var(--xr-primary-light); color: var(--xr-primary); }
.sidebar__item--active .sidebar__link:focus-visible, .sidebar__item--open .sidebar__link:focus-visible { outline-color: var(--xr-primary); }
.sidebar__link:focus-visible, .sidebar__toggle:focus-visible, .sidebar__close:focus-visible { outline: 2px solid #fff; outline-offset: -3px; }
.sidebar__footer { flex: none; padding: 10px 12px; border-top: 1px solid rgba(255,255,255,.14); }
.sidebar__toggle, .sidebar__close { display: flex; align-items: center; gap: 12px; width: 100%; min-height: 40px; padding: 8px 14px; border: 0; border-radius: 5px; background: transparent; color: #fff; font: inherit; font-size: 12px; text-align: start; cursor: pointer; }
.sidebar__toggle i, .sidebar__close i { width: 20px; flex: 0 0 20px; text-align: center; font-size: 20px; }
.sidebar__toggle:hover, .sidebar__close:hover { background: rgba(255,255,255,.12); }
.sidebar__close { display: none; }
.sidebar__rail { position: absolute; inset-inline-end: 3px; top: 50%; width: 3px; height: 88px; border-radius: 3px; background: rgba(255,255,255,.18); opacity: 0; transform: translateY(-50%); transition: opacity .2s; }
.sidebar:hover .sidebar__rail { opacity: 1; }
.sidebar__thumb { position: relative; width: 100%; border-radius: 3px; background: rgba(255,255,255,.75); cursor: grab; touch-action: none; }
.sidebar__thumb::before { content: ''; position: absolute; inset: -4px -6px; }
.sidebar__thumb:hover, .sidebar__thumb:active { background: #fff; }
.sidebar__thumb:active { cursor: grabbing; }
@media (min-width: 992px) and (hover: hover) {
  .sidebar--mini { width: var(--xr-sidebar-mini); }
  .sidebar--mini .sidebar__link span, .sidebar--mini .sidebar__toggle span, .sidebar--mini .sidebar__arrow { display: none; }
  .sidebar--mini .sidebar__link, .sidebar--mini .sidebar__toggle { justify-content: center; padding-inline: 0; }
  .sidebar--mini .sidebar__submenu-list { padding-inline-start: 0; }
}
@media (hover: none) { .sidebar__toggle { display: none; } }
@media (min-width: 992px) and (hover: none) { .sidebar__footer { display: none; } }
.sidebar-backdrop { display: none; }
@media (max-width: 991.98px) {
  .sidebar { background: var(--xr-gradient-primary); transform: translateX(100%); transition: transform .15s; }
  .sidebar__toggle { display: none; }
  .sidebar__close { display: flex; }
  .sidebar__footer { padding: 10px 12px; border-top: 1px solid rgba(255,255,255,.14); }
  .sidebar--open { transform: none; }
  .sidebar-backdrop { display: block; position: fixed; inset: 0; z-index: 1029; background: rgba(0,0,0,.35); }
}
@media (prefers-reduced-motion: reduce) { .sidebar, .sidebar__submenu, .sidebar__arrow, .sidebar__item, .sidebar__link { transition: none; } }
</style>
