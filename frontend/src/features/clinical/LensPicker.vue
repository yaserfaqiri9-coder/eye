<script setup lang="ts">
import { lensOptions } from './examRecord'

const model = defineModel<string[]>({ required: true })
</script>

<template>
  <fieldset class="lens">
    <legend class="form-label mb-1">نوع عدسی</legend>
    <label v-for="option in lensOptions" :key="option" class="form-check-label lens__option">
      <input v-model="model" class="form-check-input" type="checkbox" :value="option" />
      <span dir="ltr">{{ option }}</span>
    </label>
  </fieldset>
</template>

<style scoped>
.lens {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
}

.lens legend {
  float: none;
  width: 100%;
  font-size: var(--xr-fs-body);
}

.lens__option {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 112px;
}

.lens__option .form-check-input {
  margin: 0;
}
</style>
