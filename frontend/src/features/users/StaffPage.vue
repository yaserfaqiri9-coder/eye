<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { clearLookups } from '@/api/lookups'
import { api, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import type { LookupItem } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { removeRecord } from '@/utils/removeRecord'

interface StaffRow {
  id: number
  fullName: string
  phone: string | null
  isActive: boolean
  departmentId: number | null
  departmentName: string | null
  userId: number | null
  userName: string | null
  version: number
}

const auth = useAuthStore()
const rows = ref<StaffRow[]>([])
const departments = ref<LookupItem[]>([])
const users = ref<{ id: number; userName: string; fullName: string }[]>([])
const loading = ref(true)
const error = ref('')
const notice = ref('')

const form = reactive({
  open: false,
  id: null as number | null,
  version: null as number | null,
  fullName: '',
  phone: '',
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
    rows.value = (await api<{ items: StaffRow[] }>('/staff?isDoctor=false&pageSize=100')).items
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function open(row?: StaffRow) {
  Object.assign(form, {
    open: true,
    id: row?.id ?? null,
    version: row?.version ?? null,
    fullName: row?.fullName ?? '',
    phone: row?.phone ?? '',
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
    fullName: form.fullName,
    phone: form.phone,
    isDoctor: false,
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

async function remove(row: StaffRow) {
  error.value = ''
  notice.value = ''
  try {
    notice.value = await removeRecord(`/staff/${row.id}?version=${row.version}`, row.fullName)
    if (notice.value) {
      clearLookups()
      await load()
    }
  } catch (cause) {
    error.value = errorMessage(cause)
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
  <UiCard title="کارمندان">
    <template #actions>
      <button v-if="auth.can('users.create')" type="button" class="btn btn-primary" @click="open()">کارمند جدید</button>
    </template>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">کارمندی ثبت نشده است. داکتران از صفحه «فهرست داکتران» ثبت می‌شوند.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr><th>نام</th><th>بخش</th><th>تماس</th><th>حساب کاربری</th><th>وضعیت</th><th v-if="auth.can('users.manage')">عملیات</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td>{{ row.fullName }}</td>
            <td>{{ row.departmentName ?? '—' }}</td>
            <td><span class="ltr-num">{{ row.phone ?? '—' }}</span></td>
            <td><span class="ltr-num">{{ row.userName ?? '—' }}</span></td>
            <td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
            <td v-if="auth.can('users.manage')" class="text-nowrap">
              <button type="button" class="btn btn-soft btn-sm" @click="open(row)">ویرایش</button>
              <button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="remove(row)">حذف</button>
            </td>
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
      <div class="mb-3">
        <label class="form-check-label"><input v-model="form.isActive" class="form-check-input ms-2" type="checkbox" />فعال</label>
      </div>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="form.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
