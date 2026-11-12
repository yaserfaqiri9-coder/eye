export const products = Array.from({ length: 108 }, (_, i) => ({
  id: i + 1, code: `DR-${String(i + 1).padStart(3, '0')}`,
  name: ['Vigamox', 'Timolol', 'Refresh Tears', 'کپسول آموکسی‌سیلین', 'تابلیت استامینوفن', 'Gentamicin', 'پماد چشمی', 'محلول شستشو'][i] || `جنس آزمایشی ${i + 1}`,
  kind: i === 7 ? 'General' : 'Drug', category: ['قطره چشمی', 'قطره چشمی', 'قطره چشمی', 'کپسول', 'تابلیت', 'آمپول/تزریقی', 'پماد', 'محلول'][i] || 'تابلیت',
  unit: 'عدد', reorderLevel: [20, 200, 20, 20, 20, 100, 10, 10][i] || 5,
  onHand: [320, 150, 210, 340, 620, 80, 0, 180, 1234.125][i] ?? 25,
  salePrice: 100, isActive: true, version: 0,
}))
export const locations = [
  { code: 'central', name: 'گدام مرکزی', productCount: 107, batchCount: 107, stockValue: 42000, lastMovementAt: null },
  { code: 'pharmacy', name: 'دواخانه', productCount: 1, batchCount: 1, stockValue: 700, lastMovementAt: null },
]
export async function setup(page) {
  const problems = []
  page.on('pageerror', e => problems.push(e.message))
  let handshake = false
  await page.route('**/hubs/**', async route => {
    const req = route.request()
    if (req.url().includes('negotiate')) return route.fulfill({ json: { negotiateVersion: 1, connectionId: 'inventory-test', connectionToken: 'inventory-test', availableTransports: [{ transport: 'LongPolling', transferFormats: ['Text','Binary'] }] } })
    if (req.method() === 'DELETE') return route.fulfill({ status: 202 })
    if (req.method() === 'POST') return route.fulfill({ status: 200, body: '' })
    if (!handshake) { handshake = true; return route.fulfill({ status: 200, contentType: 'text/plain', body: '{}\u001e' }) }
    await new Promise(r => setTimeout(r, 500))
    return route.fulfill({ status: 200, contentType: 'text/plain', body: '{"type":6}\u001e' }).catch(() => {})
  })
  await page.route('**/api/**', async route => {
    const u = new URL(route.request().url()); if (!u.pathname.startsWith('/api/')) return route.continue(); const path = u.pathname.replace('/api', '')
    let data = {}
    if (path === '/auth/me') data = { id: 1, userName: 'inventory-test', fullName: 'مدیر گدام', roles: ['Inventory'], permissions: ['inventory.view','inventory.create','inventory.edit','inventory.approve','inventory.cancel'] }
    else if (path === '/inventory/products') { const pageNumber = Number(u.searchParams.get('page') || 1); const size = Number(u.searchParams.get('pageSize') || 100); data = { items: products.slice((pageNumber-1)*size, pageNumber*size), total: products.length, page: pageNumber, pageSize: size } }
    else if (path === '/inventory/locations') data = locations
    else if (path.startsWith('/inventory/locations/')) {
      const location = locations.find(l => l.code === path.split('/').pop()) || locations[0]
      data = { location, stock: products.filter(p => p.onHand > 0 && (location.code === 'central' || p.id === 108)).map(p => ({ productId: p.id, productCode: p.code, productName: p.name, kind: p.kind, category: p.category, unit: p.unit, quantity: p.onHand, value: p.onHand*10, nearestExpiry: null, batchCount: 1 })) }
    }
    else if (path === '/inventory/alerts') data = { lowStock: [], expiring: [] }
    else if (path === '/inventory/batches' || path === '/inventory/drug-forms') data = []
    else if (path === '/inventory/movements' || path === '/inventory/purchases') data = { items: [], total: 0 }
    return route.fulfill({ status: 200, json: data })
  })
  return problems
}
