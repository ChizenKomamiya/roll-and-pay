/** Transport for the later API adapter. It is not invoked in demo mode. */
export interface ApiClientOptions {
  baseUrl: string
  getAccessToken: () => Promise<string>
  fetch?: typeof fetch
}
export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  idempotencyKey?: string
  ifMatch?: string
  signal?: AbortSignal
}
export interface ApiResponse<T> { data: T; etag: string | null; replayed: boolean }
export class ApiError extends Error {
  status: number
  code: string
  retryAfter: string | null
  constructor(status: number, code: string, retryAfter: string | null) {
    super(status === 401 ? 'ログインし直してください。' : status === 403 ? 'このアカウントでは利用できません。' : status === 410 ? 'この履歴は削除されています。' : status === 412 ? '記録が更新されました。最新の内容を確認してください。' : 'APIへの要求に失敗しました。')
    this.name = 'ApiError'; this.status = status; this.code = code; this.retryAfter = retryAfter
  }
}

export function createApiClient(options: ApiClientOptions) {
  const base = new URL(options.baseUrl.endsWith('/') ? options.baseUrl : `${options.baseUrl}/`)
  if (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(base.hostname))) throw new Error('APIはHTTPSまたはローカル開発用URLを指定してください。')
  if (base.username || base.password || base.search || base.hash) throw new Error('API Base URLの形式が不正です。')
  let readSession: string | null = null
  const fetcher = options.fetch ?? fetch
  return {
    clearSession() { readSession = null },
    async request<T>(path: string, request: ApiRequestOptions = {}): Promise<ApiResponse<T>> {
      const url = new URL(path.replace(/^\//, ''), base)
      if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) throw new Error('APIパスが範囲外です。')
      const token = await options.getAccessToken()
      if (!token) throw new Error('APIアクセストークンを取得できませんでした。')
      const headers = new Headers({ Authorization: `Bearer ${token}`, Accept: 'application/json' })
      if (request.body !== undefined) headers.set('Content-Type', 'application/json')
      if (request.idempotencyKey) headers.set('Idempotency-Key', request.idempotencyKey)
      if (request.ifMatch) headers.set('If-Match', request.ifMatch)
      if (readSession) headers.set('X-Read-Session', readSession)
      // No automatic mutation retry. The future adapter must keep the SAME key/body/ETag,
      // and explicitly handle authorization refresh, 412 conflicts and replayed old responses.
      const response = await fetcher(url, { method: request.method ?? 'GET', headers, body: request.body === undefined ? undefined : JSON.stringify(request.body), signal: request.signal, cache: 'no-store', credentials: 'omit', redirect: 'error' })
      const nextSession = response.headers.get('X-Read-Session')
      if (nextSession) readSession = nextSession
      if (!response.ok) {
        let code = 'API_ERROR'
        try { const body = await response.json(); if (typeof body.error?.code === 'string') code = body.error.code } catch { /* The HTTP status remains usable for non-JSON errors. */ }
        throw new ApiError(response.status, code, response.headers.get('Retry-After'))
      }
      return { data: response.status === 204 ? undefined as T : await response.json() as T, etag: response.headers.get('ETag'), replayed: response.headers.get('Idempotency-Replayed') === 'true' }
    },
  }
}
