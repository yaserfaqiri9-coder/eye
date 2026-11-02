// Plain script on purpose: it must still run when the app bundle fails or the browser is too old for it.
// Tells the user what went wrong instead of leaving a blank page.
(function () {
  var TIMEOUT_MS = 15000

  function show() {
    if (window.__alshifaBooted) return
    var box = document.getElementById('boot')
    if (!box || box.getAttribute('data-failed')) return
    box.setAttribute('data-failed', '1')
    box.innerHTML =
      '<p style="font-size:18px;margin:0 0 8px">سیستم بارگذاری نشد.</p>' +
      '<p style="margin:0 0 16px">از روشن بودن سیستم مطمئن شوید و از نسخه به‌روز Chrome یا Edge استفاده کنید. اگر مشکل ماند، این پیام را به مسئول سیستم بگویید.</p>' +
      '<button id="boot-retry" type="button" style="padding:8px 20px;border:0;border-radius:10px;background:#089bab;color:#fff;font:inherit;cursor:pointer">تلاش دوباره</button>'
    document.getElementById('boot-retry').addEventListener('click', function () {
      window.location.reload()
    })
  }

  // Capture phase, because a failed script or stylesheet does not bubble its error to window.
  window.addEventListener('error', function (event) {
    var target = event.target
    var isResource = target && (target.tagName === 'SCRIPT' || target.tagName === 'LINK')
    if (isResource || target === window) show()
  }, true)

  window.addEventListener('unhandledrejection', show)
  setTimeout(show, TIMEOUT_MS)
})()
