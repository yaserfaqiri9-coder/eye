<script setup lang="ts">
// Dev-only page for comparing shared components with the XRay reference. Sample text only.
import StatCard from '@/components/StatCard.vue'
import UiCard from '@/components/UiCard.vue'

const rows = [
  { file: '1', name: 'نمونه اول', status: 'در انتظار', tone: 'orange' },
  { file: '2', name: 'نمونه دوم', status: 'نزد داکتر', tone: 'primary' },
  { file: '3', name: 'نمونه سوم', status: 'تکمیل', tone: 'green' },
]
</script>

<template>
  <div class="preview">
    <div class="preview__stats">
      <StatCard label="مراجعات امروز" value="120" icon="ri-user-3-fill" />
      <StatCard label="در انتظار" value="14" icon="ri-hourglass-fill" tone="orange" />
      <StatCard label="لغو شده" value="2" icon="ri-close-line" tone="red" />
      <StatCard label="نوبت‌ها" value="38" icon="ri-calendar-event-fill" tone="cyan" />
    </div>

    <UiCard title="جدول نمونه">
      <template #actions><button type="button" class="btn btn-soft">عملیات</button></template>
      <div class="xr-table-wrap">
        <table class="xr-table">
          <thead>
            <tr><th>شماره پرونده</th><th>نام</th><th>وضعیت</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in rows" :key="row.file">
              <td><span class="ltr-num">{{ row.file }}</span></td>
              <td>{{ row.name }}</td>
              <td><span class="xr-badge" :class="`xr-badge--${row.tone}`">{{ row.status }}</span></td>
            </tr>
          </tbody>
        </table>
      </div>
    </UiCard>

    <UiCard title="فورم نمونه">
      <div class="row g-3">
        <div class="col-md-6">
          <label class="form-label" for="preview-name">نام</label>
          <input id="preview-name" class="form-control" placeholder="نام مریض" />
        </div>
        <div class="col-md-6">
          <label class="form-label" for="preview-gender">جنسیت</label>
          <select id="preview-gender" class="form-select">
            <option>مرد</option>
            <option>زن</option>
          </select>
        </div>
      </div>
      <button type="button" class="btn btn-primary mt-3">ذخیره</button>
    </UiCard>
  </div>
</template>

<style scoped>
.preview {
  display: grid;
  gap: var(--xr-gap);
}

.preview__stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--xr-gap);
}
</style>
