<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import UiAvatar from '@/components/UiAvatar.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { roleLabel } from '@/features/users/roles'
import { patientName, type PatientListItem } from '@/features/patients/types'

defineEmits<{ 'toggle-sidebar': [] }>()

const auth = useAuthStore()
const router = useRouter()
const error = ref('')
const search = ref('')
const stuck = ref(false)
let scrollContainer: HTMLElement | null = null

/** Once the page scrolls, the floating bar snaps to the top edge and spans the content, as in the template. */
function onScroll() {
  stuck.value = (scrollContainer?.scrollTop ?? window.scrollY) > 0
}

onMounted(() => {
  scrollContainer = document.querySelector<HTMLElement>('.layout__main')
  onScroll()
  ;(scrollContainer ?? window).addEventListener('scroll', onScroll, { passive: true })
})

onBeforeUnmount(() => (scrollContainer ?? window).removeEventListener('scroll', onScroll))

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

/** A few matching patients appear while typing; one click opens the file. Enter without a choice still opens the full list. */
const hits = ref<PatientListItem[]>([])
const hitsOpen = ref(false)
const activeHit = ref(-1)
let hitTimer: ReturnType<typeof setTimeout> | undefined
let hitController: AbortController | null = null

function onSearchInput() {
  clearTimeout(hitTimer)
  hitController?.abort()
  activeHit.value = -1
  const term = search.value.trim()
  // One digit is a whole file number now, so it searches; one letter would find too much.
  if (term.length < 2 && !/^[0-9۰-۹]$/.test(term)) {
    hits.value = []
    hitsOpen.value = false
    return
  }
  hitTimer = setTimeout(async () => {
    const controller = new AbortController()
    hitController = controller
    try {
      const page = await api<{ items: PatientListItem[] }>(`/patients?pageSize=6&q=${encodeURIComponent(term)}`, { signal: controller.signal })
      if (controller.signal.aborted) return
      hits.value = page.items
      // A late answer must not open a list the user has already left or submitted.
      hitsOpen.value = document.activeElement === searchInput.value
    } catch {
      // The list is a shortcut only; Enter still searches on the patients page.
      if (!controller.signal.aborted) hitsOpen.value = false
    }
  }, 250)
}

const searchInput = ref<HTMLInputElement | null>(null)

function closeHits() {
  hitsOpen.value = false
  activeHit.value = -1
}

/** Stops a suggestion search still on its way, so nothing reopens after the user has moved on. */
function cancelHits() {
  clearTimeout(hitTimer)
  hitController?.abort()
  closeHits()
}

function openHit(row: PatientListItem) {
  cancelHits()
  search.value = ''
  hits.value = []
  router.push(`/patients/${row.id}`)
}

function onSearchKeydown(event: KeyboardEvent) {
  if (!hitsOpen.value || hits.value.length === 0) return
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    const count = hits.value.length
    if (event.key === 'ArrowDown') activeHit.value = (activeHit.value + 1) % count
    else activeHit.value = activeHit.value <= 0 ? count - 1 : activeHit.value - 1
  } else if (event.key === 'Escape') {
    cancelHits()
  }
}

function submitSearch() {
  const chosen = hits.value[activeHit.value]
  if (hitsOpen.value && chosen) return openHit(chosen)
  cancelHits()
  router.push({ name: 'patients', query: { q: search.value.trim() } })
}

onBeforeUnmount(() => {
  clearTimeout(hitTimer)
  hitController?.abort()
})

/** Leaving ends the session for everyone at this desk, so it asks first: the icon sits beside «تغییر رمز». */
const confirmingLogout = ref(false)

