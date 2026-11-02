<template>
  <div class="missing">
    <div class="missing__code">404</div>
    <h2>این صفحه پیدا نشد.</h2>
    <p>صفحه درخواست‌شده وجود ندارد.</p>
    <RouterLink to="/" class="btn btn-primary"><i class="ri-home-4-line" aria-hidden="true"></i> بازگشت به صفحه اصلی</RouterLink>
  </div>
</template>

<style scoped>
.missing {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 560px;
  text-align: center;
}

.missing__code {
  color: var(--xr-primary);
  font-size: 260px;
  font-weight: 700;
  line-height: 1;
  direction: ltr;
}

.missing p {
  margin: 8px 0 16px;
}

@media (max-width: 575.98px) {
  .missing__code {
    font-size: 120px;
  }
}
</style>
