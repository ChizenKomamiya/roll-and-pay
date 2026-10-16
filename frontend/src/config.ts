export interface ApiConfig { mode: 'api'; baseUrl: string; tenantId: string; clientId: string; scope: string }
export type AppConfig = { mode: 'demo' } | ApiConfig

export function readConfig(env: Record<string, string | undefined>): AppConfig {
  if (!env.VITE_APP_MODE || env.VITE_APP_MODE === 'demo') return { mode: 'demo' }
  if (env.VITE_APP_MODE !== 'api') throw new Error('VITE_APP_MODEはdemoまたはapiを指定してください。')
  const baseUrl = env.VITE_API_BASE_URL?.trim(), tenantId = env.VITE_ENTRA_TENANT_ID?.trim()
  const clientId = env.VITE_ENTRA_SPA_CLIENT_ID?.trim(), scope = env.VITE_ENTRA_API_SCOPE?.trim()
  const guid = /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i
  if (!baseUrl || !tenantId || !clientId || !scope || !guid.test(tenantId) || !guid.test(clientId) || !/^api:\/\/[\da-f-]{36}\/access_as_user$/i.test(scope)) {
    throw new Error('API接続設定を確認してください。API URL、テナントID、SPA Client ID、APIスコープが必要です。')
  }
  const url = new URL(baseUrl)
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) || url.username || url.password || url.hash || url.search) throw new Error('API URLにはHTTPS、またはローカル開発用のURLを指定してください。')
  return { mode: 'api', baseUrl, tenantId, clientId, scope }
}
