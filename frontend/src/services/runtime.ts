import type { AppConfig } from '../config'
import type { GameService } from './game-service'
import { createMockService } from './mock-service'

const DEMO_SESSION = 'roll-and-pay.demo.session'
export interface AppRuntime {
  mode: 'demo' | 'api'
  service: GameService
  signedIn: boolean
  message?: string
  onSessionExpired?: () => void
  login(): Promise<void>
  logout(): Promise<void>
}

export async function createRuntime(config: AppConfig): Promise<AppRuntime> {
  if (config.mode === 'demo') {
    let signedIn = false
    try { signedIn = sessionStorage.getItem(DEMO_SESSION) === 'active' } catch { /* A memory-only demo is usable. */ }
    return {
      mode: 'demo', service: createMockService({ getItem: key => localStorage.getItem(key), setItem: (key, value) => localStorage.setItem(key, value) }), signedIn,
      async login() { try { sessionStorage.setItem(DEMO_SESSION, 'active') } catch { /* Memory-only sign-in. */ } },
      async logout() { try { sessionStorage.removeItem(DEMO_SESSION) } catch { /* Memory-only sign-in. */ } },
    }
  }
  const [{ createAuth, SignInRequired }, { createApiClient }, { createApiService }] = await Promise.all([import('./auth'), import('./api-client'), import('./api-service')])
  const auth = await createAuth(config)
  const apiConfig = config
  let client: ReturnType<typeof createApiClient>
  const expire = () => { client.clearSession(); runtime.signedIn = false; runtime.message = '再度Microsoftでログインしてください。'; runtime.onSessionExpired?.() }
  function connect() {
    const prefix = `roll-and-pay.api.v1:${apiConfig.baseUrl}:${apiConfig.clientId}:${auth.accountId() ?? 'signed-out'}`
    client = createApiClient({ baseUrl: apiConfig.baseUrl, session: { storage: sessionStorage, key: `${prefix}:read-session` }, onUnauthorized: expire,
      getAccessToken: async force => { try { return await auth.token(force) } catch (error) { if (error instanceof SignInRequired) expire(); throw error } },
    })
    return createApiService(client, sessionStorage, `${prefix}:pending-create`)
  }
  async function authorize() {
    const { data } = await client.request<{ authorized: boolean; role: string }>('me')
    if (data.authorized !== true || data.role !== 'administrator') throw new Error('このアカウントでは利用できません。')
  }
  const runtime: AppRuntime = {
    mode: 'api', service: connect(), signedIn: false,
    async login() {
      client.clearSession()
      await auth.login()
      runtime.service = connect()
      await authorize()
      runtime.signedIn = true; runtime.message = undefined
    },
    async logout() { client.clearSession(); runtime.signedIn = false; await auth.logout() },
  }
  if (auth.accountId()) {
    try { await authorize(); runtime.signedIn = true }
    catch (error) { runtime.message = error instanceof Error ? error.message : 'ログイン状態を確認できませんでした。' }
  }
  return runtime
}
