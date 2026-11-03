import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'

/** Warns before leaving a page whose reactive form has unsaved edits. Call `markSaved` after saving. */
export function useUnsavedGuard(source: object) {
  const dirty = ref(false)
  let ignoring = true

  watch(
    source,
    () => {
      if (!ignoring) dirty.value = true
    },
    { deep: true, flush: 'sync' },
  )

  function markSaved() {
    dirty.value = false
  }

  /** Applies a server value without counting it as an edit. */
  function applying(change: () => void) {
    ignoring = true
    change()
    dirty.value = false
    ignoring = false
  }

  function warnOnClose(event: BeforeUnloadEvent) {
    if (dirty.value) event.preventDefault()
  }

  onMounted(() => {
    ignoring = false
    window.addEventListener('beforeunload', warnOnClose)
  })
  onBeforeUnmount(() => window.removeEventListener('beforeunload', warnOnClose))

  onBeforeRouteLeave(() => !dirty.value || window.confirm('تغییرات ذخیره نشده‌اند. از این صفحه خارج شوید؟'))

  return { dirty, markSaved, applying }
}
