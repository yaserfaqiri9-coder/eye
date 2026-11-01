<script setup lang="ts">
import { quantity } from '@/utils/money'

withDefaults(
  defineProps<{
    label: string
    value: string | number
    icon: string
    tone?: 'primary' | 'orange' | 'red' | 'cyan'
  }>(),
  { tone: 'primary' },
)
</script>

<template>
  <div class="stat" :class="`stat--${tone}`">
    <div class="stat__icon"><i class="bi" :class="icon" aria-hidden="true"></i></div>
    <div class="stat__text">
      <div class="stat__value">{{ typeof value === 'number' ? quantity(value) : value }}</div>
      <div class="stat__label">{{ label }}</div>
    </div>
  </div>
</template>

<style scoped>
.stat {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 25px 20px;
  border-radius: var(--xr-radius-card);
  box-shadow: var(--xr-shadow-card);
}

.stat__icon {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 60px;
  height: 60px;
  border-radius: 50%;
  color: #fff;
  font-size: 22px;
}

.stat__text {
  text-align: end;
}

.stat__value {
  font-size: var(--xr-fs-stat);
  font-weight: 300;
  line-height: 1.3;
}

.stat__label {
  font-size: 15px;
}

.stat--primary { background: var(--xr-primary-light); }
.stat--primary .stat__icon { background: var(--xr-primary); }
.stat--orange { background: var(--xr-orange-light); }
.stat--orange .stat__icon { background: var(--xr-orange); }
.stat--red { background: var(--xr-red-light); }
.stat--red .stat__icon { background: var(--xr-red); }
.stat--cyan { background: var(--xr-cyan-light); }
.stat--cyan .stat__icon { background: var(--xr-cyan); }
</style>
