import { createApp } from 'vue'
import { createPinia } from 'pinia'

import 'vazirmatn/Vazirmatn-font-face.css'
import 'bootstrap/dist/css/bootstrap.rtl.min.css'
import 'bootstrap-icons/font/bootstrap-icons.min.css'
import '@/design/tokens.css'
import '@/design/base.css'

import App from './App.vue'
import { setUnauthorizedHandler } from './api/http'
import { router } from './router'
import { useAuthStore } from './stores/auth'

declare global {
  interface Window {
    __alshifaBooted?: boolean
  }
}

const app = createApp(App).use(createPinia()).use(router)

// A session ended on the server (expired or deactivated) sends the user back to sign-in.
setUnauthorizedHandler(() => {
  useAuthStore().clear()
  router.push({ name: 'login' })
})

// The loading message in index.html stays visible until the first page is settled.
router
  .isReady()
  .catch(() => {})
  .finally(() => {
    app.mount('#app')
    window.__alshifaBooted = true
  })