async function logout() {
  confirmingLogout.value = false
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
      <input
        ref="searchInput"
        v-model="search"
        type="search"
        class="form-control"
        placeholder="جستجوی مریض: نام، پرونده یا تماس"
        aria-label="جستجوی مریض در هدر"
        autocomplete="off"
        aria-controls="header-search-hits"
        :aria-expanded="hitsOpen"
        :aria-activedescendant="hitsOpen && activeHit >= 0 ? `header-hit-${activeHit}` : undefined"
        @input="onSearchInput"
        @keydown="onSearchKeydown"
        @blur="cancelHits"
      />
      <i class="ri-search-line" aria-hidden="true"></i>
      <div v-if="hitsOpen" id="header-search-hits" class="header__hits xr-popover" role="listbox" aria-label="مریضان پیدا شده" @mousedown.prevent>
        <button
          v-for="(row, index) in hits"
          :id="`header-hit-${index}`"
          :key="row.id"
          type="button"
          role="option"
          class="header__hit"
          :class="{ 'header__hit--active': index === activeHit }"
          :aria-selected="index === activeHit"
          @mousemove="activeHit = index"
          @click="openHit(row)"
        >
          <span class="header__hit-name">{{ patientName(row) }}</span>
          <span class="header__hit-meta"><span class="ltr-num">{{ row.fileNumber }}</span><template v-if="row.fatherName"> · پدر: {{ row.fatherName }}</template></span>
        </button>
        <p v-if="hits.length === 0" class="xr-combo__note">مریضی پیدا نشد.</p>
        <p v-else class="xr-combo__note">Enter: همه نتایج در فهرست مریضان</p>
      </div>
    </form>
    <div class="header__spacer"></div>
    <span v-if="error" class="text-danger small" role="alert">{{ error }}</span>
    <button type="button" class="header__icon" aria-label="تمام صفحه" title="تمام صفحه" @click="toggleFullscreen">
      <i class="ri-fullscreen-line" aria-hidden="true"></i>
    </button>
    <button type="button" class="header__icon" aria-label="تغییر رمز" title="تغییر رمز" @click="Object.assign(password, { open: true, current: '', next: '', error: '', done: false })">
      <i class="ri-key-2-fill" aria-hidden="true"></i>
    </button>
    <button type="button" class="header__icon" aria-label="خروج" title="خروج" @click="confirmingLogout = true">
      <i class="ri-logout-box-line" aria-hidden="true"></i>
    </button>
    <div v-if="auth.user" class="header__user">
      <UiAvatar :seed="auth.user.fullName" :size="38" plain />
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
      <label class="form-label" for="pw-next">رمز جدید</label>
      <input id="pw-next" v-model="password.next" class="form-control mb-3" type="password" dir="ltr" autocomplete="new-password" required />
      <div v-if="password.error" class="alert alert-danger" role="alert">{{ password.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="password.saving">تغییر رمز</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="confirmingLogout" title="خروج از سیستم" @close="confirmingLogout = false">
    <p>از حساب {{ auth.user?.fullName }} خارج می‌شوید. کار ذخیره‌نشده در صفحه باز از بین می‌رود.</p>
    <div class="xr-form-actions">
      <button type="button" class="btn btn-primary" @click="logout">خروج</button>
      <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
    </div>
  </UiModal>
</template>

<style scoped>
.header {
  position: fixed;
  top: var(--xr-header-top);
  inset-inline: calc(var(--xr-sidebar-current) + var(--xr-page-gutter)) var(--xr-page-gutter);
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
  border: 1px solid var(--xr-border-table);
  background: var(--xr-surface);
  color: #000;
  line-height: 25.2px;
}

.header__search .form-control:focus {
  border-color: var(--xr-primary);
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

.header__hits {
  position: absolute;
  inset-block-start: calc(100% + 6px);
  inset-inline: 0;
  padding: 4px;
}

.header__hit {
  display: flex;
  flex-direction: column;
  width: 100%;
  padding: 6px 10px;
  border: 0;
  border-radius: var(--xr-radius-button);
  background: transparent;
  color: var(--xr-text);
  text-align: start;
}

.header__hit--active {
  background: var(--xr-primary-light);
}

.header__hit-name {
  font-weight: 500;
}

.header__hit-meta {
  color: var(--xr-text-soft);
  font-size: 12.5px;
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
  font-size: 15px;
  font-weight: 500;
  line-height: 22px;
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
