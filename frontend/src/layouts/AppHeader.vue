<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { roleLabel } from '@/features/users/roles'

defineEmits<{ 'toggle-sidebar': [] }>()

const auth = useAuthStore()
const router = useRouter()
const error = ref('')
const search = ref('')

const password = reactive({ open: false, current: '', next: '', saving: false, error: '', done: false })

async function changePassword() {
  password.saving = true
  password.error = ''
  try {
    await api('/auth/change-password', { method: 'POST', body: { currentPassword: password.current, newPassword: password.next } })
    Object.assign(password, { current: '', next: '', done: true })
  } catch (cause) {
    password.error = errorMessage(cause)
  } finally {
    password.saving = false
  }
}

function submitSearch() {
  router.push({ name: 'patients', query: { q: search.value.trim() } })
}

async function logout() {
  error.value = ''
  try {
    await auth.logout()
    await router.push({ name: 'login' })
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'خروج انجام نشد.'
  }
}
</script>

<template>
  <header class="header">
    <button
      type="button"
      class="btn btn-soft header__menu"
      aria-label="باز کردن منو"
      @click="$emit('toggle-sidebar')"
    >
      <i class="bi bi-list" aria-hidden="true"></i>
    </button>
    <form v-if="auth.can('patients.view')" class="header__search" role="search" @submit.prevent="submitSearch">
      <input v-model="search" type="search" class="form-control" placeholder="جستجوی مریض..." aria-label="جستجوی مریض در هدر" />
      <i class="bi bi-search" aria-hidden="true"></i>
    </form>
    <div class="header__spacer"></div>
    <span v-if="error" class="text-danger small" role="alert">{{ error }}</span>
    <div v-if="auth.user" class="header__user">
      <div class="header__name">{{ auth.user.fullName }}</div>
      <div class="header__role">{{ auth.user.roles.map(roleLabel).join('، ') }}</div>
    </div>
    <button type="button" class="btn btn-soft" aria-label="تغییر رمز" title="تغییر رمز" @click="Object.assign(password, { open: true, current: '', next: '', error: '', done: false })">
      <i class="bi bi-key-fill" aria-hidden="true"></i>
    </button>
    <button type="button" class="btn btn-soft" aria-label="خروج" title="خروج" @click="logout">
      <i class="bi bi-box-arrow-left" aria-hidden="true"></i>
    </button>
  </header>

  <UiModal v-if="password.open" title="تغییر رمز" @close="password.open = false">
    <div v-if="password.done" class="alert alert-success py-2 mb-0" role="status">رمز شما تغییر کرد.</div>
    <form v-else @submit.prevent="changePassword">
      <label class="form-label" for="pw-current">رمز فعلی</label>
      <input id="pw-current" v-model="password.current" class="form-control mb-3" type="password" dir="ltr" autocomplete="current-password" required />
      <label class="form-label" for="pw-next">رمز جدید (حداقل ۱۰ حرف)</label>
      <input id="pw-next" v-model="password.next" class="form-control mb-3" type="password" dir="ltr" minlength="10" autocomplete="new-password" required />
      <div v-if="password.error" class="alert alert-danger py-2" role="alert">{{ password.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="password.saving">تغییر رمز</button>
    </form>
  </UiModal>
</template>

<style scoped>
.header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: var(--xr-header-height);
  padding: 0 20px;
  background: var(--xr-surface);
  border-radius: var(--xr-radius-card);
  box-shadow: var(--xr-shadow-card);
}

.header__menu {
  display: none;
}

.header__spacer {
  flex: 1;
}

.header__search {
  position: relative;
  width: 350px;
  min-width: 0;
}

.header__search .form-control {
  min-height: 35px;
  padding-inline-end: 36px;
  font-size: 13px;
}

.header__search .bi {
  position: absolute;
  inset-inline-end: 14px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--xr-primary);
  font-size: 13px;
}

.header__name {
  font-size: 16px;
  line-height: 1.3;
}

.header__role {
  color: var(--xr-primary);
  font-size: 12px;
}

@media (max-width: 991.98px) {
  .header {
    padding: 0 12px;
  }

  .header__menu {
    display: inline-flex;
  }

  .header__search {
    flex: 1;
    width: auto;
  }

  .header__spacer,
  .header__user {
    display: none;
  }
}
</style>
