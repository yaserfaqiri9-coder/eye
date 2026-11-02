<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import UiAvatar from '@/components/UiAvatar.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { roleLabel } from '@/features/users/roles'

defineEmits<{ 'toggle-sidebar': [] }>()

const auth = useAuthStore()
const router = useRouter()
const error = ref('')
const search = ref('')
const stuck = ref(false)

/** Once the page scrolls, the floating bar snaps to the top edge and spans the content, as in the template. */
function onScroll() {
  stuck.value = window.scrollY > 0
}

onMounted(() => {
  onScroll()
  window.addEventListener('scroll', onScroll, { passive: true })
})

onBeforeUnmount(() => window.removeEventListener('scroll', onScroll))

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

const roles = computed(() => auth.user?.roles.map(roleLabel).join('، ') ?? '')

function toggleFullscreen() {
  if (document.fullscreenElement) void document.exitFullscreen()
  else void document.documentElement.requestFullscreen()
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
  <header class="header" :class="{ 'header--stuck': stuck }">
    <button
      type="button"
      class="btn btn-soft header__menu"
      aria-label="باز کردن منو"
      @click="$emit('toggle-sidebar')"
    >
      <i class="ri-menu-line" aria-hidden="true"></i>
    </button>
    <form v-if="auth.can('patients.view')" class="header__search" role="search" @submit.prevent="submitSearch">
      <input v-model="search" type="search" class="form-control" placeholder="جستجوی مریض..." aria-label="جستجوی مریض در هدر" />
      <i class="ri-search-line" aria-hidden="true"></i>
    </form>
    <div class="header__spacer"></div>
    <span v-if="error" class="text-danger small" role="alert">{{ error }}</span>
    <button type="button" class="header__icon" aria-label="تمام صفحه" title="تمام صفحه" @click="toggleFullscreen">
      <i class="ri-fullscreen-line" aria-hidden="true"></i>
    </button>
    <button type="button" class="header__icon" aria-label="تغییر رمز" title="تغییر رمز" @click="Object.assign(password, { open: true, current: '', next: '', error: '', done: false })">
      <i class="ri-key-2-fill" aria-hidden="true"></i>
    </button>
    <button type="button" class="header__icon" aria-label="خروج" title="خروج" @click="logout">
      <i class="ri-logout-box-line" aria-hidden="true"></i>
    </button>
    <div v-if="auth.user" class="header__user">
      <UiAvatar :seed="auth.user.fullName" :size="50" plain />
      <div class="header__identity">
        <div class="header__name">{{ auth.user.fullName }}</div>
        <div class="header__role" :title="roles">{{ roles }}</div>
      </div>
    </div>
  </header>

  <UiModal v-if="password.open" title="تغییر رمز" @close="password.open = false">
    <div v-if="password.done" class="alert alert-success mb-0" role="status">رمز شما تغییر کرد.</div>
    <form v-else @submit.prevent="changePassword">
      <label class="form-label" for="pw-current">رمز فعلی</label>
      <input id="pw-current" v-model="password.current" class="form-control mb-3" type="password" dir="ltr" autocomplete="current-password" required />
      <label class="form-label" for="pw-next">رمز جدید (حداقل ۱۰ حرف)</label>
      <input id="pw-next" v-model="password.next" class="form-control mb-3" type="password" dir="ltr" minlength="10" autocomplete="new-password" required />
      <div v-if="password.error" class="alert alert-danger" role="alert">{{ password.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="password.saving">تغییر رمز</button>
    </form>
  </UiModal>
</template>

<style scoped>
.header {
  position: fixed;
  top: 20px;
  inset-inline: calc(var(--xr-sidebar-current) + 30px) 30px;
  z-index: 99;
  display: flex;
  align-items: center;
  min-height: var(--xr-header-height);
  background: var(--xr-surface);
  border-radius: var(--xr-radius-card);
  box-shadow: var(--xr-shadow-bar);
  transition: inset 0.3s ease-in-out, border-radius 0.3s ease-in-out;
}

.header--stuck {
  top: 0;
  inset-inline: var(--xr-sidebar-current) 0;
  border-radius: 0;
}

.header__menu {
  display: none;
}

.header__spacer {
  flex: 1;
}

.header__search {
  position: relative;
  flex: none;
  width: 350px;
  min-width: 0;
  margin-inline: 15px;
}

.header__search .form-control {
  min-height: 35px;
  padding-block: 5px;
  padding-inline: 15px 40px;
  border: 0;
  background: var(--xr-primary-light);
  color: #000;
  line-height: 25.2px;
}

.header__search .form-control::placeholder {
  color: #757575;
}

.header__search i {
  position: absolute;
  inset-inline-end: 15px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--xr-primary);
  font-size: 16px;
}

.header__icon {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: var(--xr-header-height);
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--xr-primary);
  font-size: 18px;
}

.header__user {
  display: flex;
  flex: none;
  align-items: center;
  padding-inline: 15px 20px;
}

.header__identity {
  min-width: 0;
  max-width: 170px;
}

.header__name {
  color: var(--xr-text);
  font-size: 18px;
  line-height: 27px;
}

.header__role {
  overflow: hidden;
  color: var(--xr-primary);
  font-size: 12px;
  line-height: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

@media (max-width: 991.98px) {
  .header,
  .header--stuck {
    position: static;
    padding: 0 12px;
    border-radius: var(--xr-radius-card);
  }

  .header__menu {
    display: inline-flex;
    margin-inline-end: 12px;
  }

  .header__search {
    flex: 1;
    width: auto;
    margin-inline: 0;
  }

  .header__spacer,
  .header__user {
    display: none;
  }
}
</style>
