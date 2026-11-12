<script setup lang="ts">
// Placeholder rows while a list loads, so the page keeps its shape instead of jumping when the rows arrive.
withDefaults(defineProps<{ rows?: number }>(), { rows: 5 })
</script>

<template>
  <div class="ui-list-skeleton" role="status" aria-live="polite">
    <span class="visually-hidden">در حال بارگذاری...</span>
    <div v-for="n in rows" :key="n" class="ui-list-skeleton__row" aria-hidden="true">
      <span class="xr-skeleton ui-list-skeleton__a"></span>
      <span class="xr-skeleton ui-list-skeleton__b"></span>
      <span class="xr-skeleton ui-list-skeleton__c"></span>
    </div>
  </div>
</template>

<style scoped>
.ui-list-skeleton {
  display: grid;
  gap: 14px;
  padding: 16px 4px;
}

.ui-list-skeleton__row {
  display: flex;
  gap: 16px;
  align-items: center;
}

.ui-list-skeleton__a {
  width: 22%;
}

.ui-list-skeleton__b {
  width: 38%;
}

.ui-list-skeleton__c {
  width: 16%;
}

@media (prefers-reduced-motion: no-preference) {
  .ui-list-skeleton .xr-skeleton {
    animation: ui-list-skeleton-pulse 1.2s ease-in-out infinite;
  }
}

@keyframes ui-list-skeleton-pulse {
  50% {
    opacity: 0.55;
  }
}
</style>
