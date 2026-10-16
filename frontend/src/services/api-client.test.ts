import { describe, expect, it, vi } from 'vitest'
import { ApiError, createApiClient } from './api-client'
import { memoryStorage } from './api-fixtures.test-helper'

describe('API通信の土台', () => {
  it('401のとき一度だけトークンを再取得し、同じ更新要求を送る', async () => {
    const token = vi.fn().mockResolvedValueOnce('old').mockResolvedValueOnce('fresh')
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{}', { status: 401 })).mockResolvedValueOnce(new Response('{}'))
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: token, fetch: fetcher })
    await client.request('games', { method: 'POST', body: { participantIds: ['p001', 'p002'] }, idempotencyKey: 'same-key' })
    expect(token.mock.calls).toEqual([[], [true]])
    expect(fetcher.mock.calls[1][1]!.body).toBe(fetcher.mock.calls[0][1]!.body)
    expect(new Headers(fetcher.mock.calls[1][1]!.headers).get('Idempotency-Key')).toBe('same-key')
    expect(new Headers(fetcher.mock.calls[1][1]!.headers).get('Authorization')).toBe('Bearer fresh')
  })
  it('401が続く場合は再ログインを案内し、無限再送しない', async () => {
    const unauthorized = vi.fn(), fetcher = vi.fn<typeof fetch>().mockImplementation(async () => new Response('{}', { status: 401 }))
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', onUnauthorized: unauthorized, fetch: fetcher })
    await expect(client.request('me')).rejects.toMatchObject({ status: 401 })
    expect(fetcher).toHaveBeenCalledTimes(2); expect(unauthorized).toHaveBeenCalledOnce()
  })
  it('読取を直列にし、再読み込み後も保存した読取セッションを引き継ぐ', async () => {
    const storage = memoryStorage()
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{}', { headers: { 'X-Read-Session': 'first' } })).mockResolvedValueOnce(new Response('{}', { headers: { 'X-Read-Session': 'second' } })).mockResolvedValueOnce(new Response('{}'))
    const options = { baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: fetcher, session: { storage, key: 'account-session' } }
    const client = createApiClient(options)
    await Promise.all([client.request('participants'), client.request('games/active')])
    expect(new Headers(fetcher.mock.calls[1][1]!.headers).get('X-Read-Session')).toBe('first')
    const restored = createApiClient(options)
    await restored.request('participants')
    expect(new Headers(fetcher.mock.calls[2][1]!.headers).get('X-Read-Session')).toBe('second')
    restored.clearSession()
    expect(storage.getItem('account-session')).toBeNull()
  })
  it('ログアウト中の応答が保存セッションを復活させない', async () => {
    const storage = memoryStorage()
    let complete!: (value: Response) => void
    const fetcher = vi.fn<typeof fetch>().mockImplementation(() => new Promise(resolve => { complete = resolve }))
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: fetcher, session: { storage, key: 'session' } })
    const request = client.request('participants')
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
    client.clearSession(); complete(new Response('{}', { headers: { 'X-Read-Session': 'stale' } }))
    await expect(request).rejects.toThrow('ログイン状態')
    expect(storage.getItem('session')).toBeNull()
  })
  it('アクセストークン・冪等性キー・ETagを渡し、読取セッションを次要求へ継承', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{"ok":true}', { headers: { ETag: '"v1"', 'X-Read-Session': 'read-session' } })).mockResolvedValueOnce(new Response('{}'))
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'access-token', fetch: fetcher })
    const result = await client.request('games', { method: 'POST', body: { participantIds: ['p001', 'p002'] }, idempotencyKey: 'operation-key' })
    expect(result.etag).toBe('"v1"')
    await client.request('games/id', { method: 'DELETE', idempotencyKey: 'delete-key', ifMatch: result.etag! })
    const init = fetcher.mock.calls[1][1]!
    const headers = new Headers(init.headers)
    expect(headers.get('Authorization')).toBe('Bearer access-token')
    expect(headers.get('X-Read-Session')).toBe('read-session')
    expect(headers.get('If-Match')).toBe('"v1"')
    expect(headers.get('Idempotency-Key')).toBe('delete-key')
    expect(init.body).toBeUndefined()
    expect(init.redirect).toBe('error')
  })
  it('削除済みエラーをHTTP statusとcodeで返す', async () => {
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: vi.fn<typeof fetch>().mockResolvedValue(new Response('{"error":{"code":"GAME_DELETED"}}', { status: 410 })) })
    await expect(client.request('games/deleted')).rejects.toMatchObject({ status: 410, code: 'GAME_DELETED' } satisfies Partial<ApiError>)
  })
  it('応答断で更新要求を勝手に再送しない', async () => {
    const fetcher = vi.fn<typeof fetch>().mockRejectedValue(new TypeError('network'))
    const client = createApiClient({ baseUrl: 'http://localhost:7071/api/v1', getAccessToken: async () => 'token', fetch: fetcher })
    await expect(client.request('games', { method: 'POST', body: {}, idempotencyKey: 'key' })).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it('他オリジンやAPIの範囲外へトークンを送信しない', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: fetcher })
    await expect(client.request('https://other.example.test')).rejects.toThrow()
    await expect(client.request('../outside')).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(() => createApiClient({ baseUrl: 'http://api.example.test', getAccessToken: async () => 'token' })).toThrow()
  })
})
