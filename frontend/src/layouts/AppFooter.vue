<script setup lang="ts">
import { todayShamsi } from '@/utils/date'
import { quantity } from '@/utils/money'

const year = String(todayShamsi().year).replace(/\d/g, (digit) => quantity(Number(digit)))
</script>

<template>
  <footer class="footer">
    <span>سیستم مدیریت شفاخانه</span>
    <span>{{ year }} <span class="footer__brand">الشفا</span> تمام حقوق محفوظ است.</span>
  </footer>
</template>

<style scoped>
.footer {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px 24px;
  min-height: 55px;
  margin: var(--xr-gap-y) 3px 20px;
  padding: 15px 27px;
  border-radius: var(--xr-radius-card);
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-footer);
}

.footer__brand {
  color: var(--xr-primary);
}

@media (max-width: 991.98px) {
  .footer {
    justify-content: center;
    margin-inline: 0;
    padding-inline: 16px;
    text-align: center;
  }
}
</style>
