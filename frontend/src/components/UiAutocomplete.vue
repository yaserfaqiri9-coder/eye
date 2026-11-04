<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId } from 'vue'
import { errorMessage } from '@/api/http'
import { useFloating } from './useFloating'

/**
 * A free-text field that suggests values as the user types. The text itself is what gets stored,
 * so a value missing from the list can still be entered. Use UiComboBox when a record must be chosen.
 */
const model = defineModel<string>({ required: true })
const props = withDefaults(
  defineProps<{
    label: string
    search: (query: string, signal: AbortSignal) => Promise<string[]>
    required?: boolean
    maxlength?: number
    placeholder?: string
    emptyText?: string
  }>(),
  { maxlength: undefined, placeholder: undefined, emptyText: 'موردی پیدا نشد؛ می‌توانید نام را کامل بنویسید.' },
)

const SEARCH_DELAY = 200

const uid = useId()
const input = ref<HTMLInputElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const open = ref(false)
// Nothing is highlighted until the user moves to it: Enter or Tab must never swap what was typed for a similar name.
const active = ref(-1)
const options = ref<string[]>([])
const searching = ref(false)
const failed = ref('')

const { style, place } = useFloating(input, panel, open, { matchWidth: true })

let timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | null = null

async function runSearch(term: string) {
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  searching.value = true
  failed.value = ''
  try {
    const rows = await props.search(term, signal)
    if (signal.aborted) return
    options.value = rows
    active.value = -1
  } catch (cause) {
    if (signal.aborted) return
    options.value = []
    failed.value = errorMessage(cause)
  }
  searching.value = false
  await nextTick()
  place()
}

function closeList() {
  open.value = false
  active.value = -1
  clearTimeout(timer)
  controller?.abort()
  searching.value = false
}

function openFor(text: string) {
  const term = text.trim()
  if (!term) {
    closeList()
    options.value = []
    return
  }
  // The previous answer stays out of sight, so it cannot be chosen for a newer query.
  options.value = []
  active.value = -1
  searching.value = true
  failed.value = ''
  open.value = true
  clearTimeout(timer)
  timer = setTimeout(() => runSearch(term), SEARCH_DELAY)
}

function onInput(event: Event) {
  // Read from the field: the model still holds the previous text until the parent re-renders.
  const text = (event.target as HTMLInputElement).value
  model.value = text
  openFor(text)
}

function select(option: string) {
  model.value = option
  closeList()
  input.value?.dispatchEvent(new Event('change', { bubbles: true }))
}

function moveActive(by: number) {
  const count = options.value.length
  if (count === 0) return
  // -1 is the typed text itself, so the user can step back to it.
  active.value = ((active.value + 1 + by + count + 1) % (count + 1)) - 1
  nextTick(() => panel.value?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }))
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) openFor(model.value)
    else moveActive(event.key === 'ArrowDown' ? 1 : -1)
  } else if (event.key === 'Enter' && open.value) {
    // Inside an open list Enter chooses or dismisses; it must not submit the form behind it.
    event.preventDefault()
    const option = options.value[active.value]
    if (option) select(option)
    else closeList()
  } else if (event.key === 'Escape' && open.value) {
    event.stopPropagation()
    closeList()
  }
}

onBeforeUnmount(() => {
  clearTimeout(timer)
  controller?.abort()
})
</script>

<template>
  <input
    :id="uid"
    ref="input"
    class="form-control"
    type="text"
    dir="auto"
    role="combobox"
    autocomplete="off"
    aria-autocomplete="list"
    :aria-label="label"
    :aria-expanded="open"
    :aria-controls="`${uid}-list`"
    :aria-activedescendant="open && active >= 0 ? `${uid}-opt-${active}` : undefined"
    :value="model"
    :required="required"
    :maxlength="maxlength"
    :placeholder="placeholder"
    @input="onInput"
    @keydown="onKeydown"
    @blur="closeList"
  />
  <Teleport to="body">
    <div v-if="open" ref="panel" class="xr-popover xr-combo__panel" :style="style" dir="rtl" @mousedown.prevent>
      <ul :id="`${uid}-list`" class="xr-combo__list" role="listbox" :aria-label="label">
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
          @click="select(option)"
        >
          <span dir="auto">{{ option }}</span>
        </li>
      </ul>
      <p v-if="failed" class="xr-combo__note xr-combo__note--error" role="alert">{{ failed }} نام را می‌توانید کامل بنویسید.</p>
      <p v-else-if="searching && options.length === 0" class="xr-combo__note" role="status">در حال جستجو...</p>
      <p v-else-if="options.length === 0" class="xr-combo__note" role="status">{{ emptyText }}</p>
    </div>
  </Teleport>
</template>
