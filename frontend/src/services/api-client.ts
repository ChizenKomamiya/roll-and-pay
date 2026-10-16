/** Transport shared by the real API adapters. */
export interface ApiClientOptions {
  baseUrl: string
  getAccessToken: (forceRefresh?: boolean) => Promise<string>
  fetch?: typeof fetch
  session?: { storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>; key: string }
  onUnauthorized?: () => void
}
export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  idempotencyKey?: string
  ifMatch?: string
  signal?: AbortSignal
}
export interface ApiResponse<T> { data: T; etag: string | null; replayed: boolean }
function errorMessage(status: number, code: string, retryAfter: string | null) {
  if (code === 'ACTIVE_GAME_EXISTS') return '進行中のゲームがあります。ホームから再開してください。'
  if (status === 429 || status === 503) return `一時的に利用できません。${retryAfter ? `${retryAfter}秒ほど待ってから` : '少し待ってから'}再度お試しください。`
  const messages: Record<number, string> = {
    401: 'ログインし直してください。', 403: 'このアカウントでは利用できません。',
    404: '記録が見つかりません。', 410: 'この履歴は削除されています。',
    409: '現在の状態では操作できません。最新の内容を確認してください。',
    412: '記録が更新されました。最新の内容を確認してください。', 422: '入力値を確認してください。',
  }
  return messages[status] ?? 'APIへの要求に失敗しました。結果を確認し、再度お試しください。'
}
export class ApiError extends Error {
  status: number
  code: string
  retryAfter: string | null
  constructor(status: number, code: string, retryAfter: string | null) {
    super(errorMessage(status, code, retryAfter))
    this.name = 'ApiError'; this.status = status; this.code = code; this.retryAfter = retryAfter
  }
}

export function createApiClient(options: ApiClientOptions) {
  const base = new URL(options.baseUrl.endsWith('/') ? options.baseUrl : `${options.baseUrl}/`)
  if (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(base.hostname))) throw new Error('APIはHTTPSまたはローカル開発用URLを指定してください。')
  if (base.username || base.password || base.search || base.hash) throw new Error('API Base URLの形式が不正です。')
  let readSession: string | null = null
  try { readSession = options.session?.storage.getItem(options.session.key) ?? null } catch { /* Continue with a fresh session. */ }
  let epoch = 0
  let queue: Promise<unknown> = Promise.resolve()
  const fetcher = options.fetch ?? fetch
  async function send<T>(path: string, request: ApiRequestOptions, requestEpoch: number): Promise<ApiResponse<T>> {
      if (requestEpoch !== epoch) throw new Error('ログイン状態が変わりました。')
      const url = new URL(path.replace(/^\//, ''), base)
      if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) throw new Error('APIパスが範囲外です。')
      let token = await options.getAccessToken()
      if (requestEpoch !== epoch) throw new Error('ログイン状態が変わりました。')
      if (!token) throw new Error('APIアクセストークンを取得できませんでした。')
      const headers = new Headers({ Authorization: `Bearer ${token}`, Accept: 'application/json' })
      if (request.body !== undefined) headers.set('Content-Type', 'application/json')
      if (request.idempotencyKey) headers.set('Idempotency-Key', request.idempotencyKey)
      if (request.ifMatch) headers.set('If-Match', request.ifMatch)
      if (readSession) headers.set('X-Read-Session', readSession)
      const init: RequestInit = { method: request.method ?? 'GET', headers, body: request.body === undefined ? undefined : JSON.stringify(request.body), signal: request.signal ?? AbortSignal.timeout(30_000), cache: 'no-store', credentials: 'omit', redirect: 'error' }
      let response = await fetcher(url, init)
      // Retry only a rejected token, once, retaining the exact body and command key.
      if (response.status === 401 && requestEpoch === epoch) {
        token = await options.getAccessToken(true)
        if (requestEpoch !== epoch) throw new Error('ログイン状態が変わりました。')
        if (!token) throw new Error('APIアクセストークンを取得できませんでした。')
        headers.set('Authorization', `Bearer ${token}`)
        response = await fetcher(url, init)
      }
      if (requestEpoch !== epoch) throw new Error('ログイン状態が変わりました。')
      const nextSession = response.headers.get('X-Read-Session')
      if (nextSession) {
        readSession = nextSession
        try { options.session?.storage.setItem(options.session.key, nextSession) } catch { /* In-memory inheritance remains available. */ }
      }
      if (!response.ok) {
        let code = 'API_ERROR'
        try { const body = await response.json(); if (typeof body.error?.code === 'string') code = body.error.code } catch { /* The HTTP status remains usable for non-JSON errors. */ }
        if (response.status === 401) options.onUnauthorized?.()
        throw new ApiError(response.status, code, response.headers.get('Retry-After'))
      }
      return { data: response.status === 204 ? undefined as T : await response.json() as T, etag: response.headers.get('ETag'), replayed: response.headers.get('Idempotency-Replayed') === 'true' }
  }
  return {
    clearSession() {
      epoch++; readSession = null
      try { options.session?.storage.removeItem(options.session.key) } catch { /* Memory is cleared. */ }
    },
    request<T>(path: string, request: ApiRequestOptions = {}): Promise<ApiResponse<T>> {
      const requestEpoch = epoch
      const result = queue.then(() => send<T>(path, request, requestEpoch))
      queue = result.catch(() => undefined)
      return result
    },
  }
}
export type ApiClient = ReturnType<typeof createApiClient>
