// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRuntime } from './runtime'
import type { ApiConfig } from '../config'

const auth = vi.hoisted(() => ({ accountId: vi.fn(), login: vi.fn(), token: vi.fn(), logout: vi.fn() }))
vi.mock('./auth', () => ({ createAuth: async () => auth, SignInRequired: class extends Error {} }))
const config: ApiConfig = { mode: 'api', baseUrl: 'https://api.example.test/api/v1/', clientId: 'spa-client', tenantId: 'tenant', scope: 'api://api/access_as_user' }
beforeEach(() => { sessionStorage.clear(); localStorage.clear(); vi.resetAllMocks(); auth.accountId.mockReturnValue('account-a'); auth.token.mockResolvedValue('access-token') })
afterEach(() => vi.unstubAllGlobals())
describe('APIログインと権限確認', () => {
  it('再読み込み後も/meを確認してからホームへのアクセスを許可する', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('{"authorized":true,"role":"administrator"}'))
    vi.stubGlobal('fetch', fetcher)
    const runtime = await createRuntime(config)
    expect(runtime.signedIn).toBe(true)
    expect(String(fetcher.mock.calls[0][0])).toBe(`${config.baseUrl}me`)
    expect(new Headers(fetcher.mock.calls[0][1]!.headers).get('Authorization')).toBe('Bearer access-token')
    expect(localStorage.length).toBe(0)
  })
  it('403ではログイン扱いにせず、別アカウントでの再ログイン後に再確認する', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{}', { status: 403 })).mockResolvedValueOnce(new Response('{"authorized":true,"role":"administrator"}'))
    vi.stubGlobal('fetch', fetcher)
    const runtime = await createRuntime(config)
    expect(runtime.signedIn).toBe(false)
    expect(runtime.message).toContain('利用できません')
    auth.accountId.mockReturnValue('account-b')
    await runtime.login()
    expect(auth.login).toHaveBeenCalledOnce()
    expect(runtime.signedIn).toBe(true)
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
  it('ログアウト時に読取セッションを消し、別アカウントへ引き継がない', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{"authorized":true,"role":"administrator"}', { headers: { 'X-Read-Session': 'session-a' } })).mockResolvedValueOnce(new Response('{"authorized":true,"role":"administrator"}'))
    vi.stubGlobal('fetch', fetcher)
    const runtime = await createRuntime(config)
    expect([...Array(sessionStorage.length)].some((_, i) => sessionStorage.getItem(sessionStorage.key(i)!) === 'session-a')).toBe(true)
    await runtime.logout()
    expect(sessionStorage.length).toBe(0)
    auth.accountId.mockReturnValue('account-b')
    await runtime.login()
    expect(new Headers(fetcher.mock.calls[1][1]!.headers).get('X-Read-Session')).toBeNull()
  })
  it('デモセッションがあってもMicrosoft認証を省略しない', async () => {
    sessionStorage.setItem('roll-and-pay.demo.session', 'active')
    auth.accountId.mockReturnValue(null)
    const fetcher = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', fetcher)
    const runtime = await createRuntime(config)
    expect(runtime.signedIn).toBe(false)
    expect(fetcher).not.toHaveBeenCalled()
    expect(localStorage.length).toBe(0)
  })
})
