<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/http'
import ClinicIllustration from '@/features/home/ClinicIllustration.vue'
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
        <div class="login__brand">
          <i class="ri-eye-fill" aria-hidden="true"></i>
          <span>الشفا</span>
        </div>
        <div class="login__picture" aria-hidden="true"><ClinicIllustration /></div>
        <h4 class="login__heading">شفاخانه تخصصی چشم الشفا</h4>
        <p class="login__sub">سیستم مدیریت شفاخانه</p>
      </div>
      <form class="login__card" @submit.prevent="submit">
        <h1 class="login__title">ورود</h1>
        <p>نام کاربری و رمز خود را وارد کنید.</p>
        <div class="login__field">
          <label class="form-label" for="login-user">نام کاربری</label>
          <input id="login-user" v-model="userName" class="form-control" dir="ltr" autocomplete="username" required />
        </div>
        <div class="login__field">
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
        <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
        <div class="login__actions">
          <button type="submit" class="btn btn-primary" :disabled="submitting">ورود</button>
        </div>
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
  padding: 48px 16px;
  overflow: hidden;
  background: var(--xr-page-bg);
}

/* A teal panel with a white form overlapping its far edge, mirrored from the template. */
.login__frame {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  width: 100%;
  max-width: 1320px;
  min-height: 812px;
}

.login__panel {
  position: absolute;
  inset-block: 0;
  inset-inline-start: 0;
  width: 1050px;
  padding: 100px 100px 0 502px;
  border-radius: 40px;
  background-image: linear-gradient(to left, var(--xr-primary) 0%, var(--xr-primary-end) 100%);
  color: #fff;
  text-align: center;
}

.login__brand {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  height: 100px;
  margin-bottom: 48px;
  font-size: 34px;
  font-weight: 700;
}

.login__brand i {
  font-size: 48px;
}

.login__picture {
  height: 315px;
  overflow: hidden;
  border-radius: 12px;
  background: var(--xr-surface);
}

.login__picture :deep(svg) {
  display: block;
  width: 100%;
  height: 100%;
}

.login__heading {
  margin-top: 26px;
  color: #fff;
}

.login__sub {
  margin: 4px 0 0;
  color: #fff;
  font-weight: 500;
}

.login__card {
  position: relative;
  width: 660px;
  min-height: 649px;
  padding: 20px 60px;
  border-start-start-radius: 37px;
  border-end-start-radius: 37px;
  border-start-end-radius: 20px;
  border-end-end-radius: 20px;
  background: var(--xr-surface);
}

.login__title {
  line-height: 64px;
}

.login__title + p {
  margin-bottom: 24px;
}

.login__field {
  margin-bottom: 16px;
}

.login__actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}

@media (max-width: 1399.98px) {
  .login__frame {
    max-width: 1100px;
    min-height: 700px;
  }

  .login__panel {
    width: 880px;
    padding: 60px 60px 0 440px;
  }

  .login__picture {
    height: 260px;
  }

  .login__card {
    width: 520px;
    min-height: 560px;
    padding: 20px 40px;
  }
}

@media (max-width: 991.98px) {
  .login__frame {
    display: block;
    max-width: 480px;
    min-height: 0;
  }

  .login__panel {
    display: none;
  }

  .login__card {
    width: 100%;
    min-height: 0;
    padding: 24px;
    border-radius: var(--xr-radius-card);
  }
}
</style>
