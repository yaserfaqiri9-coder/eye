<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, useId, watch } from 'vue'
import { afghanMonths, faDigits, formatShamsi, isoToShamsi, parseShamsi, shamsiMonthLength, shamsiToIso, shamsiWeekColumn, todayIso, weekdayNames } from '@/utils/date'
import { useFloating } from './useFloating'

/** The value is a date-only yyyy-MM-dd string, exactly as the API stores it, or null when empty. */
const model = defineModel<string | null>()
const props = defineProps<{ label: string; required?: boolean; disabled?: boolean; min?: string; max?: string }>()

const uid = useId()
const field = ref<HTMLElement | null>(null)
const input = ref<HTMLInputElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const open = ref(false)
const text = ref(formatShamsi(model.value))
const invalid = ref(false)

const { style } = useFloating(field, panel, open)

const view = reactive({ year: 0, month: 1 })
/** The day keyboard focus sits on while the calendar is open. */
const cursor = ref(1)

const today = computed(() => todayIso())
const isAllowed = (iso: string) => (!props.min || iso >= props.min) && (!props.max || iso <= props.max)
const dayIso = (day: number) => shamsiToIso(view.year, view.month, day)

const years = computed(() => {
  const now = isoToShamsi(today.value).year
  const from = props.min ? isoToShamsi(props.min).year : Math.min(1300, view.year)
  const to = props.max ? isoToShamsi(props.max).year : Math.max(now + 10, view.year)
  const list: number[] = []
  for (let y = to; y >= from; y--) list.push(y)
  return list
})

const weeks = computed(() => {
  const length = shamsiMonthLength(view.year, view.month)
  const cells: (number | null)[] = Array(shamsiWeekColumn(view.year, view.month, 1)).fill(null)
  for (let day = 1; day <= length; day++) cells.push(day)
  while (cells.length % 7) cells.push(null)
  const rows: (number | null)[][] = []
  for (let i = 0; i < cells.length; i += 7) rows.push(cells.slice(i, i + 7))
  return rows
})

function syncValidity() {
  const message = invalid.value ? 'این تاریخ معتبر نیست.' : props.required && !model.value ? 'تاریخ را انتخاب کنید.' : ''
  input.value?.setCustomValidity(message)
}

watch(model, (value) => {
  // A value set from outside replaces whatever is in the box; what the user is typing is left alone.
  if (parseShamsi(text.value) !== (value ?? null)) text.value = formatShamsi(value)
  invalid.value = false
})
watch([model, invalid, () => props.required, input], syncValidity, { flush: 'post', immediate: true })

/** Lets a surrounding dialog notice the change, as it would for any native field. */
function announce() {
  input.value?.dispatchEvent(new Event('change', { bubbles: true }))
}

function commitText() {
  const value = text.value.trim()
  if (!value) {
    invalid.value = false
    if (model.value) {
      model.value = null
      announce()
    }
    return
  }
  const iso = parseShamsi(value)
  invalid.value = !iso || !isAllowed(iso)
  if (invalid.value || iso === model.value) return
  model.value = iso
  text.value = formatShamsi(iso)
  announce()
}

function showMonthOf(iso: string) {
  const { year, month, day } = isoToShamsi(iso)
  view.year = year
  view.month = month
  cursor.value = day
}

async function focusCursor() {
  await nextTick()
  panel.value?.querySelector<HTMLElement>('[data-cursor="true"]')?.focus()
}

function openCalendar(moveFocus: boolean) {
  if (props.disabled || input.value?.disabled) return
  if (!open.value) {
    showMonthOf(parseShamsi(text.value) ?? model.value ?? today.value)
    open.value = true
  }
  if (moveFocus) focusCursor()
}

function close(returnFocus = false) {
  open.value = false
  if (returnFocus) input.value?.focus()
}

function pick(iso: string) {
  if (!isAllowed(iso)) return
  model.value = iso
  text.value = formatShamsi(iso)
  invalid.value = false
  announce()
  close(true)
}

function clear() {
  model.value = null
  text.value = ''
  invalid.value = false
  announce()
  close(true)
}

function shiftMonth(by: number) {
  const index = view.year * 12 + (view.month - 1) + by
  view.year = Math.floor(index / 12)
  view.month = (index % 12) + 1
  cursor.value = Math.min(cursor.value, shamsiMonthLength(view.year, view.month))
}

function onViewChange() {
  cursor.value = Math.min(cursor.value, shamsiMonthLength(view.year, view.month))
}

function moveCursor(by: number) {
  const length = shamsiMonthLength(view.year, view.month)
  const next = cursor.value + by
  if (next < 1) {
    shiftMonth(-1)
    cursor.value = shamsiMonthLength(view.year, view.month) + next
  } else if (next > length) {
    const over = next - length
    shiftMonth(1)
    cursor.value = over
  } else {
    cursor.value = next
  }
  focusCursor()
}

function onFieldKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    openCalendar(true)
  } else if (event.key === 'Escape' && open.value) {
    event.stopPropagation()
    close()
  } else if (event.key === 'Enter') {
    // Enter confirms the typed date; it never submits the form behind it by surprise.
    event.preventDefault()
    commitText()
    close()
  } else if (event.key === 'Tab') {
    close()
  }
}

