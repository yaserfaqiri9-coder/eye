import { ref } from 'vue'
import type { Ref } from 'vue'

export interface ChartTip {
  x: number
  y: number
  title: string
  lines: string[]
}

/** Five round steps from zero, so an empty or small chart keeps a readable axis. */
export function niceTicks(max: number, steps = 5): number[] {
  const safeMax = Math.max(max, steps)
  const rough = safeMax / steps
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const unit = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((candidate) => candidate >= rough) ?? rough
  return Array.from({ length: steps + 1 }, (_, i) => i * unit)
}

/** Short "12 عقرب" label for a yyyy-MM-dd date. It is a calendar date, so it is formatted in UTC to stop it shifting a day. */
export function dayLabel(iso: string) {
  return new Intl.DateTimeFormat('fa-AF-u-ca-persian', { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(`${iso}T00:00:00Z`))
}

export function useChartTip(): { tip: Ref<ChartTip | null>; show: (event: MouseEvent, title: string, lines: string[]) => void; hide: () => void } {
  const tip = ref<ChartTip | null>(null)

  function show(event: MouseEvent, title: string, lines: string[]) {
    const box = (event.currentTarget as Element).closest('.chart')?.getBoundingClientRect()
    if (!box) return
    tip.value = { x: event.clientX - box.left, y: event.clientY - box.top, title, lines }
  }

  return { tip, show, hide: () => (tip.value = null) }
}
