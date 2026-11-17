<script setup lang="ts">
import { computed } from 'vue'
import type { ReportGroup } from './reportCatalog'
import ReportArtDefs from './ReportArtDefs.vue'
import ReportAvatar from './ReportAvatar.vue'

const props = defineProps<{ groups: ReportGroup[]; group: string | undefined }>()
const emit = defineEmits<{ 'update:group': [key: string]; open: [key: string] }>()

const current = computed(() => props.groups.find((g) => g.key === props.group) ?? props.groups[0])

/** Arrow keys move between the tabs, as a tab list should. */
function step(by: number) {
  const index = props.groups.indexOf(current.value)
  const next = props.groups[(index + by + props.groups.length) % props.groups.length]
  emit('update:group', next.key)
  requestAnimationFrame(() => document.getElementById(`report-tab-${next.key}`)?.focus())
}
</script>

<template>
  <section class="rg">
    <ReportArtDefs />
    <h1 class="rg__title">گزارش‌ها</h1>

    <div class="rg__tabs" role="tablist" aria-label="دسته گزارش‌ها" @keydown.left.prevent="step(1)" @keydown.right.prevent="step(-1)">
      <button
        v-for="g in groups"
        :id="`report-tab-${g.key}`"
        :key="g.key"
        type="button"
        role="tab"
        class="rg__tab"
        :class="{ 'rg__tab--active': g.key === current.key }"
        :aria-selected="g.key === current.key"
        :aria-controls="`report-panel-${g.key}`"
        :tabindex="g.key === current.key ? 0 : -1"
        @click="emit('update:group', g.key)"
      >{{ g.title }}</button>
    </div>

    <div :id="`report-panel-${current.key}`" class="rg__grid" role="tabpanel" :aria-labelledby="`report-tab-${current.key}`">
      <button v-for="report in current.reports" :key="report.key" type="button" class="rg__item" :title="report.hint" @click="emit('open', report.key)">
        <span class="rg__avatar"><ReportAvatar :name="report.key" /></span>
        <span class="rg__name">{{ report.name }}</span>
      </button>
    </div>
  </section>
</template>

<style scoped>
/* One white sheet: the title, a row of underlined tabs on a hairline, then the reports as round pictures six to a row. */
.rg {
  --rg-ink: #1f2a44;
  --rg-tab: #2b3445;
  --rg-accent: #1a6fdb;
  --rg-line: #e3e8ef;

  position: relative;
  min-height: 360px;
  padding: 34px 40px 44px;
  border-radius: 10px;
  background: var(--xr-surface);
  box-shadow: 0 1px 3px rgba(31, 42, 68, 0.06), 0 6px 24px rgba(31, 42, 68, 0.05);
}

.rg__title {
  margin: 0 0 40px;
  color: var(--rg-ink);
  font-size: 22px;
  font-weight: 700;
  line-height: 1.5;
}

.rg__tabs {
  display: flex;
  margin-bottom: 36px;
  overflow-x: auto;
  border-bottom: 1px solid var(--rg-line);
  scrollbar-width: none;
}

.rg__tabs::-webkit-scrollbar {
  display: none;
}

.rg__tab {
  position: relative;
  flex: none;
  padding: 0 28px 13px;
  border: 0;
  background: none;
  color: var(--rg-tab);
  font-size: 14.5px;
  font-weight: 600;
  line-height: 1.6;
  white-space: nowrap;
  transition: color 0.15s ease;
}

.rg__tab:hover {
  color: var(--rg-accent);
}

.rg__tab--active {
  color: var(--rg-accent);
}

.rg__tab--active::after {
  content: '';
  position: absolute;
  inset-inline: 28px;
  bottom: -1px;
  height: 3px;
  border-radius: 3px 3px 0 0;
  background: var(--rg-accent);
}

.rg__tab:focus-visible,
.rg__item:focus-visible {
  outline: 2px solid var(--rg-accent);
  outline-offset: 2px;
  border-radius: 6px;
}

/* Fixed columns, centred as a block; a short last row starts from the right like the rows above it. */
.rg__grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, 150px);
  justify-content: center;
  row-gap: 40px;
  max-width: 900px;
  margin-inline: auto;
}

.rg__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 14px;
  min-width: 0;
  padding: 0;
  border: 0;
  background: none;
  cursor: pointer;
}

.rg__avatar {
  display: block;
  width: 110px;
  height: 110px;
  transition: transform 0.2s ease, filter 0.2s ease;
}

.rg__item:hover .rg__avatar {
  transform: translateY(-3px);
  filter: drop-shadow(0 6px 10px rgba(29, 92, 198, 0.14));
}

.rg__name {
  max-width: 140px;
  color: var(--rg-ink);
  font-size: 13px;
  font-weight: 700;
  line-height: 1.6;
  white-space: normal;
  overflow-wrap: anywhere;
  transition: color 0.15s ease;
}

.rg__item:hover .rg__name {
  color: var(--rg-accent);
}

@media (max-width: 767.98px) {
  .rg {
    padding: 24px 16px 32px;
  }

  .rg__title {
    margin-bottom: 24px;
  }

  .rg__tab {
    padding-inline: 16px;
  }

  .rg__tab--active::after {
    inset-inline: 16px;
  }

  .rg__grid {
    grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
    row-gap: 28px;
  }

  .rg__avatar {
    width: 96px;
    height: 96px;
  }
}
</style>