function onGridKeydown(event: KeyboardEvent) {
  // The grid runs right to left, so the left arrow goes to the next day.
  const moves: Record<string, () => void> = {
    ArrowLeft: () => moveCursor(1),
    ArrowRight: () => moveCursor(-1),
    ArrowDown: () => moveCursor(7),
    ArrowUp: () => moveCursor(-7),
    PageDown: () => (shiftMonth(event.shiftKey ? 12 : 1), focusCursor()),
    PageUp: () => (shiftMonth(event.shiftKey ? -12 : -1), focusCursor()),
    Home: () => moveCursor(-shamsiWeekColumn(view.year, view.month, cursor.value)),
    End: () => moveCursor(6 - shamsiWeekColumn(view.year, view.month, cursor.value)),
  }
  const move = moves[event.key]
  if (!move) return
  event.preventDefault()
  move()
}

/** Holds Tab inside the calendar and answers Esc here, so a surrounding dialog does not react as well. */
function onPanelKeydown(event: KeyboardEvent) {
  event.stopPropagation()
  if (event.key === 'Escape') {
    close(true)
    return
  }
  if (event.key !== 'Tab' || !panel.value) return
  const items = [...panel.value.querySelectorAll<HTMLElement>('select, button:not(:disabled):not([tabindex="-1"])')]
  const first = items[0]
  const last = items[items.length - 1]
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault()
    last.focus()
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault()
    first.focus()
  }
}

function onOutside(event: PointerEvent) {
  const target = event.target as Node
  if (!field.value?.contains(target) && !panel.value?.contains(target)) close()
}

watch(open, (isOpen) => {
  if (isOpen) document.addEventListener('pointerdown', onOutside, true)
  else document.removeEventListener('pointerdown', onOutside, true)
})
onBeforeUnmount(() => document.removeEventListener('pointerdown', onOutside, true))
</script>

<template>
  <div class="xr-date">
    <label class="form-label" :class="{ 'xr-required': required }" :for="uid">{{ label }}</label>
    <div ref="field" class="xr-date__field">
      <input
        :id="uid"
        ref="input"
        v-model="text"
        class="form-control xr-date__input"
        :class="{ 'is-invalid': invalid }"
        dir="ltr"
        autocomplete="off"
        inputmode="numeric"
        maxlength="10"
        placeholder="سال/ماه/روز"
        :disabled="disabled"
        :required="required"
        :aria-invalid="invalid"
        :aria-expanded="open"
        :aria-controls="`${uid}-calendar`"
        aria-haspopup="dialog"
        @click="openCalendar(false)"
        @keydown="onFieldKeydown"
        @blur="commitText"
      />
      <button type="button" class="xr-date__icon" :disabled="disabled" :aria-label="`باز کردن تقویم: ${label}`" tabindex="-1" @click="open ? close(true) : openCalendar(true)">
        <i class="ri-calendar-2-line" aria-hidden="true"></i>
      </button>
    </div>
    <p v-if="invalid" class="form-warning mb-0" role="alert">این تاریخ معتبر نیست یا در محدوده مجاز نیست.</p>

    <Teleport to="body">
      <div
        v-if="open"
        :id="`${uid}-calendar`"
        ref="panel"
        class="xr-popover xr-calendar"
        :style="style"
        role="dialog"
        :aria-label="`تقویم: ${label}`"
        dir="rtl"
        @keydown="onPanelKeydown"
      >
        <div class="xr-calendar__head">
          <button type="button" class="xr-calendar__nav" aria-label="ماه قبل" title="ماه قبل" @click="shiftMonth(-1)"><i class="ri-arrow-right-s-line" aria-hidden="true"></i></button>
          <select v-model.number="view.month" class="xr-calendar__select" aria-label="ماه" @change="onViewChange">
            <option v-for="(name, index) in afghanMonths" :key="name" :value="index + 1">{{ name }}</option>
          </select>
          <select v-model.number="view.year" class="xr-calendar__select" aria-label="سال" @change="onViewChange">
            <option v-for="year in years" :key="year" :value="year">{{ faDigits(year) }}</option>
          </select>
          <button type="button" class="xr-calendar__nav" aria-label="ماه بعد" title="ماه بعد" @click="shiftMonth(1)"><i class="ri-arrow-left-s-line" aria-hidden="true"></i></button>
        </div>

        <table class="xr-calendar__grid" role="grid" :aria-label="`${afghanMonths[view.month - 1]} ${faDigits(view.year)}`" @keydown="onGridKeydown">
          <thead>
            <tr>
              <th v-for="name in weekdayNames" :key="name" scope="col" :abbr="name">{{ name[0] }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(week, w) in weeks" :key="w">
              <td v-for="(day, d) in week" :key="d">
                <button
                  v-if="day"
                  type="button"
                  class="xr-calendar__day"
                  :class="{ 'xr-calendar__day--today': dayIso(day) === today, 'xr-calendar__day--selected': dayIso(day) === model }"
                  :disabled="!isAllowed(dayIso(day))"
                  :tabindex="day === cursor ? 0 : -1"
                  :data-cursor="day === cursor"
                  :aria-label="`${faDigits(day)} ${afghanMonths[view.month - 1]} ${faDigits(view.year)}`"
                  :aria-pressed="dayIso(day) === model"
                  :aria-current="dayIso(day) === today ? 'date' : undefined"
                  @click="pick(dayIso(day))"
                >{{ faDigits(day) }}</button>
              </td>
            </tr>
          </tbody>
        </table>

        <div class="xr-calendar__foot">
          <button type="button" class="btn btn-soft btn-sm" :disabled="!isAllowed(today)" @click="pick(today)">امروز</button>
          <button v-if="!required" type="button" class="btn btn-sm xr-calendar__clear" :disabled="!model && !text" @click="clear">پاک کردن</button>
        </div>
      </div>
    </Teleport>
  </div>
</template>
