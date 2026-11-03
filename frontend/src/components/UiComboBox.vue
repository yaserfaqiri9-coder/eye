<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import { errorMessage } from '@/api/http'
import { useFloating } from './useFloating'

export interface ComboOption {
  /** The record's id: this, never the display text, is what gets stored. */
  value: number
  label: string
  /** A second, quieter line such as stock or a file number. */
  hint?: string
  disabled?: boolean
}

const model = defineModel<number | null>()
const props = withDefaults(
  defineProps<{
    label: string
    /** A short list held in memory and filtered here. */
    options?: ComboOption[]
    /** A long list searched on the server. Stale answers are dropped, so a slow reply cannot replace a newer one. */
    search?: (query: string, signal: AbortSignal) => Promise<ComboOption[]>
    /** Shows the current value on an edit form before any list has been loaded. */
    selectedLabel?: string | null
    /** Shows the choice as a soft tag inside the field. Plain text suits simple fields better. */
    chip?: boolean
    required?: boolean
    disabled?: boolean
    placeholder?: string
    emptyText?: string
    hideLabel?: boolean
    /** Set while a parent is still fetching `options`, so "nothing found" is not shown too early. */
    loading?: boolean
    error?: string
  }>(),
  { options: undefined, search: undefined, selectedLabel: undefined, placeholder: 'جستجو یا انتخاب کنید', emptyText: 'موردی پیدا نشد.', loading: false, error: '' },
)
const emit = defineEmits<{ select: [option: ComboOption | null] }>()

const SEARCH_DELAY = 250
const RENDER_LIMIT = 100

const uid = useId()
const field = ref<HTMLElement | null>(null)
const input = ref<HTMLInputElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const open = ref(false)
const query = ref('')
const typing = ref(false)
const active = ref(-1)
const remote = ref<ComboOption[]>([])
const searching = ref(false)
const searchFailed = ref('')
/** The chosen option itself is kept, so a failed search or a filtered list can never lose the value. */
const picked = ref<ComboOption | null>(null)

const { style, place } = useFloating(field, panel, open, { matchWidth: true })

function normalize(text: string) {
  return text.toLowerCase().replace(/ي/g, 'ی').replace(/ك/g, 'ک').replace(/\s+/g, ' ').trim()
}

const matches = computed(() => {
  if (props.search) return remote.value
  const term = normalize(typing.value ? query.value : '')
  const list = props.options ?? []
  return term ? list.filter((o) => normalize(`${o.label} ${o.hint ?? ''}`).includes(term)) : list
})
const visible = computed(() => matches.value.slice(0, RENDER_LIMIT))

const selectedText = computed(() => {
  if (model.value === null || model.value === undefined) return ''
  if (picked.value?.value === model.value) return picked.value.label
  return props.options?.find((o) => o.value === model.value)?.label ?? props.selectedLabel ?? ''
})

const showChip = computed(() => props.chip && !!selectedText.value)
const inputValue = computed(() => (typing.value ? query.value : showChip.value ? '' : selectedText.value))
const canClear = computed(() => !props.required && !props.disabled && model.value !== null && model.value !== undefined)
const isLoading = computed(() => searching.value || props.loading)

function syncValidity() {
  input.value?.setCustomValidity(props.required && (model.value === null || model.value === undefined) ? 'یکی از گزینه‌های فهرست را انتخاب کنید.' : '')
}
watch([model, () => props.required, input], syncValidity, { flush: 'post', immediate: true })

let timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | null = null

async function runSearch(term: string) {
  if (!props.search) return
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  searching.value = true
  searchFailed.value = ''
  try {
    const rows = await props.search(term, signal)
    if (signal.aborted) return
    remote.value = rows
    active.value = rows.findIndex((o) => !o.disabled)
  } catch (cause) {
    if (signal.aborted) return
    remote.value = []
    searchFailed.value = errorMessage(cause)
  }
  searching.value = false
  await nextTick()
  place()
}

function openList() {
  if (props.disabled || open.value) return
  open.value = true
  active.value = visible.value.findIndex((o) => o.value === model.value)
  if (props.search) runSearch(typing.value ? query.value.trim() : '')
}

function closeList() {
  open.value = false
  typing.value = false
  query.value = ''
  clearTimeout(timer)
  controller?.abort()
  searching.value = false
}

function onInput(event: Event) {
  query.value = (event.target as HTMLInputElement).value
  typing.value = true
  active.value = 0
  if (!open.value) openList()
  nextTick(place)
  if (!props.search) return
  clearTimeout(timer)
  timer = setTimeout(() => runSearch(query.value.trim()), SEARCH_DELAY)
}

/** Lets a surrounding dialog notice the change, as it would for any native field. */
function announce() {
  input.value?.dispatchEvent(new Event('change', { bubbles: true }))
}

