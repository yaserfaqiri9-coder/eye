<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/http'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const userName = ref('')
const password = ref('')
const error = ref('')
const submitting = ref(false)

async function submit() {
  submitting.value = true
  error.value = ''
  try {
    await auth.login(userName.value, password.value)
    const next = route.query.next
    // Only same-site paths are followed, so the link cannot send a user to another site.
    const target = typeof next === 'string' && next.startsWith('/') && !next.startsWith('//') ? next : '/'
    await router.push(target)
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : 'ورود انجام نشد. دوباره تلاش کنید.'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login">
    <div class="login__frame">
      <div class="login__panel">
        <i class="bi bi-eye-fill login__logo" aria-hidden="true"></i>
        <h1 class="login__brand">شفاخانه تخصصی چشم الشفا</h1>
        <p class="login__sub">سیستم مدیریت شفاخانه</p>
      </div>
      <form class="login__card" @submit.prevent="submit">
        <h2 class="login__title">ورود</h2>
        <p class="login__hint">نام کاربری و رمز خود را وارد کنید.</p>
        <div class="mb-3">
          <label class="form-label" for="login-user">نام کاربری</label>
          <input id="login-user" v-model="userName" class="form-control" dir="ltr" autocomplete="username" required />
        </div>
        <div class="mb-3">
          <label class="form-label" for="login-pass">رمز عبور</label>
          <input
            id="login-pass"
            v-model="password"
            class="form-control"
            type="password"
            dir="ltr"
            autocomplete="current-password"
            required
          />
        </div>
        <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
        <button type="submit" class="btn btn-primary w-100" :disabled="submitting">ورود</button>
      </form>
    </div>
  </div>
</template>

<style scoped>
.login {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-surface);
}

.login__frame {
  display: flex;
  align-items: center;
  width: 100%;
  max-width: 900px;
}

.login__panel {
  flex: 1;
  min-height: 480px;
  padding: 60px 40px 60px 120px;
  border-radius: var(--xr-radius-card);
  background: var(--xr-primary);
  color: #fff;
  text-align: center;
}

.login__logo {
  font-size: 56px;
}

.login__brand {
  margin-top: 20px;
  font-size: 22px;
  font-weight: 500;
}

.login__sub {
  opacity: 0.85;
}

.login__card {
  width: 400px;
  flex: none;
  margin-inline-start: -90px;
  padding: 35px;
  border-radius: var(--xr-radius-card);
  background: var(--xr-surface);
  box-shadow: 0 0 30px rgba(8, 158, 175, 0.12);
}

.login__title {
  font-size: 32px;
  font-weight: 400;
}

.login__hint {
  color: var(--xr-text-muted);
}

@media (max-width: 767.98px) {
  .login__panel {
    display: none;
  }

  .login__card {
    width: 100%;
    margin: 0;
  }
}
</style>
