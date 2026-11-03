export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly problem: Record<string, unknown> | null = null,
  ) {
    super(message)
  }

  /** True when the request may or may not have been applied on the server. */
  get outcomeUnknown() {
    return this.code === 'outcome_unknown'
  }
}

export function errorMessage(cause: unknown) {
  return cause instanceof ApiError ? cause.message : 'خطایی رخ داد. دوباره تلاش کنید.'
}

/** One key per form submission attempt; reuse it when retrying so the server applies the action once. */
export function newIdempotencyKey() {
  return crypto.randomUUID().replaceAll('-', '')
}

function readCookie(name: string) {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
  return match ? decodeURIComponent(match[1]) : null
}

async function ensureCsrfToken() {
  const existing = readCookie('XSRF-TOKEN')
  if (existing) return existing
  await fetch('/api/auth/csrf', { credentials: 'same-origin' })
  return readCookie('XSRF-TOKEN') ?? ''
}

let onUnauthorized: () => void = () => {}

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

interface RequestOptions {
  method?: string
  body?: unknown
  signal?: AbortSignal
  idempotencyKey?: string
}

export async function uploadFile<T>(path: string, form: FormData): Promise<T> {
  const token = await ensureCsrfToken()
  let response: Response
  try {
    response = await fetch(`/api${path}`, { method: 'POST', headers: { 'X-XSRF-TOKEN': token }, credentials: 'same-origin', body: form })
  } catch {
    throw new ApiError(0, 'outcome_unknown', 'ارتباط قطع شد و معلوم نیست فایل ذخیره شده یا نه. پیش از تکرار، فهرست فایل‌ها را بررسی کنید.')
  }
  if (!response.ok) throw await toApiError(response)
  return (await response.json()) as T
}

async function send(path: string, options: RequestOptions) {
  const method = options.method ?? 'GET'
  const headers: Record<string, string> = {}
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET') headers['X-XSRF-TOKEN'] = await ensureCsrfToken()
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey

  return fetch(`/api${path}`, {
    method,
    headers,
    credentials: 'same-origin',
    signal: options.signal,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  })
}

async function toApiError(response: Response) {
  const problem = await response.json().catch(() => null)
  if (response.status === 403) {
    // A refusal from a rule says what to do next, such as handing a patient over or asking for emergency access.
    if (problem?.code && problem.code !== 'forbidden') return new ApiError(403, problem.code, problem.title, problem)
    return new ApiError(403, 'forbidden', 'شما اجازه این عملیات را ندارید.', problem)
  }
  if (response.status === 429) {
    return new ApiError(429, 'rate_limited', 'تعداد تلاش‌ها زیاد است. کمی بعد دوباره تلاش کنید.', problem)
  }
  // Field validation failures have no code and an English framework title.
  if (problem?.errors) {
    return new ApiError(response.status, 'validation', 'اطلاعات فورم کامل یا درست نیست. لطفاً بررسی کنید.', problem)
  }
  return new ApiError(
    response.status,
    problem?.code ?? 'error',
    problem?.code ? problem.title : 'خطایی رخ داد. دوباره تلاش کنید.',
    problem,
  )
}

export async function api<T = void>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method ?? 'GET'
  let response: Response
  try {
    response = await send(path, options)

    // An invalid CSRF token is rejected before any work happens, so one retry with a fresh token is safe.
    if (response.status === 400 && method !== 'GET') {
      const error = await toApiError(response.clone())
      if (error.code === 'csrf_invalid') {
        document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/'
        response = await send(path, options)
      }
    }
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    if (method === 'GET') {
      throw new ApiError(0, 'network', 'اتصال با سرور برقرار نشد. اتصال شبکه را بررسی کنید.')
    }
    throw new ApiError(
      0,
      'outcome_unknown',
      'ارتباط قطع شد و معلوم نیست این عملیات ثبت شده یا نه. پیش از تکرار، صفحه را تازه کنید و بررسی کنید.',
    )
  }

  if (response.status === 401 && path !== '/auth/login' && path !== '/auth/me') {
    onUnauthorized()
  }

  if (!response.ok) throw await toApiError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}
