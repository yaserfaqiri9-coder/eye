<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, ApiError } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { roleLabel, roles } from './roles'

interface UserRow {
  id: number
  userName: string
  fullName: string
  isActive: boolean
  roles: string[]
}

const auth = useAuthStore()
const rows = ref<UserRow[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 25
const q = ref('')
const loading = ref(true)
const error = ref('')

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))

function messageOf(cause: unknown) {
  return cause instanceof ApiError ? cause.message : 'خطایی رخ داد. دوباره تلاش کنید.'
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    if (q.value.trim()) params.set('q', q.value.trim())
    const result = await api<{ items: UserRow[]; total: number }>(`/users?${params}`)
    rows.value = result.items
    total.value = result.total
  } catch (cause) {
    error.value = messageOf(cause)
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  load()
}

function goTo(next: number) {
  page.value = next
  load()
}

const form = reactive({ open: false, saving: false, error: '', userName: '', fullName: '', password: '', roles: [] as string[] })

function openCreate() {
  Object.assign(form, { open: true, saving: false, error: '', userName: '', fullName: '', password: '', roles: [] })
}

async function create() {
  if (form.roles.length === 0) {
    form.error = 'حداقل یک نقش انتخاب کنید.'
    return
  }
  form.saving = true
  form.error = ''
  try {
    await api('/users', {
      method: 'POST',
      body: { userName: form.userName, fullName: form.fullName, password: form.password, roles: form.roles },
    })
    form.open = false
    await load()
  } catch (cause) {
    form.error = messageOf(cause)
  } finally {
    form.saving = false
  }
}

const roleForm = reactive({ user: null as UserRow | null, saving: false, error: '', roles: [] as string[] })

function openRoles(user: UserRow) {
  Object.assign(roleForm, { user, saving: false, error: '', roles: [...user.roles] })
}

async function saveRoles() {
  if (roleForm.roles.length === 0) {
    roleForm.error = 'حداقل یک نقش انتخاب کنید.'
    return
  }
  roleForm.saving = true
  roleForm.error = ''
  try {
    await api(`/users/${roleForm.user!.id}/roles`, { method: 'PUT', body: { roles: roleForm.roles } })
    roleForm.user = null
    await load()
  } catch (cause) {
    roleForm.error = messageOf(cause)
  } finally {
    roleForm.saving = false
  }
}

const reset = reactive({ user: null as UserRow | null, password: '', saving: false, error: '' })

async function saveReset() {
  reset.saving = true
  reset.error = ''
  try {
    await api(`/users/${reset.user!.id}/reset-password`, { method: 'POST', body: { newPassword: reset.password } })
    reset.user = null
    reset.password = ''
  } catch (cause) {
    reset.error = messageOf(cause)
  } finally {
    reset.saving = false
  }
}

async function setActive(user: UserRow, active: boolean) {
  error.value = ''
  try {
    await api(`/users/${user.id}/${active ? 'activate' : 'deactivate'}`, { method: 'POST' })
    await load()
  } catch (cause) {
    error.value = messageOf(cause)
  }
}

onMounted(load)
</script>

