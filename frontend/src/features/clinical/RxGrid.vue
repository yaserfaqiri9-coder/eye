<script setup lang="ts">
import { computed } from 'vue'
import { axisWarning, dioptreWarning, formatDioptre, normalizeDigits } from './examRecord'

export interface RxRow {
  sphere: string
  cylinder: string
  axis: string
  add: string
  va: string
}

const props = defineProps<{
  od: RxRow
  os: RxRow
  /** Names the grid in each field's accessible label, so device and final values cannot be mistaken. */
  label: string
  showAdd?: boolean
  showVa?: boolean
  readonly?: boolean
}>()

// No sample values in empty fields: a greyed 6/6 or +0.00 could be read as a recorded result.
const columns = computed(() => [
  { key: 'sphere' as const, title: 'SPH' },
  { key: 'cylinder' as const, title: 'CYL' },
  { key: 'axis' as const, title: 'AXIS' },
  ...(props.showAdd ? [{ key: 'add' as const, title: 'ADD' }] : []),
  ...(props.showVa ? [{ key: 'va' as const, title: 'VA' }] : []),
])

const eyes = computed(() => [
  { code: 'OD', title: 'چشم راست', row: props.od, tone: 'xr-eye--od' },
  { code: 'OS', title: 'چشم چپ', row: props.os, tone: 'xr-eye--os' },
])

function tidy(row: RxRow, key: keyof RxRow) {
  row[key] = key === 'axis' || key === 'va' ? normalizeDigits(row[key]) : formatDioptre(row[key])
}

function warningOf(row: RxRow) {
  return dioptreWarning(row.sphere) || dioptreWarning(row.cylinder) || axisWarning(row.axis)
}
</script>

<template>
  <div class="xr-table-wrap">
    <table class="xr-clinical-table rx-grid">
      <thead>
        <tr>
          <th scope="col" class="rx-grid__eye">چشم</th>
          <th v-for="column in columns" :key="column.key" scope="col" class="ltr-num">{{ column.title }}</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="eye in eyes" :key="eye.code">
          <tr>
            <th scope="row" class="rx-grid__eye">
              <span class="xr-eye" :class="eye.tone">{{ eye.code }}</span>
              <span class="rx-grid__eye-name">{{ eye.title }}</span>
            </th>
            <td v-for="column in columns" :key="column.key">
              <input
                v-model="eye.row[column.key]"
                class="form-control text-center"
                dir="ltr"
                inputmode="text"
                autocomplete="off"
                :maxlength="column.key === 'va' ? 30 : 10"
                :readonly="readonly"
                :aria-label="`${label}: ${column.title} ${eye.code}`"
                @blur="tidy(eye.row, column.key)"
              />
            </td>
          </tr>
          <tr v-if="warningOf(eye.row)">
            <td :colspan="columns.length + 1" class="form-warning" role="alert"><span class="ltr-num">{{ eye.code }}</span>: {{ warningOf(eye.row) }}</td>
          </tr>
        </template>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.rx-grid__eye {
  width: 130px;
  font-weight: 400;
  white-space: nowrap;
}

.rx-grid__eye-name {
  margin-inline-start: 8px;
}

@media (max-width: 575.98px) {
  .rx-grid__eye-name {
    display: none;
  }

  .rx-grid__eye {
    width: 50px;
  }
}
</style>
