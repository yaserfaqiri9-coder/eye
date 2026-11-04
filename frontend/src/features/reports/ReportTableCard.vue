<script setup lang="ts">
import UiCard from '@/components/UiCard.vue'
import { cellText, isDeduction, isNumericKind } from './reportFormat'
import type { ReportTable, ReportValueKind } from './types'

defineProps<{ table: ReportTable }>()

const cellClass = (kind: ReportValueKind) => ({
  'report-cell--number': isNumericKind(kind),
  'report-cell--date': kind === 'date' || kind === 'datetime',
})
</script>

<template>
  <UiCard :title="table.title">
    <p v-if="table.rows.length === 0" class="report-table__empty">برای این دوره چیزی ثبت نشده است.</p>
    <div v-else class="xr-table-wrap report-table__wrap" tabindex="0" role="region" :aria-label="table.title">
      <table class="xr-table">
        <thead>
          <tr>
            <th v-for="column in table.columns" :key="column.label" scope="col">{{ column.label }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, r) in table.rows" :key="r">
            <td v-for="(cell, c) in row" :key="c" :class="[cellClass(table.columns[c].kind), { 'report-cell--deduction': isDeduction(cell, table.columns[c].kind) }]">
              <!-- Free text may mix Dari and Latin. A number must not be isolated: its minus mark would turn the cell left to right and break the column's alignment. -->
              <bdi v-if="table.columns[c].kind === 'text'" class="report-cell__text">{{ cellText(cell, 'text') }}</bdi>
              <template v-else>{{ cellText(cell, table.columns[c].kind) }}</template>
            </td>
          </tr>
        </tbody>
        <tfoot v-if="table.total">
          <tr>
            <td v-for="(cell, c) in table.total" :key="c" :class="[cellClass(table.columns[c].kind), { 'report-cell--deduction': isDeduction(cell, table.columns[c].kind) }]">{{ cellText(cell, table.columns[c].kind) }}</td>
          </tr>
        </tfoot>
      </table>
    </div>
    <p v-if="table.note" class="report-table__note">{{ table.note }}</p>
  </UiCard>
</template>

<style scoped>
.report-table__empty {
  margin: 0;
  padding: 12px 0;
  color: var(--xr-text-soft);
  text-align: center;
}

/* A long list scrolls inside its card with its titles and total still in view. */
.report-table__wrap {
  max-height: min(70vh, 640px);
  overflow-y: auto;
}

.report-table__wrap:focus-visible {
  border-radius: var(--xr-radius-button);
  outline: 2px solid var(--xr-primary);
  outline-offset: 2px;
}

.report-table__wrap thead th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: var(--xr-surface);
}

.report-table__wrap tfoot td {
  position: sticky;
  bottom: 0;
  border-top: 2px solid var(--xr-border-table);
  background: var(--xr-surface);
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

/* Short text stays on one line and the table scrolls instead; only a long sentence wraps. */
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
  margin: 12px 0 0;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  line-height: 1.7;
}
</style>
