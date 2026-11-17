<script setup lang="ts">
import { ref } from 'vue'
defineProps<{ setupLink?: boolean }>()
const showHelp = ref(false)
const locationOrigin = window.location.origin
// The installed handler accepts only its configured origin, never arbitrary URLs or commands.
const televisionUrl = `alshifa-tv://open?origin=${encodeURIComponent(window.location.origin)}`
</script>

<template>
  <a :href="televisionUrl" class="btn btn-soft text-nowrap" title="اجراکنندهٔ تلویزیون ویندوز باید یک‌بار نصب شود" @click="showHelp = true">
    <i class="ri-tv-2-line" aria-hidden="true"></i> نمایش در تلویزیون
  </a>
  <a v-if="setupLink" href="/tools/AlShifa-TV.zip" download class="btn btn-soft text-nowrap">نصب اجراکنندهٔ ویندوز</a>
  <div v-if="showHelp" class="tv-help" role="status">
    <span>برای نمایش روی تلویزیون، آن را با HDMI وصل کنید، در ویندوز Win+P ← Extend را انتخاب کنید و اجازهٔ بازشدن Al-Shifa TV را بدهید.</span>
    <a href="/call-screen" target="_blank" rel="noopener" class="btn btn-soft text-nowrap">پیش‌نمایش در همین مرورگر</a>
    <span>اگر اجراکننده باز نشد، فایل نصب را دانلود و از حالت فشرده خارج کنید؛ سپس Install-TV.cmd را اجرا کنید و آدرس همین سیستم را وارد کنید: <bdi>{{ locationOrigin }}</bdi></span>
    <a href="/tools/AlShifa-TV.zip" download>دانلود اجراکنندهٔ ویندوز</a>
  </div>
</template>

<style scoped>
.tv-help { flex-basis: 100%; display: flex; flex-wrap: wrap; align-items: center; gap: 10px; padding: 12px; border: 1px solid var(--bs-border-color, #dbe4e8); border-radius: 8px; font-size: .875rem; }
</style>
