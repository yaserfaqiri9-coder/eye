<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useFloating } from '@/components/useFloating'

/**
 * One search box that adds drugs to the prescription, like the box above Rx on the old paper form:
 * type any part of a name, pick it, and it becomes the next line. A name missing from the pharmacy can still be added.
 */
defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ pick: [name: string] }>()

const uid = useId()
const input = ref<HTMLInputElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const text = ref('')
const open = ref(false)
const options = ref<string[]>([])
const active = ref(-1)
const searching = ref(false)
const failed = ref('')

const { style, place } = useFloating(input, panel, open, { matchWidth: true })

let timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | null = null
let pendingEnter = false

async function runSearch(term: string) {
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  try {
    const rows = await api<string[]>(`/prescriptions/drugs?${new URLSearchParams({ q: term })}`, { signal })
    if (signal.aborted) return
    options.value = rows
    active.value = rows.length ? 0 : -1
    failed.value = ''
  } catch (cause) {
    if (signal.aborted) return
    options.value = []
    active.value = -1
    failed.value = errorMessage(cause)
  }
  searching.value = false
  // Enter pressed before the answer came: it takes the best match, as it would have a moment later.
  if (pendingEnter) {
    pendingEnter = false
    pick(options.value[0] ?? text.value)
    return
  }
  await nextTick()
  place()
}

function onInput() {
  pendingEnter = false
  const term = text.value.trim()
  clearTimeout(timer)
  if (!term) {
    close()
    return
  }
  options.value = []
  active.value = -1
  searching.value = true
  open.value = true
  timer = setTimeout(() => runSearch(term), 180)
}

function close() {
  pendingEnter = false
  open.value = false
  active.value = -1
  searching.value = false
  clearTimeout(timer)
  controller?.abort()
}

function pick(name: string) {
  const value = name.trim()
  if (!value) return
  emit('pick', value)
  text.value = ''
  options.value = []
  close()
}

/** The typed text itself is the last entry, so a drug outside the pharmacy list can still be written. */
const count = () => options.value.length + 1

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    if (!open.value) return
    event.preventDefault()
    active.value = (active.value + (event.key === 'ArrowDown' ? 1 : -1) + count()) % count()
    nextTick(() => panel.value?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }))
  } else if (event.key === 'Enter') {
    // Enter never submits the chart form from here.
    event.preventDefault()
    if (!text.value.trim()) return
    if (searching.value) pendingEnter = true
    else pick(options.value[active.value] ?? text.value)
  } else if (event.key === 'Escape' && open.value) {
    event.stopPropagation()
    close()
  }
}

onBeforeUnmount(() => {
  clearTimeout(timer)
  controller?.abort()
})
</script>

<template>
  <div class="drug-picker">
    <div class="drug-picker__field">
      <i class="ri-search-line" aria-hidden="true"></i>
      <input
        :id="uid"
        ref="input"
        v-model="text"
        class="form-control"
        type="text"
        dir="auto"
        role="combobox"
        autocomplete="off"
        aria-label="افزودن دوا"
        aria-autocomplete="list"
        :aria-expanded="open"
        :aria-controls="`${uid}-list`"
        :aria-activedescendant="open && active >= 0 ? `${uid}-opt-${active}` : undefined"
        :disabled="disabled"
        placeholder="افزودن دوا: هر بخشی از نام را بنویسید و Enter بزنید"
        @input="onInput"
        @keydown="onKeydown"
        @blur="close"
      />
    </div>
    <Teleport to="body">
      <div v-if="open" ref="panel" class="xr-popover xr-combo__panel" :style="style" dir="rtl" @mousedown.prevent>
        <ul :id="`${uid}-list`" class="xr-combo__list" role="listbox" aria-label="افزودن دوا">
          <li
            v-for="(option, index) in options"
            :id="`${uid}-opt-${index}`"
            :key="option"
            role="option"
            class="xr-combo__option"
            :class="{ 'xr-combo__option--active': index === active }"
            :aria-selected="index === active"
            :data-active="index === active"
            @mousemove="active = index"
            @click="pick(option)"
          >
            <span dir="auto">{{ option }}</span>
          </li>
          <li
            v-if="!searching && text.trim()"
            :id="`${uid}-opt-${options.length}`"
            role="option"
            class="xr-combo__option drug-picker__free"
            :class="{ 'xr-combo__option--active': active === options.length }"
            :aria-selected="active === options.length"
            :data-active="active === options.length"
            @mousemove="active = options.length"
            @click="pick(text)"
          >
            <span>افزودن «<bdi>{{ text.trim() }}</bdi>» همان‌طور که نوشته شده</span>
          </li>
        </ul>
        <p v-if="searching" class="xr-combo__note" role="status">در حال جستجو...</p>
        <p v-else-if="failed" class="xr-combo__note xr-combo__note--error" role="alert">{{ failed }}</p>
        <p v-else-if="options.length === 0" class="xr-combo__note" role="status">در فهرست دواخانه پیدا نشد.</p>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.drug-picker {
  display: grid;
  gap: 8px;
}

.drug-picker__field {
  position: relative;
}

.drug-picker__field i {
  position: absolute;
  inset-inline-start: 12px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--xr-text-muted);
  pointer-events: none;
}

.drug-picker__field input {
  padding-inline-start: 36px;
}

.drug-picker__free {
  color: var(--xr-text-muted);
}
</style>
