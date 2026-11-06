<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiModal from '@/components/UiModal.vue'
import { clearLookups } from '@/api/lookups'
import { useAuthStore } from '@/stores/auth'
import { removeRecord } from '@/utils/removeRecord'
import DoctorFormModal, { type DoctorRow as Doctor } from './DoctorFormModal.vue'
const auth = useAuthStore()
const rows = ref<Doctor[]>([])
const loading = ref(true)
const error = ref('')
const notice = ref('')
const profile = ref<Doctor | null>(null)
// null is closed; a new doctor has no row to edit, so "new" is its own value.
const editing = ref<Doctor | 'new' | null>(null)
const unlinked = computed(() => rows.value.filter((r) => r.isActive && !r.userId).map((r) => r.fullName))
const socials = [{ key: 'facebook', icon: 'ri-facebook-fill', label: 'Facebook' }, { key: 'twitter', icon: 'ri-twitter-fill', label: 'Twitter' }, { key: 'google', icon: 'ri-google-fill', label: 'Google' }] as const
function safeLink(value: string | null) { return value && /^https?:\/\//i.test(value) ? value : undefined }
function avatar(row: Doctor) { return `/images/doctors/${row.avatar >= 12 && row.avatar <= 18 ? row.avatar : 12}.jpg` }
async function load() {
  try {
    const loaded: Doctor[] = []
    let page = 1
    let total = 0
    do {
      const result = await api<{ items: Doctor[]; total: number }>(`/staff?isDoctor=true&pageSize=100&page=${page++}`)
      loaded.push(...result.items); total = result.total
      if (!result.items.length) break
    } while (loaded.length < total)
    rows.value = loaded
    error.value = ''
  } catch (e) { error.value = errorMessage(e) } finally { loading.value = false }
}
function onSaved() {
  editing.value = null
  load()
}
async function remove(row: Doctor) {
  error.value = ''
  notice.value = ''
  try {
    notice.value = await removeRecord(`/staff/${row.id}?version=${row.version}`, row.fullName)
    if (notice.value) {
      clearLookups()
      await load()
    }
  } catch (e) { error.value = errorMessage(e) }
}
onMounted(load)
</script>
<template>
  <section class="doctor-directory" aria-labelledby="doctors-title">
    <header class="doctor-directory__heading"><h2 id="doctors-title">فهرست داکتران</h2><button v-if="auth.can('users.create')" type="button" class="btn btn-primary" @click="editing = 'new'">ثبت داکتر</button></header>
    <div v-if="auth.can('users.manage') && unlinked.length" class="alert alert-warning" role="alert">این داکتران حساب کاربری وصل‌شده ندارند و مریضان‌شان در صف مشترک داکتران دیده می‌شوند، نه صف خودشان: {{ unlinked.join('، ') }}.</div>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="!rows.length && !error" class="xr-state">داکتری ثبت نشده است. با دکمهٔ «ثبت داکتر» در بالای همین صفحه، داکتر جدید را ثبت کنید.</div>
    <div v-else class="doctor-directory__grid">
      <article v-for="row in rows" :key="row.id" class="doctor-card">
        <img :src="avatar(row)" alt="" width="80" height="80" class="doctor-card__avatar" />
        <div class="doctor-card__info"><h3>{{ row.fullName }}</h3><p>{{ row.specialty || row.departmentName || '—' }}</p><a v-if="safeLink(row.website)" :href="safeLink(row.website)" target="_blank" rel="noopener noreferrer" dir="ltr">{{ row.website }}</a><span v-else class="doctor-card__website">—</span></div>
        <p class="doctor-card__description">{{ row.biography || 'معرفی ثبت نشده است.' }}</p>
        <ul class="doctor-card__social" aria-label="پیوندهای داکتر">
          <li v-for="social in socials" :key="social.key"><a v-if="safeLink(row[social.key])" :href="safeLink(row[social.key])" :aria-label="`${social.label}: ${row.fullName}`" target="_blank" rel="noopener noreferrer"><i :class="social.icon" aria-hidden="true"></i></a><span v-else :aria-label="`${social.label}: ثبت نشده`"><i :class="social.icon" aria-hidden="true"></i></span></li>
        </ul>
        <div class="doctor-card__actions"><button class="doctor-card__profile" type="button" @click="profile = row">مشاهده پروفایل</button><button v-if="auth.can('users.manage')" class="btn btn-soft btn-sm" type="button" :aria-label="`ویرایش ${row.fullName}`" @click="editing = row">ویرایش</button><button v-if="auth.can('users.manage')" class="btn btn-soft-danger btn-sm" type="button" :aria-label="`حذف ${row.fullName}`" @click="remove(row)">حذف</button></div>
      </article>
    </div>
  </section>
  <UiModal v-if="profile" :title="profile.fullName" @close="profile = null">
    <div class="text-center"><img :src="avatar(profile)" alt="" width="80" height="80" class="doctor-card__avatar mb-3" /><h3>{{ profile.specialty || profile.departmentName }}</h3><p class="doctor-profile-bio">{{ profile.biography || 'معرفی ثبت نشده است.' }}</p></div>
    <dl><dt>بخش</dt><dd>{{ profile.departmentName || '—' }}</dd><dt>تماس</dt><dd dir="ltr">{{ profile.phone || '—' }}</dd><dt>وضعیت</dt><dd>{{ profile.isActive ? 'فعال' : 'غیرفعال' }}</dd></dl>
    <a v-if="safeLink(profile.website)" :href="safeLink(profile.website)" target="_blank" rel="noopener noreferrer">{{ profile.website }}</a>
  </UiModal>
  <DoctorFormModal v-if="editing" :doctor="editing === 'new' ? null : editing" @close="editing = null" @saved="onSaved" />
</template>
<style scoped>
.doctor-directory__heading { background: #fff; border-radius: 25px; min-height: 60px; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 0 20px; margin-bottom: 30px; border-bottom: 1px solid #eee; box-shadow: 0 4px 5px 1px rgb(175 175 175 / 10%); }
.doctor-directory__heading h2 { font-size: 19.6px; font-weight: 400; line-height: 1.5; margin: 0; color: #3f414d; }
.doctor-directory__grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); column-gap: 30px; }
.doctor-card { padding: 20px; background: #fff; border-radius: 25px; margin-bottom: 30px; text-align: center; box-shadow: 0 4px 5px 1px rgb(175 175 175 / 10%); color: #a09e9e; font-size: 14px; line-height: 25px; min-width: 0; }
.doctor-card__avatar { border-radius: 50%; object-fit: cover; width: 80px; height: 80px; }
.doctor-card__info { margin-top: 16px; }
.doctor-card__info h3 { font-size: 19.6px; line-height: 1.5; font-weight: 400; color: #3f414d; margin: 0; overflow-wrap: anywhere; }
.doctor-card__info p { margin: 0; }
.doctor-card__info a, .doctor-card__website { color: #089bab; display: block; overflow-wrap: anywhere; }
.doctor-card__description { margin: 8px 0 0; min-height: 75px; overflow-wrap: anywhere; white-space: pre-line; }
.doctor-card__social { display: flex; justify-content: center; gap: 0; list-style: none; padding: 0; margin: 16px 0; direction: ltr; }
.doctor-card__social a, .doctor-card__social span { display: block; color: #089bab; background: #ceebee; height: 30px; width: 30px; line-height: 30px; border-radius: 5px; margin: 0 3px; }
.doctor-card__actions { display: flex; justify-content: center; align-items: center; flex-wrap: wrap; gap: 8px; }
.doctor-card__profile { background: linear-gradient(to right, #089bab 0%, #0db5c8 100%); border: 1px solid #089bab; color: #fff; border-radius: 5px; font: inherit; line-height: 1.5; padding: 6px 12px; cursor: pointer; }
.doctor-card__profile:hover { background: linear-gradient(to left, #089bab, #0db5c8); }
.doctor-card__profile:focus-visible, .doctor-card a:focus-visible { outline: 3px solid #089bab; outline-offset: 3px; }
.doctor-profile-bio { white-space: pre-line; }
@media (max-width: 767.98px) { .doctor-directory__grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 575.98px) { .doctor-directory__grid { grid-template-columns: 1fr; } }
</style>
