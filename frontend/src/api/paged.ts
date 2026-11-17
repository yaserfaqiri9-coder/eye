import { computed, ref, type Ref } from 'vue'
import { api } from './http'

interface Page<T> {
  items: T[]
  total: number
}

const MAX_PAGE_SIZE = 100

/**
 * One page of a server list at a time, with the total the server counted.
 * A list that only ever asked for its first hundred rows silently hid everything older; this never does.
 * `path` is read on every load, so it may carry the page's current filters.
 */
export function usePagedList<T>(path: () => string, pageSize = 25) {
  const rows = ref<T[]>([]) as Ref<T[]>
  const total = ref(0)
  const page = ref(1)
  const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))
  let request = 0

  async function load(): Promise<void> {
    const current = ++request
    const base = path()
    const result = await api<Page<T>>(`${base}${base.includes('?') ? '&' : '?'}page=${page.value}&pageSize=${pageSize}`)
    // A slower, older answer must not replace a newer one.
    if (current !== request) return
    rows.value = result.items
    total.value = result.total
    // The last row of the last page was removed: step back instead of showing an empty page.
    if (result.items.length === 0 && page.value > 1 && result.total > 0) {
      page.value = pageCount.value
      await load()
    }
  }

  function goTo(next: number) {
    page.value = next
    return load()
  }

  /** A changed filter starts again from the first page. */
  function first() {
    page.value = 1
    return load()
  }

  return { rows, total, page, pageCount, load, goTo, first }
}

/** Every row of a list that is short by nature, such as today's unpaid invoices: totals built from it must not stop at one page. */
export async function fetchAll<T>(path: string, signal?: AbortSignal): Promise<T[]> {
  const all: T[] = []
  for (let page = 1; ; page++) {
    const result = await api<Page<T>>(`${path}${path.includes('?') ? '&' : '?'}page=${page}&pageSize=${MAX_PAGE_SIZE}`, { signal })
    all.push(...result.items)
    if (result.items.length === 0 || all.length >= result.total) return all
  }
}
