<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { clearLookups } from '@/api/lookups'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import type { LookupItem } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'

interface StaffRow {
  id: number
  fullName: string
  phone: string | null
  isDoctor: boolean
  isActive: boolean
  departmentId: number | null
  departmentName: string | null
  userId: number | null
  userName: string | null
  version: number
  specialty: string | null
  biography: string | null
  website: string | null
  facebook: string | null
  twitter: string | null
  google: string | null
  avatar: number
}

const auth = useAuthStore()
const rows = ref<StaffRow[]>([])
const departments = ref<LookupItem[]>([])
const users = ref<{ id: number; userName: string; fullName: string }[]>([])
const loading = ref(true)
const error = ref('')
const unlinkedDoctors = computed(() => rows.value.filter((r) => r.isDoctor && r.isActive && !r.userId).map((r) => r.fullName))

const form = reactive({
  open: false,
  id: null as number | null,
  version: null as number | null,
  specialty: '', biography: '', website: '', facebook: '', twitter: '', google: '', avatar: 12,
  fullName: '',
  phone: '',
  isDoctor: false,
  isActive: true,
  departmentId: null as number | null,
  userId: null as number | null,
  saving: false,
  error: '',
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    rows.value = (await api<{ items: StaffRow[] }>('/staff?pageSize=100')).items
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function open(row?: StaffRow) {
  Object.assign(form, {
    specialty: row?.specialty ?? '', biography: row?.biography ?? '', website: row?.website ?? '', facebook: row?.facebook ?? '', twitter: row?.twitter ?? '', google: row?.google ?? '', avatar: row?.avatar ?? 12,
    open: true,
    id: row?.id ?? null,
    version: row?.version ?? null,
    fullName: row?.fullName ?? '',
    phone: row?.phone ?? '',
    isDoctor: row?.isDoctor ?? false,
    isActive: row?.isActive ?? true,
    departmentId: row?.departmentId ?? null,
    userId: row?.userId ?? null,
    saving: false,
    error: '',
  })
}

async function save() {
  form.saving = true
  form.error = ''
  const body = {
    specialty: form.specialty, biography: form.biography, website: form.website, facebook: form.facebook, twitter: form.twitter, google: form.google, avatar: form.avatar,
    fullName: form.fullName,
    phone: form.phone,
    isDoctor: form.isDoctor,
    isActive: form.isActive,
    departmentId: form.departmentId,
    userId: form.userId,
    version: form.version,
  }
  try {
    if (form.id) {
      await api(`/staff/${form.id}`, { method: 'PUT', body })
    } else {
      await api('/staff', { method: 'POST', body })
    }
    clearLookups()
    form.open = false
    await load()
  } catch (cause) {
    form.error = errorMessage(cause)
  } finally {
    form.saving = false
  }
}

onMounted(async () => {
  try {
    ;[departments.value, users.value] = await Promise.all([
      api<LookupItem[]>('/lookups/departments'),
      api<{ items: { id: number; userName: string; fullName: string }[] }>('/users?pageSize=100').then((r) => r.items),
    ])
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
})
</script>

<template>
  <UiCard title="کارمندان و داکتران">
    <template #actions>
      <button v-if="auth.can('users.create')" type="button" class="btn btn-primary" @click="open()">کارمند جدید</button>
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="unlinkedDoctors.length" class="alert alert-warning" role="alert">
      این داکتران حساب کاربری وصل‌شده ندارند و مریضان‌شان در صف مشترک داکتران دیده می‌شوند، نه صف خودشان: {{ unlinkedDoctors.join('، ') }}.
    </div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">کارمندی ثبت نشده است.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نام</th><th>بخش</th><th>نوع</th><th>تماس</th><th>حساب کاربری</th><th>وضعیت</th><th v-if="auth.can('users.manage')">عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td>{{ row.fullName }}</td>
            <td>{{ row.departmentName ?? '—' }}</td>
            <td>{{ row.isDoctor ? 'داکتر' : 'کارمند' }}</td>
            <td><span class="ltr-num">{{ row.phone ?? '—' }}</span></td>
            <td><span class="ltr-num">{{ row.userName ?? '—' }}</span></td>
            <td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
            <td v-if="auth.can('users.manage')"><button type="button" class="btn btn-soft btn-sm" @click="open(row)">ویرایش</button></td>
          </tr>
        </tbody>
      </table>
    </div>
  </UiCard>

  <UiModal v-if="form.open" :title="form.id ? 'ویرایش کارمند' : 'کارمند جدید'" @close="form.open = false">
    <form @submit.prevent="save">
      <div class="mb-3">
        <label class="form-label" for="s-name">نام کامل</label>
        <input id="s-name" v-model="form.fullName" class="form-control" maxlength="150" required />
      </div>
      <div class="mb-3">
        <label class="form-label" for="s-phone">تماس</label>
        <input id="s-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="s-dept">بخش</label>
        <select id="s-dept" v-model.number="form.departmentId" class="form-select">
          <option :value="null">بدون بخش</option>
          <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="s-user">حساب کاربری</label>
        <select id="s-user" v-model.number="form.userId" class="form-select">
          <option :value="null">بدون حساب</option>
          <option v-for="u in users" :key="u.id" :value="u.id">{{ u.fullName }} ({{ u.userName }})</option>
        </select>
      </div>
      <div class="mb-3 d-flex gap-4">
        <label class="form-check-label"><input v-model="form.isDoctor" class="form-check-input ms-2" type="checkbox" />داکتر است</label>
        <label class="form-check-label"><input v-model="form.isActive" class="form-check-input ms-2" type="checkbox" />فعال</label>
      </div>
      <template v-if="form.isDoctor">
        <div class="mb-3"><label for="s-specialty" class="form-label">تخصص</label><input id="s-specialty" v-model="form.specialty" class="form-control" maxlength="150" /></div>
        <div class="mb-3"><label for="s-bio" class="form-label">معرفی داکتر</label><textarea id="s-bio" v-model="form.biography" class="form-control" maxlength="1000"></textarea></div>
        <div class="mb-3"><label for="s-avatar" class="form-label">تصویر نمایشی قالب</label><select id="s-avatar" v-model.number="form.avatar" class="form-select"><option v-for="n in 7" :key="n" :value="n + 11">تصویر {{ n }}</option></select><img :src="`/images/doctors/${form.avatar}.jpg`" width="80" height="80" alt="تصویر نمایشی انتخاب‌شده" class="rounded-circle mt-2" /></div>
        <div v-for="link in ([['website', 'وب‌سایت'], ['facebook', 'Facebook'], ['twitter', 'Twitter'], ['google', 'Google']] as const)" :key="link[0]" class="mb-3"><label :for="`s-${link[0]}`" class="form-label">{{ link[1] }}</label><input :id="`s-${link[0]}`" v-model="form[link[0]]" type="url" dir="ltr" class="form-control" maxlength="500" placeholder="https://" /></div>
      </template>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="form.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
