import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, ApiError } from '@/api/http'
import { clearLookups } from '@/api/lookups'

export interface CurrentUser {
  id: number
  userName: string
  fullName: string
  roles: string[]
  permissions: string[]
  /** Sidebar pages an administrator hid for this user. Display only: the permissions stay. */
  hiddenMenu?: string[]
  /** The online copy: everything can be viewed, nothing changed. */
  readOnly?: boolean
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<CurrentUser | null>(null)
  const loaded = ref(false)

  const permissions = computed(() => new Set(user.value?.permissions ?? []))

  function can(permission: string) {
    return permissions.value.has(permission)
  }

  async function load() {
    if (loaded.value) return
    try {
      user.value = await api<CurrentUser>('/auth/me')
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401) throw error
      user.value = null
    }
    loaded.value = true
  }

  async function login(userName: string, password: string, code?: string) {
    clearLookups()
    user.value = await api<CurrentUser>('/auth/login', { method: 'POST', body: { userName, password, code: code || undefined } })
    loaded.value = true
  }

  async function logout() {
    await api('/auth/logout', { method: 'POST' })
    clear()
  }

  function clear() {
    clearLookups()
    user.value = null
    loaded.value = true
  }

  return { user, loaded, can, load, login, logout, clear }
})
