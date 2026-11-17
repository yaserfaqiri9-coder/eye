<script setup lang="ts">
/**
 * The one bar every page uses above its list or report: the page or list name, a search box, the page's own
 * filters, how many rows match, and the page's actions — all on one line that wraps on a narrow screen.
 * Nothing else stands above or under it, so a page never opens with two bars.
 */
withDefaults(
  defineProps<{
    /** The name at the start of the bar. A page's main bar uses level 1; a bar inside a card uses 2. */
    title?: string
    titleLevel?: 1 | 2
    /** A short line under the name, such as the days a report covers. */
    subtitle?: string
    /** Shows a search box bound to v-model:search. */
    searchLabel?: string
    searchPlaceholder?: string
    /** How many rows match, such as «۱۲ فاکتور». */
    count?: string
    /** Inside a card: no card of its own, only a hairline under it. */
    flat?: boolean
    /** Stays in view under the page header while the page scrolls. */
    sticky?: boolean
  }>(),
  { titleLevel: 1 },
)

const search = defineModel<string>('search')
</script>

<template>
  <section class="fbar" :class="{ 'fbar--flat': flat, 'fbar--sticky': sticky }" :aria-label="title ? `فیلترهای ${title}` : 'فیلترها'">
    <div v-if="title" class="fbar__head" :title="subtitle">
      <component :is="titleLevel === 1 ? 'h1' : 'h2'" class="fbar__title">{{ title }}</component>
      <p v-if="subtitle" class="fbar__subtitle">{{ subtitle }}</p>
    </div>

    <label v-if="searchLabel" class="fbar__search">
      <i class="ri-search-line" aria-hidden="true"></i>
      <input v-model="search" type="search" class="fbar__input" :placeholder="searchPlaceholder" :aria-label="searchLabel" />
    </label>

    <div v-if="$slots.default" class="fbar__filters">
      <slot />
    </div>

    <div v-if="count || $slots.actions" class="fbar__end">
      <span v-if="count" class="fbar__count" aria-live="polite">{{ count }}</span>
      <slot name="actions" />
    </div>

    <div v-if="$slots.below" class="fbar__below">
      <slot name="below" />
    </div>
  </section>
</template>

<style scoped>
.fbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px 16px;
  margin-bottom: 16px;
  padding: 12px 16px;
  border-radius: 10px;
  background: var(--xr-surface);
  box-shadow: 0 1px 2px rgba(31, 42, 68, 0.05), 0 4px 16px rgba(31, 42, 68, 0.06);
}

.fbar--flat {
  margin-bottom: 12px;
  padding: 0 0 12px;
  border-bottom: 1px solid var(--xr-border);
  border-radius: 0;
  background: none;
  box-shadow: none;
}

.fbar--sticky {
  position: sticky;
  top: var(--xr-sticky-top);
  z-index: 20;
}

.fbar__head {
  flex: none;
  min-width: 0;
  max-width: 100%;
  margin-inline-end: 8px;
}

.fbar__title {
  margin: 0;
  overflow: hidden;
  color: #1f2a44;
  font-size: 18px;
  font-weight: 700;
  line-height: 1.5;
  text-overflow: ellipsis;
  white-space: nowrap;
}

h2.fbar__title {
  font-size: 15px;
}

.fbar__subtitle {
  margin: 0;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  line-height: 1.5;
}

/* Search as a quiet underline with its icon, the way a filter reads rather than a form field. */
.fbar__search {
  display: flex;
  flex: 1 1 220px;
  align-items: center;
  gap: 8px;
  max-width: 380px;
  min-height: 36px;
  padding-inline: 2px;
  border-bottom: 1px solid #d9dfe8;
  color: var(--xr-text-soft);
  transition: border-color 0.15s ease;
}

.fbar__search:focus-within {
  border-color: var(--xr-primary);
  color: var(--xr-primary);
}

.fbar__input {
  flex: 1;
  min-width: 0;
  padding: 6px 0;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--xr-text);
  font-size: 13.5px;
}

.fbar__input::placeholder {
  color: #9aa3b2;
}

.fbar__filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.fbar__filters :deep(.form-control),
.fbar__filters :deep(.form-select) {
  width: auto;
  min-height: 36px;
  padding-block: 4px;
  font-size: 13.5px;
}

.fbar__end {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-inline-start: auto;
}

.fbar__count {
  color: var(--xr-text-soft);
  font-size: 12.5px;
  white-space: nowrap;
}

.fbar__below {
  flex: 1 1 100%;
  padding-top: 10px;
  border-top: 1px solid var(--xr-border);
}

@media (max-width: 991.98px) {
  .fbar--sticky {
    position: static;
  }
}

@media (max-width: 575.98px) {
  .fbar {
    padding: 10px 12px;
  }

  .fbar--flat {
    padding: 0 0 10px;
  }

  .fbar__search {
    max-width: none;
  }

  .fbar__end {
    width: 100%;
  }
}
</style>
