import { api } from '@/api/http'

/**
 * The delete action of a list. The server removes an unused record and deactivates one that history still needs.
 * Returns what happened to tell the user, or an empty string if they changed their mind.
 */
export async function removeRecord(path: string, label: string): Promise<string> {
  if (!window.confirm(`«${label}» حذف شود؟\nاگر در سوابق به کار رفته باشد، به‌جای حذف غیرفعال می‌شود.`)) return ''
  const outcome = await api<{ deleted: boolean }>(path, { method: 'DELETE' })
  return outcome.deleted ? `«${label}» حذف شد.` : `«${label}» در سوابق به کار رفته است و به‌جای حذف، غیرفعال شد.`
}
