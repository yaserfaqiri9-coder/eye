<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useFloating } from './useFloating'

defineProps<{
  /** Names the button for screen readers, including which row it belongs to. */
  label: string
  items: { label: string; ariaLabel?: string; icon?: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[]
}>()

const trigger = ref<HTMLElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const open = ref(false)
const { style } = useFloating(trigger, panel, open)

function enabledItems() {
  return Array.from(panel.value?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? [])
}

async function show() {
  open.value = true
  await nextTick()
  enabledItems()[0]?.focus({ preventScroll: true })
}

function close(returnFocus = true) {
  open.value = false
  if (returnFocus) trigger.value?.focus()
}

function select(action: () => void) {
  close()
  // Stays inside the click: browsers allow sound only from one, and a menu item may start an announcement.
  action()
}

function onKeydown(event: KeyboardEvent) {
  const items = enabledItems()
  const current = items.indexOf(document.activeElement as HTMLButtonElement)
  const moves: Record<string, number> = { ArrowDown: current + 1, ArrowUp: current - 1, Home: 0, End: items.length - 1 }
  if (event.key === 'Escape') {
    event.preventDefault()
    close()
  } else if (event.key === 'Tab') {
    close(false)
  } else if (event.key in moves && items.length) {
    event.preventDefault()
    items[(moves[event.key] + items.length) % items.length].focus()
  }
}

function onOutsidePress(event: PointerEvent) {
  const target = event.target as Node
  if (!panel.value?.contains(target) && !trigger.value?.contains(target)) close(false)
}

function listen(on: boolean) {
  if (on) document.addEventListener('pointerdown', onOutsidePress, true)
  else document.removeEventListener('pointerdown', onOutsidePress, true)
}

watch(open, listen)
onBeforeUnmount(() => listen(false))
</script>

<template>
  <button
    ref="trigger"
    type="button"
    class="btn btn-quiet btn-sm xr-row-menu"
    aria-haspopup="menu"
    :aria-expanded="open"
    :aria-label="label"
    title="گزینه‌های بیشتر"
    @click="open ? close() : show()"
  >
    <i class="ri-more-2-fill" aria-hidden="true"></i>
  </button>
  <Teleport to="body">
    <div v-if="open" ref="panel" class="xr-popover xr-menu" :style="style" dir="rtl" role="menu" :aria-label="label" @keydown="onKeydown">
      <button
        v-for="item in items"
        :key="item.label"
        type="button"
        role="menuitem"
        class="xr-menu__item"
        :class="{ 'xr-menu__item--danger': item.danger }"
        :disabled="item.disabled"
        :aria-label="item.ariaLabel"
        @click="select(item.onSelect)"
      >
        <i v-if="item.icon" :class="item.icon" aria-hidden="true"></i>
        {{ item.label }}
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.xr-row-menu {
  flex: none;
  width: 30px;
  padding-inline: 0;
}
</style>
