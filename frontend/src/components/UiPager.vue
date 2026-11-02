<script setup lang="ts">
import { computed } from 'vue'
import { quantity } from '@/utils/money'

const props = defineProps<{ page: number; pageCount: number }>()
defineEmits<{ go: [page: number] }>()

/** Up to five numbers around the current page. */
const pages = computed(() => {
  const count = Math.min(5, props.pageCount)
  const first = Math.max(1, Math.min(props.page - 2, props.pageCount - count + 1))
  return Array.from({ length: count }, (_, index) => first + index)
})
</script>

<template>
  <nav v-if="pageCount > 1" class="d-flex justify-content-center mt-3" aria-label="صفحه‌ها">
    <ul class="pagination">
      <li class="page-item" :class="{ disabled: page <= 1 }">
        <button type="button" class="page-link" :disabled="page <= 1" @click="$emit('go', page - 1)">قبلی</button>
      </li>
      <li v-for="number in pages" :key="number" class="page-item" :class="{ active: number === page }">
        <button type="button" class="page-link" :aria-current="number === page ? 'page' : undefined" @click="$emit('go', number)">{{ quantity(number) }}</button>
      </li>
      <li class="page-item" :class="{ disabled: page >= pageCount }">
        <button type="button" class="page-link" :disabled="page >= pageCount" @click="$emit('go', page + 1)">بعدی</button>
      </li>
    </ul>
  </nav>
</template>
