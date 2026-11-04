import { computed, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import { useAuthStore } from '@/stores/auth'
import { MissingClipError, sharedPlayer } from './clipPlayer'

export type CallDeskName = 'Reception' | 'Vision' | 'Doctor' | 'Refraction' | 'Imaging' | 'Lab' | 'Pharmacy' | 'Optical'

export interface CallRow {
  id: number
  queueNumber: number
  destination: string
  isRecall: boolean
  status: 'Queued' | 'Playing' | 'Played' | 'Failed' | 'Cancelled'
  outcome: 'Attended' | 'NoShow' | null
  failureReason: string | null
  requestedAt: string
  finishedAt: string | null
  localClips?: string[] | null
}

export interface CallQueueRow {
  visitId: number | null
  tokenId: number | null
  queueNumber: number
  lastCall: CallRow | null
}

const permissions: Record<CallDeskName, string> = {
  Reception: 'visitfees.create',
  Vision: 'preexam.edit',
  Doctor: 'consultation.edit',
  Refraction: 'preexam.edit',
  Imaging: 'diagnostics.edit',
  Lab: 'diagnostics.edit',
  Pharmacy: 'pharmacy.create',
  Optical: 'optical.create',
}

// Any of these can move a patient in or out of a desk's queue.
const refreshOn = [
  'PatientCallChanged',
  'VisitCreated',
  'VisitStatusChanged',
  'PreExamCompleted',
  'ConsultationCompleted',
  'DiagnosticOrderCreated',
  'DiagnosticResultReady',
  'PaymentReceived',
  'PrescriptionCreated',
  'OpticalPrescriptionCreated',
  'OpticalOrderChanged',
  'InventoryChanged',
]

/** One desk's queue as the server sees it. A row with no entry here is not callable, whatever the page shows. */
export function useCallDesk(desk: CallDeskName) {
  const enabled = useAuthStore().can(permissions[desk])
  const rows = ref<CallQueueRow[]>([])
  const stationOnline = ref(true)
  const error = ref('')
  const loadError = ref('')
  // Per row: a press on one patient must not swallow a press on the next.
  const sending = ref<CallQueueRow[]>([])

  let loading = false

  async function load() {
    if (!enabled || loading) return
    loading = true
    try {
      const view = await api<{ stationOnline: boolean; rows: CallQueueRow[] }>(`/calls/desk/${desk}`)
      rows.value = view.rows
      stationOnline.value = view.stationOnline
      loadError.value = ''
    } catch (cause) {
      loadError.value = errorMessage(cause)
    } finally {
      loading = false
    }
  }

  function entry(visitId?: number | null, tokenId?: number | null) {
    return rows.value.find((row) => (tokenId ? row.tokenId === tokenId : row.visitId === visitId))
  }

  const isSending = (row: CallQueueRow) => sending.value.some((r) => r.visitId === row.visitId && r.tokenId === row.tokenId)

  /** No station was online, so this computer announces it. The result is reported either way so the desk sees what was heard. */
  async function playHere(call: CallRow, clips: string[]) {
    let reason: string | null = null
    try {
      await sharedPlayer.play(clips)
    } catch (cause) {
      reason = cause instanceof MissingClipError ? cause.message : 'پخش صدا در این رایانه ناموفق بود. بلندگوی رایانه را بررسی کنید.'
      error.value = reason
    }
    await api(`/calls/${call.id}/local-result`, { method: 'POST', body: { played: reason === null, reason } })
  }

  async function act(row: CallQueueRow, path: string, body: unknown) {
    sending.value.push(row)
    error.value = ''
    try {
      const result = await api<CallRow | undefined>(path, { method: 'POST', body })
      if (result?.localClips?.length) await playHere(result, result.localClips)
    } catch (cause) {
      error.value = errorMessage(cause)
    } finally {
      sending.value = sending.value.filter((r) => r.visitId !== row.visitId || r.tokenId !== row.tokenId)
    }
    await load()
  }

  function call(row: CallQueueRow, recall: boolean) {
    // Still inside the click: browsers allow sound only from one, and the answer below decides whether it plays here.
    void sharedPlayer.unlock()
    return act(row, '/calls', { desk, visitId: row.visitId, tokenId: row.tokenId, recall })
  }

  const mark = (row: CallQueueRow, outcome: 'Attended' | 'NoShow') => act(row, `/calls/${row.lastCall!.id}/outcome`, { outcome })

  // Station activation does not emit a patient event; poll its heartbeat as well as live queue changes.
  useLiveRefresh(enabled ? refreshOn : [], () => { if (enabled) void load() }, 15_000)
  onMounted(load)

  return { enabled, rows, stationOnline, error: computed(() => error.value || loadError.value), isSending, load, entry, call, mark }
}

export type CallDeskHandle = ReturnType<typeof useCallDesk>