function select(option: ComboOption) {
  if (option.disabled) return
  picked.value = option
  model.value = option.value
  emit('select', option)
  closeList()
  announce()
}

function clear() {
  picked.value = null
  model.value = null
  emit('select', null)
  closeList()
  announce()
  input.value?.focus()
}

function moveActive(by: number) {
  const count = visible.value.length
  if (count === 0) return
  let next = active.value
  for (let i = 0; i < count; i++) {
    next = (next + by + count) % count
    if (!visible.value[next].disabled) break
  }
  active.value = next
  nextTick(() => panel.value?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }))
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) openList()
    else moveActive(event.key === 'ArrowDown' ? 1 : -1)
  } else if (event.key === 'Enter') {
    if (!open.value) return
    // Inside an open list Enter chooses; it must not submit the form behind it.
    event.preventDefault()
    const option = visible.value[active.value]
    if (option) select(option)
  } else if (event.key === 'Escape' && open.value) {
    event.stopPropagation()
    closeList()
  } else if (event.key === 'Tab') {
    closeList()
  } else if (event.key === 'Backspace' && !inputValue.value && canClear.value) {
    clear()
  }
}

function onOutside(event: PointerEvent) {
  const target = event.target as Node
  if (!field.value?.contains(target) && !panel.value?.contains(target)) closeList()
}

watch(open, (isOpen) => {
  if (isOpen) document.addEventListener('pointerdown', onOutside, true)
  else document.removeEventListener('pointerdown', onOutside, true)
})

onBeforeUnmount(() => {
  clearTimeout(timer)
  controller?.abort()
  document.removeEventListener('pointerdown', onOutside, true)
})
</script>

<template>
  <div class="xr-combo">
    <label class="form-label" :class="{ 'xr-required': required, 'visually-hidden': hideLabel }" :for="uid">{{ label }}</label>
    <div ref="field" class="xr-combo__field" :class="{ 'xr-combo__field--open': open, 'xr-combo__field--disabled': disabled }" @click="input?.focus(); openList()">
      <span v-if="showChip" class="xr-chip">
        <span class="xr-chip__text">{{ selectedText }}</span>
      </span>
      <input
        :id="uid"
        ref="input"
        class="xr-combo__input"
        type="text"
        role="combobox"
        autocomplete="off"
        aria-autocomplete="list"
        :value="inputValue"
        :placeholder="showChip ? '' : placeholder"
        :disabled="disabled"
        :aria-required="required"
        :aria-expanded="open"
        :aria-controls="`${uid}-list`"
        :aria-activedescendant="open && active >= 0 ? `${uid}-opt-${active}` : undefined"
        @input="onInput"
        @keydown="onKeydown"
      />
      <button v-if="canClear" type="button" class="xr-combo__clear" :aria-label="`پاک کردن ${label}`" title="پاک کردن" tabindex="-1" @click.stop="clear"><i class="ri-close-line" aria-hidden="true"></i></button>
      <i class="ri-arrow-down-s-line xr-combo__caret" aria-hidden="true"></i>
    </div>
    <p v-if="error" class="form-warning mb-0" role="alert">{{ error }}</p>

    <Teleport to="body">
      <div v-if="open" ref="panel" class="xr-popover xr-combo__panel" :style="style" dir="rtl" @mousedown.prevent>
        <ul :id="`${uid}-list`" class="xr-combo__list" role="listbox" :aria-label="label">
          <li
            v-for="(option, index) in visible"
            :id="`${uid}-opt-${index}`"
            :key="option.value"
            role="option"
            class="xr-combo__option"
            :class="{ 'xr-combo__option--active': index === active, 'xr-combo__option--selected': option.value === model }"
            :aria-selected="option.value === model"
            :aria-disabled="option.disabled || undefined"
            :data-active="index === active"
            @mousemove="active = index"
            @click="select(option)"
          >
            <span>{{ option.label }}</span>
            <small v-if="option.hint" class="xr-combo__hint">{{ option.hint }}</small>
          </li>
        </ul>
        <p v-if="searchFailed" class="xr-combo__note xr-combo__note--error" role="alert">{{ searchFailed }} انتخاب قبلی حفظ شده است.</p>
        <p v-else-if="isLoading && visible.length === 0" class="xr-combo__note" role="status">در حال جستجو...</p>
        <p v-else-if="visible.length === 0" class="xr-combo__note" role="status">{{ emptyText }}</p>
        <p v-else-if="matches.length > RENDER_LIMIT" class="xr-combo__note">{{ matches.length - RENDER_LIMIT }} مورد دیگر؛ برای پیدا کردن، تایپ کنید.</p>
      </div>
    </Teleport>
  </div>
</template>