<template>
  <UiCard title="کاربران">
    <template #actions>
      <button v-if="auth.can('users.create')" type="button" class="btn btn-primary" @click="openCreate">
        کاربر جدید
      </button>
    </template>

    <form class="row g-2 mb-3" @submit.prevent="search">
      <div class="col-sm-6 col-lg-4">
        <input v-model="q" type="search" class="form-control" placeholder="جستجوی نام یا نام کاربری" aria-label="جستجو" />
      </div>
      <div class="col-auto"><button type="submit" class="btn btn-soft">جستجو</button></div>
    </form>

    <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>

    <div v-if="loading" class="py-4 text-center text-muted" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="py-4 text-center text-muted">کاربری پیدا نشد.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead>
          <tr>
            <th>نام کاربری</th>
            <th>نام کامل</th>
            <th>نقش</th>
            <th>وضعیت</th>
            <th v-if="auth.can('users.manage')">عملیات</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="user in rows" :key="user.id">
            <td><span class="ltr-num">{{ user.userName }}</span></td>
            <td>{{ user.fullName }}</td>
            <td>{{ user.roles.map(roleLabel).join('، ') }}</td>
            <td>
              <span class="xr-badge" :class="user.isActive ? 'xr-badge--green' : 'xr-badge--red'">
                {{ user.isActive ? 'فعال' : 'غیرفعال' }}
              </span>
            </td>
            <td v-if="auth.can('users.manage')" class="text-nowrap">
              <button type="button" class="btn btn-soft btn-sm" @click="openRoles(user)">نقش‌ها</button>
              <button type="button" class="btn btn-soft btn-sm ms-2" @click="Object.assign(reset, { user, password: '', error: '' })">بازنشانی رمز</button>
              <button
                v-if="user.id !== auth.user?.id"
                type="button"
                class="btn btn-soft btn-sm ms-2"
                @click="setActive(user, !user.isActive)"
              >
                {{ user.isActive ? 'غیرفعال‌سازی' : 'فعال‌سازی' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="pageCount > 1" class="d-flex align-items-center justify-content-center gap-2 mt-3">
      <button type="button" class="btn btn-soft btn-sm" :disabled="page <= 1" @click="goTo(page - 1)">قبلی</button>
      <span>{{ page }} از {{ pageCount }}</span>
      <button type="button" class="btn btn-soft btn-sm" :disabled="page >= pageCount" @click="goTo(page + 1)">بعدی</button>
    </div>
  </UiCard>

  <UiModal v-if="form.open" title="کاربر جدید" @close="form.open = false">
    <form @submit.prevent="create">
      <div class="mb-3">
        <label class="form-label" for="user-name">نام کاربری</label>
        <input id="user-name" v-model="form.userName" class="form-control" dir="ltr" autocomplete="off" required />
      </div>
      <div class="mb-3">
        <label class="form-label" for="user-fullname">نام کامل</label>
        <input id="user-fullname" v-model="form.fullName" class="form-control" required />
      </div>
      <div class="mb-3">
        <label class="form-label" for="user-password">رمز عبور (حداقل ۱۰ حرف)</label>
        <input id="user-password" v-model="form.password" class="form-control" type="password" dir="ltr" minlength="10" autocomplete="new-password" required />
      </div>
      <fieldset class="mb-3">
        <legend class="form-label">نقش‌ها</legend>
        <div class="row">
          <div v-for="role in roles" :key="role.value" class="col-6">
            <label class="form-check-label"><input v-model="form.roles" class="form-check-input ms-2" type="checkbox" :value="role.value" />{{ role.label }}</label>
          </div>
        </div>
      </fieldset>
      <div v-if="form.error" class="alert alert-danger py-2" role="alert">{{ form.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="form.saving">ذخیره</button>
    </form>
  </UiModal>

  <UiModal v-if="reset.user" :title="`بازنشانی رمز ${reset.user.fullName}`" @close="reset.user = null">
    <form @submit.prevent="saveReset">
      <p class="text-muted small">نشست فعلی این کاربر پایان می‌یابد. رمز جدید را حضوری به او بدهید.</p>
      <label class="form-label" for="reset-password">رمز جدید (حداقل ۱۰ حرف)</label>
      <input id="reset-password" v-model="reset.password" class="form-control mb-3" type="password" dir="ltr" minlength="10" autocomplete="new-password" required />
      <div v-if="reset.error" class="alert alert-danger py-2" role="alert">{{ reset.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="reset.saving">بازنشانی رمز</button>
    </form>
  </UiModal>

  <UiModal v-if="roleForm.user" :title="`نقش‌های ${roleForm.user.fullName}`" @close="roleForm.user = null">
    <form @submit.prevent="saveRoles">
      <p class="text-muted small">با تغییر نقش، نشست فعلی این کاربر پایان می‌یابد و باید دوباره وارد شود.</p>
      <div class="row mb-3">
        <div v-for="role in roles" :key="role.value" class="col-6">
          <label class="form-check-label"><input v-model="roleForm.roles" class="form-check-input ms-2" type="checkbox" :value="role.value" />{{ role.label }}</label>
        </div>
      </div>
      <div v-if="roleForm.error" class="alert alert-danger py-2" role="alert">{{ roleForm.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="roleForm.saving">ذخیره</button>
    </form>
  </UiModal>
</template>
