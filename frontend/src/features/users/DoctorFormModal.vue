<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { clearLookups } from '@/api/lookups'
import UiModal from '@/components/UiModal.vue'
import type { LookupItem } from '@/features/visits/types'

export interface DoctorRow {
  id: number
  fullName: string
  phone: string | null
  departmentId: number | null
  departmentName: string | null
  isActive: boolean
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

const props = defineProps<{ doctor?: DoctorRow | null }>()
const emit = defineEmits<{ close: []; saved: [] }>()

const links = [['website', 'وب‌سایت'], ['facebook', 'Facebook'], ['twitter', 'Twitter'], ['google', 'Google']] as const

const departments = ref<LookupItem[]>([])
const users = ref<{ id: number; userName: string; fullName: string }[]>([])
const saving = ref(false)
const error = ref('')
const form = reactive({
  fullName: props.doctor?.fullName ?? '',
  phone: props.doctor?.phone ?? '',
  specialty: props.doctor?.specialty ?? '',
  departmentId: props.doctor?.departmentId ?? null as number | null,
  userId: props.doctor?.userId ?? null as number | null,
  isActive: props.doctor?.isActive ?? true,
  biography: props.doctor?.biography ?? '',
  avatar: props.doctor?.avatar ?? 12,
  website: props.doctor?.website ?? '',
  facebook: props.doctor?.facebook ?? '',
  twitter: props.doctor?.twitter ?? '',
  google: props.doctor?.google ?? '',
})

async function save() {
  saving.value = true
  error.value = ''
  const body = { ...form, isDoctor: true, version: props.doctor?.version ?? null }
  try {
    if (props.doctor) {
      await api(`/staff/${props.doctor.id}`, { method: 'PUT', body })
    } else {
      await api('/staff', { method: 'POST', body })
    }
    clearLookups()
    emit('saved')
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
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
})
</script>

<template>
  <UiModal :title="doctor ? 'ویرایش داکتر' : 'ثبت داکتر'" @close="$emit('close')">
    <form @submit.prevent="save">
      <div class="mb-3">
        <label class="form-label" for="d-name">نام کامل</label>
        <input id="d-name" v-model="form.fullName" class="form-control" maxlength="150" required autofocus />
      </div>
      <div class="mb-3">
        <label class="form-label" for="d-specialty">تخصص</label>
        <input id="d-specialty" v-model="form.specialty" class="form-control" maxlength="150" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="d-phone">تماس</label>
        <input id="d-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" />
      </div>
      <div class="mb-3">
        <label class="form-label" for="d-dept">بخش</label>
        <select id="d-dept" v-model.number="form.departmentId" class="form-select">
          <option :value="null">بدون بخش</option>
          <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
      </div>
      <div class="mb-3">
        <label class="form-label" for="d-user">حساب کاربری</label>
        <select id="d-user" v-model.number="form.userId" class="form-select">
          <option :value="null">بدون حساب</option>
          <option v-for="u in users" :key="u.id" :value="u.id">{{ u.fullName }} ({{ u.userName }})</option>
        </select>
        <p class="form-hint mb-0">مریضان این داکتر در صف همین حساب دیده می‌شوند.</p>
      </div>
      <div class="mb-3">
        <label class="form-check-label"><input v-model="form.isActive" class="form-check-input ms-2" type="checkbox" />فعال</label>
      </div>
      <div class="mb-3">
        <label for="d-bio" class="form-label">معرفی داکتر</label>
        <textarea id="d-bio" v-model="form.biography" class="form-control" maxlength="1000"></textarea>
      </div>
      <div class="mb-3">
        <label for="d-avatar" class="form-label">تصویر نمایشی</label>
        <select id="d-avatar" v-model.number="form.avatar" class="form-select"><option v-for="n in 7" :key="n" :value="n + 11">تصویر {{ n }}</option></select>
        <img :src="`/images/doctors/${form.avatar}.jpg`" width="80" height="80" alt="تصویر نمایشی انتخاب‌شده" class="rounded-circle mt-2" />
      </div>
      <div v-for="link in links" :key="link[0]" class="mb-3">
        <label :for="`d-${link[0]}`" class="form-label">{{ link[1] }}</label>
        <input :id="`d-${link[0]}`" v-model="form[link[0]]" type="url" dir="ltr" class="form-control" maxlength="500" placeholder="https://" />
      </div>
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
