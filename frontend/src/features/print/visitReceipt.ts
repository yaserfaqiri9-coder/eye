/** Reserve the tab during the click so an asynchronous save cannot trigger the popup blocker. */
export function prepareVisitReceipt(allowed: boolean) {
  const tab = allowed ? window.open('about:blank', '_blank') : null
  if (tab) {
    tab.opener = null
    tab.document.title = 'آماده‌سازی رسید نوبت'
    tab.document.body.textContent = 'در حال ثبت مراجعه و آماده‌سازی رسید نوبت…'
    tab.document.body.dir = 'rtl'
  }
  return {
    open(visitId: number, queueNumber: number | null) {
      if (!tab || tab.closed || queueNumber === null) {
        tab?.close()
        return false
      }
      tab.location.replace(`/print/visit/${visitId}?autoPrint=1`)
      return true
    },
    close() { tab?.close() },
  }
}
