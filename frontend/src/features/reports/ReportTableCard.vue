<script setup lang="ts">
import { computed, ref } from 'vue'
import UiCard from '@/components/UiCard.vue'
import { quantity } from '@/utils/money'
import { cellText, isDeduction, isNumericKind } from './reportFormat'
import type { ReportTable, ReportValueKind } from './types'

const props = defineProps<{ table: ReportTable }>()

/** A long list opens with its first rows; the rest are one click away, and every row is in the Excel and PDF files. */
const SHORT_ROWS = 50
const expanded = ref(false)
const foldable = computed(() => props.table.rows.length > SHORT_ROWS + 2)

const cellClass = (kind: ReportValueKind) => ({
  'report-cell--number': isNumericKind(kind),
  'report-cell--date': kind === 'date' || kind === 'datetime',
})
</script>

<template>
  <UiCard :title="table.title">
    <p v-if="table.rows.length === 0" class="report-table__empty">برای این دوره چیزی ثبت نشده است.</p>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr>
            <th scope="col" class="report-cell--index">ردیف</th>
            <th v-for="column in table.columns" :key="column.label" scope="col">{{ column.label }}</th>
          </tr>
        </thead>
        <tbody>
          <!-- Every row is in the page, so paper and search always have the whole list; the fold only hides them on screen. -->
          <tr v-for="(row, r) in table.rows" :key="r" :class="{ 'report-row--folded': foldable && !expanded && r >= SHORT_ROWS }">
            <td class="report-cell--index">{{ quantity(r + 1) }}</td>
            <td v-for="(cell, c) in row" :key="c" :class="[cellClass(table.columns[c].kind), { 'report-cell--deduction': isDeduction(cell, table.columns[c].kind) }]">
              <!-- Free text may mix Dari and Latin. A number must not be isolated: its minus mark would turn the cell left to right and break the column's alignment. -->
              <bdi v-if="table.columns[c].kind === 'text'" class="report-cell__text">{{ cellText(cell, 'text') }}</bdi>
              <template v-else>{{ cellText(cell, table.columns[c].kind) }}</template>
            </td>
          </tr>
        </tbody>
        <tfoot v-if="table.total">
          <tr>
            <td class="report-cell--index"></td>
            <td v-for="(cell, c) in table.total" :key="c" :class="[cellClass(table.columns[c].kind), { 'report-cell--deduction': isDeduction(cell, table.columns[c].kind) }]">{{ cellText(cell, table.columns[c].kind) }}</td>
          </tr>
        </tfoot>
      </table>
    </div>
    <button v-if="foldable" type="button" class="btn btn-quiet btn-sm report-table__more no-print" :aria-expanded="expanded" @click="expanded = !expanded">
      <i :class="expanded ? 'ri-arrow-up-s-line' : 'ri-arrow-down-s-line'" aria-hidden="true"></i>
      {{ expanded ? 'نمایش کمتر' : `نمایش همه ${quantity(table.rows.length)} ردیف` }}
    </button>
    <p v-if="table.note" class="report-table__note">{{ table.note }}</p>
  </UiCard>
</template>

<style scoped>
.report-table__empty {
  margin: 0;
  padding: 8px 0;
  color: var(--xr-text-soft);
  text-align: center;
}

@media screen {
  .report-row--folded {
    display: none;
  }
}

.report-table__more {
  display: flex;
  align-items: center;
  gap: 4px;
  margin: 10px auto 0;
}

tfoot td {
  border-top: 2px solid var(--xr-border-table);
  font-weight: 700;
}

/* Numbers read from the right in Dari, so aligning to the start keeps units under units. */
.report-cell--number {
  font-variant-numeric: tabular-nums;
  text-align: start;
  white-space: nowrap;
}

.report-cell--date {
  white-space: nowrap;
}

.report-cell--index {
  width: 1%;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  text-align: center;
  white-space: nowrap;
}

/* Short text stays on one line and the table scrolls sideways instead; only a long sentence wraps. */
.report-cell__text {
  display: block;
  width: max-content;
  max-width: 240px;
}

/* On a phone, text wraps so a short table fits and no amount is cut off at the edge. Only a wide table still scrolls. */
@media (max-width: 575.98px) {
  .report-cell__text {
    width: auto;
    min-width: 6em;
    overflow-wrap: anywhere;
  }
}

.report-cell--deduction {
  color: var(--xr-tone-red-text);
}

.report-table__note {
  margin: 10px 0 0;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  line-height: 1.7;
}
</style>
