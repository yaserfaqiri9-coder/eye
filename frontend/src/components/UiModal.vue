<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'

defineProps<{ title: string; size?: 'lg' | 'xl' }>()
const emit = defineEmits<{ close: [] }>()

const panel = ref<HTMLElement | null>(null)
const dirty = ref(false)
const confirming = ref(false)

const focusable = 'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled)'

/** Every form here disables its submit button while saving, and only then. */
function isSaving() {
  return !!panel.value?.querySelector('button[type="submit"]:disabled')
}

/** A dialog that moved on to a result or a warning has no edits left to lose. */
function hasOpenForm() {
  return !!panel.value?.querySelector('form input, form select, form textarea')
}

function requestClose() {
  if (isSaving()) return
  if (dirty.value && hasOpenForm()) confirming.value = true
  else emit('close')
}

function onClick(event: MouseEvent) {
  if ((event.target as HTMLElement).closest('[data-modal-cancel]')) requestClose()
}

/** Holds Tab inside the dialog and lets Esc close it from anywhere. */
function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.stopPropagation()
    if (confirming.value) confirming.value = false
    else requestClose()
    return
  }
  if (event.key !== 'Tab' || !panel.value) return
  const items = [...panel.value.querySelectorAll<HTMLElement>(focusable)].filter((el) => el.offsetParent !== null)
  if (items.length === 0) return
  const first = items[0]
  const last = items[items.length - 1]
  const active = document.activeElement
  if (event.shiftKey && (active === first || !panel.value.contains(active))) {
    event.preventDefault()
    last.focus()
  } else if (!event.shiftKey && (active === last || !panel.value.contains(active))) {
    event.preventDefault()
    first.focus()
  }
}

let opener: HTMLElement | null = null
let priorOverflow = ''

onMounted(async () => {
  opener = document.activeElement as HTMLElement | null
  priorOverflow = document.body.style.overflow
  document.body.style.overflow = 'hidden'
  document.addEventListener('keydown', onKeydown)
  await nextTick()
  const body = panel.value?.querySelector<HTMLElement>('.xr-modal__body')
  const first = body?.querySelector<HTMLElement>('input:not(:disabled), select:not(:disabled), textarea:not(:disabled)')
  ;(first ?? body?.querySelector<HTMLElement>(focusable) ?? panel.value)?.focus()
})

onBeforeUnmount(() => {
  document.body.style.overflow = priorOverflow
  document.removeEventListener('keydown', onKeydown)
  opener?.focus?.()
})
</script>

<template>
  <Teleport to="body">
    <div class="xr-modal" role="dialog" aria-modal="true" :aria-label="title">
      <div class="xr-modal__backdrop" @click="requestClose"></div>
      <div ref="panel" class="xr-modal__panel" :class="size && `xr-modal__panel--${size}`" tabindex="-1" @input="dirty = true" @change="dirty = true" @click="onClick">
        <div class="xr-modal__header">
          <h2 class="xr-modal__title">{{ title }}</h2>
          <button type="button" class="btn-close m-0" aria-label="بستن" @click="requestClose"></button>
        </div>
        <div class="xr-modal__body"><slot /></div>
        <div v-if="confirming" class="xr-modal__confirm" role="alert">
          <span>تغییرات ذخیره نشده‌اند. بدون ذخیره بسته شود؟</span>
          <div class="d-flex gap-2">
            <button type="button" class="btn btn-soft" @click="confirming = false">ادامه ویرایش</button>
            <button type="button" class="btn btn-danger" @click="$emit('close')">بستن بدون ذخیره</button>
          </div>
        </div>
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
  outline: 0;
}

.xr-modal__panel--lg {
  max-width: 800px;
}

.xr-modal__panel--xl {
  max-width: 1140px;
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

.xr-modal__confirm {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 16px;
  border-top: 1px solid var(--xr-border-table);
  border-radius: 0 0 8px 8px;
  background: var(--xr-red-light);
  color: var(--xr-text);
}
</style>
