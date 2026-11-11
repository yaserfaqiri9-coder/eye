<template>
  <footer class="footer">
    <span>
      توسعه داده شده توسط
      <a
        class="footer__brand"
        href="https://novatechsoft.com"
        target="_blank"
        rel="noopener noreferrer"
      >شرکت خدمات تکنالوژی نواتیک</a>
    </span>
  </footer>
</template>

<style scoped>
.footer {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
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
  font-weight: 700;
  text-decoration: none;
}

.footer__brand:hover,
.footer__brand:focus-visible {
  text-decoration: underline;
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
